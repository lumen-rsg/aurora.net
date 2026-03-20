using System;
using System.Collections.Generic;
using System.Linq;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly HashSet<Package> _installedPackagesSet;
    private readonly Dictionary<string, List<(Package Pkg, string ProvString)>> _providersMap;

    public DependencySolver(IEnumerable<Package> availablePackages, IEnumerable<Package> installedPackages)
    {
        var installedList = installedPackages.ToList();
        _installedPackagesSet = installedList.ToHashSet();
        
        _providersMap = new Dictionary<string, List<(Package, string)>>(100000);

        var allPackages = availablePackages.Concat(installedList).DistinctBy(p => p.Nevra);

        foreach (var pkg in allPackages)
        {
            AddProvider(pkg.Name, pkg, pkg.Name);

            foreach (var prov in pkg.Provides)
            {
                int spaceIdx = prov.IndexOf(' ');
                string provName = spaceIdx > 0 ? prov.Substring(0, spaceIdx) : prov;
                
                AddProvider(provName, pkg, prov);

                if (provName.StartsWith('/'))
                {
                    if (provName.StartsWith("/usr/bin/")) 
                        AddProvider(string.Concat("/bin/", provName.AsSpan(9)), pkg, prov);
                    else if (provName.StartsWith("/bin/")) 
                        AddProvider(string.Concat("/usr/bin/", provName.AsSpan(5)), pkg, prov);
                    else if (provName.StartsWith("/usr/sbin/")) 
                        AddProvider(string.Concat("/sbin/", provName.AsSpan(10)), pkg, prov);
                    else if (provName.StartsWith("/sbin/")) 
                        AddProvider(string.Concat("/usr/sbin/", provName.AsSpan(6)), pkg, prov);
                }
            }
        }

        foreach (var list in _providersMap.Values)
        {
            if (list.Count > 1)
            {
                // --- CRITICAL FIX: Sort by FullVersion (not Version) so RPM releases (-10 vs -4) are evaluated properly ---
                list.Sort((a, b) => VersionComparer.Compare(b.Pkg.FullVersion, a.Pkg.FullVersion));
            }
        }
    }

    private void AddProvider(string name, Package pkg, string provString)
    {
        if (!_providersMap.TryGetValue(name, out var list))
        {
            list = new List<(Package, string)>();
            _providersMap[name] = list;
        }
            
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Pkg.Nevra == pkg.Nevra) return;
        }
        
        list.Add((pkg, provString));
    }

    private string? FindPathAlias(string requestedPath)
    {
        int lastSlash = requestedPath.LastIndexOf('/');
        if (lastSlash == -1) return null;

        var fileName = requestedPath.Substring(lastSlash + 1);

        string p1 = $"/usr/bin/{fileName}";
        if (_providersMap.ContainsKey(p1)) return p1;

        string p2 = $"/usr/sbin/{fileName}";
        if (_providersMap.ContainsKey(p2)) return p2;

        string p3 = $"/bin/{fileName}";
        if (_providersMap.ContainsKey(p3)) return p3;

        string p4 = $"/sbin/{fileName}";
        if (_providersMap.ContainsKey(p4)) return p4;

        return null;
    }

    public List<Package> Resolve(IEnumerable<string> targets)
    {
        // --- CRITICAL FIX: Dictionary prevents HashSet from injecting mixed releases ---
        var plan = new Dictionary<string, Package>();
        var queue = new Queue<(string ReqStr, Package? Requester)>();
        var processedReqs = new HashSet<string>();

        foreach (var t in targets) queue.Enqueue((t, null));

        while (queue.Count > 0)
        {
            var (currentReqStr, requester) = queue.Dequeue();
            
            if (!processedReqs.Add(currentReqStr)) continue;

            var req = new RpmRequirement(currentReqStr);
            string lookupName = req.Name;
            
            if (lookupName.StartsWith('/') && !_providersMap.ContainsKey(lookupName))
            {
                var alias = FindPathAlias(lookupName);
                if (alias != null) lookupName = alias;
            }

            if (!_providersMap.TryGetValue(lookupName, out var candidates))
            {
                if (lookupName.StartsWith("user(") || lookupName.StartsWith("group("))
                {
                    AnsiConsole.MarkupLine($"[grey]Bypassing virtual identity:[/] {lookupName} [grey](Handled by sysusers)[/]");
                    continue;
                }

                string requesterInfo = requester != null ? $" (required by [bold]{requester.Name}[/])" : "";
                var suggestions = _providersMap.Keys
                    .Select(k => new { Name = k, Distance = FuzzyMatcher.LevenshteinDistance(lookupName, k) })
                    .OrderBy(x => x.Distance).Take(5).ToList();

                var msg = $"Unresolvable dependency: '{currentReqStr}'{requesterInfo}. No package provides '{lookupName}'.";
                if (suggestions.Count > 0)
                {
                    msg += "\nDid you mean one of these?";
                    foreach (var s in suggestions) msg += $"\n  - {s.Name} (dist: {s.Distance})";
                }
                
                throw new Exception(msg);
            }

            var validCandidates = new List<(Package Pkg, string ProvStr)>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (req.IsSatisfiedBy(c.Pkg, c.ProvString))
                {
                    validCandidates.Add(c);
                }
            }

            if (validCandidates.Count == 0)
            {
                string reqInfo = requester != null ? $" (required by {requester.Name})" : "";
                throw new Exception($"Version conflict for '{currentReqStr}'{reqInfo}.");
            }

            bool alreadyMet = false;
            for (int i = 0; i < validCandidates.Count; i++)
            {
                var c = validCandidates[i];
                if (_installedPackagesSet.Contains(c.Pkg))
                {
                    alreadyMet = true;
                    break;
                }
                if (plan.TryGetValue(c.Pkg.Name, out var plannedPkg) && plannedPkg.Nevra == c.Pkg.Nevra)
                {
                    alreadyMet = true;
                    break;
                }
            }
            if (alreadyMet) continue; 

            var chosenPkg = PickBestCandidate(lookupName, validCandidates);

            // Add or Upgrade in Plan collision detection
            bool shouldEnqueueChildren = false;
            if (!plan.TryGetValue(chosenPkg.Name, out var existingPkg))
            {
                plan[chosenPkg.Name] = chosenPkg;
                shouldEnqueueChildren = true;
            }
            else if (existingPkg.Nevra != chosenPkg.Nevra)
            {
                // Check if existing locked package happens to natively satisfy the new requirement
                bool existingSatisfies = req.IsSatisfiedBy(existingPkg, existingPkg.Name);
                if (!existingSatisfies)
                {
                    for (int i = 0; i < existingPkg.Provides.Count; i++)
                    {
                        if (req.IsSatisfiedBy(existingPkg, existingPkg.Provides[i])) { existingSatisfies = true; break; }
                    }
                }

                if (!existingSatisfies)
                {
                    if (VersionComparer.Compare(chosenPkg.FullVersion, existingPkg.FullVersion) > 0)
                    {
                        plan[chosenPkg.Name] = chosenPkg;
                        shouldEnqueueChildren = true;
                    }
                    else
                    {
                        string reqInfo = requester != null ? $" (required by {requester.Name})" : "";
                        throw new Exception($"Dependency conflict: '{currentReqStr}'{reqInfo} requires {chosenPkg.Name} {chosenPkg.FullVersion}, but {existingPkg.FullVersion} is locked in the plan.");
                    }
                }
            }

            if (shouldEnqueueChildren)
            {
                foreach (var childReqStr in chosenPkg.Requires)
                {
                    if (childReqStr.StartsWith("rpmlib(")) continue;

                    if (childReqStr.StartsWith('(') && childReqStr.Contains(" if "))
                    {
                        // Fixed string array trim
                        var clean = childReqStr.AsSpan().Trim("()");
                        int ifIdx = clean.IndexOf(" if ");
                        
                        if (ifIdx != -1)
                        {
                            var reqPart = clean.Slice(0, ifIdx).Trim();
                            string conditionPkgName = clean.Slice(ifIdx + 4).Trim().ToString();

                            bool conditionMet = false;
                            foreach (var p in plan.Values) { if (p.Name == conditionPkgName) { conditionMet = true; break; } }
                            if (!conditionMet)
                            {
                                foreach (var p in _installedPackagesSet) { if (p.Name == conditionPkgName) { conditionMet = true; break; } }
                            }

                            if (!conditionMet)
                            {
                                AnsiConsole.MarkupLine($"[grey]Bypassing conditional:[/] {childReqStr} [grey](Condition '{conditionPkgName}' not met)[/]");
                                continue;
                            }
                            else
                            {
                                queue.Enqueue((reqPart.ToString(), chosenPkg));
                                continue;
                            }
                        }
                    }

                    queue.Enqueue((childReqStr, chosenPkg));
                }
            }
        }

        return TopologicalSort(plan.Values);
    }

    private Package PickBestCandidate(string reqName, List<(Package Pkg, string ProvStr)> candidates)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Pkg.Name == reqName)
                return candidates[i].Pkg;
        }

        var shortest = candidates[0].Pkg;
        for (int i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].Pkg.Name.Length < shortest.Name.Length)
            {
                shortest = candidates[i].Pkg;
            }
        }
        
        return shortest;
    }

    private List<Package> TopologicalSort(IEnumerable<Package> unsorted)
    {
        var unsortedList = unsorted.ToList();
        var result = new List<Package>(unsortedList.Count);
        var visited = new HashSet<string>();
        var tempMark = new HashSet<string>(); 

        var planProviders = new Dictionary<string, Package>();
        
        // --- CRITICAL FIX: Pass 1 maps literal package names ---
        foreach (var pkg in unsortedList)
        {
            planProviders[pkg.Name] = pkg;
        }

        // --- CRITICAL FIX: Pass 2 maps virtual provides, protecting literal names ---
        foreach (var pkg in unsortedList)
        {
            foreach (var pr in pkg.Provides)
            {
                int spaceIdx = pr.IndexOf(' ');
                string n = spaceIdx > 0 ? pr.Substring(0, spaceIdx) : pr;
                
                if (!planProviders.ContainsKey(n)) planProviders[n] = pkg;
                
                if (n.StartsWith('/'))
                {
                    if (n.StartsWith("/usr/bin/")) 
                    {
                        string p = string.Concat("/bin/", n.AsSpan(9));
                        if (!planProviders.ContainsKey(p)) planProviders[p] = pkg;
                    }
                    else if (n.StartsWith("/bin/")) 
                    {
                        string p = string.Concat("/usr/bin/", n.AsSpan(5));
                        if (!planProviders.ContainsKey(p)) planProviders[p] = pkg;
                    }
                    else if (n.StartsWith("/usr/sbin/")) 
                    {
                        string p = string.Concat("/sbin/", n.AsSpan(10));
                        if (!planProviders.ContainsKey(p)) planProviders[p] = pkg;
                    }
                    else if (n.StartsWith("/sbin/")) 
                    {
                        string p = string.Concat("/usr/sbin/", n.AsSpan(6));
                        if (!planProviders.ContainsKey(p)) planProviders[p] = pkg;
                    }
                }
            }
        }

        void Visit(Package pkg)
        {
            if (visited.Contains(pkg.Name)) return;
            if (tempMark.Contains(pkg.Name)) return; 

            tempMark.Add(pkg.Name);

            foreach (var reqStr in pkg.Requires)
            {
                if (reqStr.StartsWith("rpmlib(")) continue;

                string reqName;
                if (reqStr.StartsWith('(') || reqStr.Contains(' '))
                {
                    var span = reqStr.AsSpan().Trim();
                    if (span.StartsWith('(')) span = span.Slice(1, span.Length - 2).Trim();
                    int sp = span.IndexOf(' ');
                    if (sp > 0) span = span.Slice(0, sp);
                    reqName = span.ToString();
                }
                else
                {
                    reqName = reqStr;
                }

                if (planProviders.TryGetValue(reqName, out var providerInPlan))
                {
                    Visit(providerInPlan);
                }
                else if (reqName.StartsWith('/'))
                {
                    int lastSlash = reqName.LastIndexOf('/');
                    if (lastSlash != -1)
                    {
                        string fileName = reqName.Substring(lastSlash + 1);
                        if (planProviders.TryGetValue($"/usr/bin/{fileName}", out providerInPlan) ||
                            planProviders.TryGetValue($"/bin/{fileName}", out providerInPlan) ||
                            planProviders.TryGetValue($"/usr/sbin/{fileName}", out providerInPlan) ||
                            planProviders.TryGetValue($"/sbin/{fileName}", out providerInPlan))
                        {
                            Visit(providerInPlan);
                        }
                    }
                }
            }

            tempMark.Remove(pkg.Name);
            visited.Add(pkg.Name);
            result.Add(pkg);
        }

        foreach (var pkg in unsortedList)
        {
            if (!visited.Contains(pkg.Name)) Visit(pkg);
        }

        return result;
    }
}