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
        
        // Use a large initial capacity to prevent costly re-hashing
        _providersMap = new Dictionary<string, List<(Package, string)>>(100000);

        // Process all packages (available + installed) to build a universal lookup map
        var allPackages = availablePackages.Concat(installedList).DistinctBy(p => p.Nevra);

        foreach (var pkg in allPackages)
        {
            AddProvider(pkg.Name, pkg, pkg.Name);

            foreach (var prov in pkg.Provides)
            {
                int spaceIdx = prov.IndexOf(' ');
                string provName = spaceIdx > 0 ? prov.Substring(0, spaceIdx) : prov;
                
                AddProvider(provName, pkg, prov);

                // --- UsrMerge Indexing ---
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

        // In-place sorting reduces list allocations
        foreach (var list in _providersMap.Values)
        {
            if (list.Count > 1)
            {
                list.Sort((a, b) => VersionComparer.Compare(b.Pkg.Version, a.Pkg.Version));
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
            
        // Prevent duplicate provisions by the exact same package
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
        var plan = new HashSet<Package>();
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
                if (alias != null)
                {
                    lookupName = alias;
                }
            }

            if (!_providersMap.TryGetValue(lookupName, out var candidates))
            {
                if (lookupName.StartsWith("user(") || lookupName.StartsWith("group("))
                {
                    AnsiConsole.MarkupLine($"[grey]Bypassing virtual identity:[/] {lookupName} [grey](Handled by sysusers)[/]");
                    continue;
                }

                string requesterInfo = requester != null ? $" (required by[bold]{requester.Name}[/])" : "";
                
                var suggestions = _providersMap.Keys
                    .Select(k => new { Name = k, Distance = FuzzyMatcher.LevenshteinDistance(lookupName, k) })
                    .OrderBy(x => x.Distance)
                    .Take(5)
                    .ToList();

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

            // O(1) Check: Has this requirement already been satisfied by an installed package or a package inside our plan?
            bool alreadyMet = false;
            for (int i = 0; i < validCandidates.Count; i++)
            {
                var c = validCandidates[i];
                if (_installedPackagesSet.Contains(c.Pkg) || plan.Contains(c.Pkg))
                {
                    alreadyMet = true;
                    break;
                }
            }
            if (alreadyMet) continue; // Bypass adding new packages if a match naturally exists

            var chosenPkg = PickBestCandidate(lookupName, validCandidates);

            if (plan.Add(chosenPkg))
            {
                foreach (var childReqStr in chosenPkg.Requires)
                {
                    if (childReqStr.StartsWith("rpmlib(")) continue;

                    if (childReqStr.StartsWith('(') && childReqStr.Contains(" if "))
                    {
                        var clean = childReqStr.AsSpan().Trim(['(', ')']);
                        int ifIdx = clean.IndexOf(" if ");
                        
                        if (ifIdx != -1)
                        {
                            var reqPart = clean.Slice(0, ifIdx).Trim();
                            string conditionPkgName = clean.Slice(ifIdx + 4).Trim().ToString();

                            bool conditionMet = false;
                            foreach (var p in plan) { if (p.Name == conditionPkgName) { conditionMet = true; break; } }
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

        return TopologicalSort(plan);
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

    private List<Package> TopologicalSort(HashSet<Package> unsorted)
    {
        var result = new List<Package>(unsorted.Count);
        var visited = new HashSet<string>();
        var tempMark = new HashSet<string>(); 

        // Fast O(1) Dictionary lookup to skip repetitive linear scanning during iteration
        var planProviders = new Dictionary<string, Package>();
        foreach (var pkg in unsorted)
        {
            planProviders[pkg.Name] = pkg;
            foreach (var pr in pkg.Provides)
            {
                int spaceIdx = pr.IndexOf(' ');
                string n = spaceIdx > 0 ? pr.Substring(0, spaceIdx) : pr;
                planProviders[n] = pkg;
                
                if (n.StartsWith('/'))
                {
                    if (n.StartsWith("/usr/bin/")) planProviders[string.Concat("/bin/", n.AsSpan(9))] = pkg;
                    else if (n.StartsWith("/bin/")) planProviders[string.Concat("/usr/bin/", n.AsSpan(5))] = pkg;
                    else if (n.StartsWith("/usr/sbin/")) planProviders[string.Concat("/sbin/", n.AsSpan(10))] = pkg;
                    else if (n.StartsWith("/sbin/")) planProviders[string.Concat("/usr/sbin/", n.AsSpan(6))] = pkg;
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

                // Span-based fast string parsing eliminates RpmRequirement instantiation
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

        foreach (var pkg in unsorted)
        {
            if (!visited.Contains(pkg.Name)) Visit(pkg);
        }

        return result;
    }
}