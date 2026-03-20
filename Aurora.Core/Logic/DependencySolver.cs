using System;
using System.Collections.Generic;
using System.Linq;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly HashSet<string> _installedNevras;
    private readonly HashSet<string> _installedNames;
    private readonly Dictionary<string, List<(Package Pkg, string ProvString)>> _providersMap;

    public DependencySolver(IEnumerable<Package> availablePackages, IEnumerable<Package> installedPackages)
    {
        var installedList = installedPackages.ToList();
        
        _installedNevras = installedList.Select(p => p.Nevra).ToHashSet();
        _installedNames = installedList.Select(p => p.Name).ToHashSet();
        
        _providersMap = new Dictionary<string, List<(Package, string)>>(100000);

        var allPackages = availablePackages.Concat(installedList).DistinctBy(p => p.Nevra);

        foreach (var pkg in allPackages)
        {
            AddProvider(pkg.Name, pkg, pkg.Name);

            foreach (var prov in pkg.Provides)
            {
                // Rely on the fixed scanner to correctly isolate "lua(abi)" from "lua(abi)=5.1"
                string provName;
                int opIdx = -1;
                for (int i = 0; i < prov.Length; i++)
                {
                    char c = prov[i];
                    if (c == '>' || c == '<' || c == '=' || c == '!') { opIdx = i; break; }
                }
                
                if (opIdx != -1) provName = prov.Substring(0, opIdx).TrimEnd();
                else 
                {
                    int sp = prov.IndexOf(' ');
                    provName = sp > 0 ? prov.Substring(0, sp) : prov;
                }
                
                AddProvider(provName, pkg, prov);

                if (provName.StartsWith('/'))
                {
                    if (provName.StartsWith("/usr/bin/")) AddProvider(string.Concat("/bin/", provName.AsSpan(9)), pkg, prov);
                    else if (provName.StartsWith("/bin/")) AddProvider(string.Concat("/usr/bin/", provName.AsSpan(5)), pkg, prov);
                    else if (provName.StartsWith("/usr/sbin/")) AddProvider(string.Concat("/sbin/", provName.AsSpan(10)), pkg, prov);
                    else if (provName.StartsWith("/sbin/")) AddProvider(string.Concat("/usr/sbin/", provName.AsSpan(6)), pkg, prov);
                }
            }
        }

        foreach (var list in _providersMap.Values)
        {
            if (list.Count > 1)
            {
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
                    AnsiConsole.MarkupLine($"[grey]Bypassing virtual identity:[/] {lookupName}[grey](Handled by sysusers)[/]");
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
            
            // --- DEBUG START ---
            // We only print for interesting requirements to avoid spam
            bool debug = currentReqStr.Contains("lua") || currentReqStr.Contains("libuv") || lookupName.Contains("abi");
            if (debug) 
            {
                AnsiConsole.MarkupLine($"[bold blue]DEBUG:[/] Resolving: [yellow]{currentReqStr}[/]");
                if (requester != null) AnsiConsole.MarkupLine($"      Requested by: [grey]{requester.Nevra}[/]");
            }
            // --- DEBUG END ---

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

            // --- CRITICAL FIX: Only short-circuit if the package is physically installed on the OS ---
            // Relying on plan-matches caused sub-optimal virtual providers to be chosen
            bool installedMet = false;
            for (int i = 0; i < validCandidates.Count; i++)
            {
                if (_installedNevras.Contains(validCandidates[i].Pkg.Nevra))
                {
                    installedMet = true;
                    break;
                }
            }
            if (installedMet) continue; 

            var chosenPkg = PickBestCandidate(lookupName, validCandidates);

            bool shouldEnqueueChildren = false;
            if (!plan.TryGetValue(chosenPkg.Name, out var existingPkg))
            {
                plan[chosenPkg.Name] = chosenPkg;
                shouldEnqueueChildren = true;
            }
            else if (existingPkg.Nevra != chosenPkg.Nevra)
            {
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
                        var clean = childReqStr.AsSpan().Trim("()");
                        int ifIdx = clean.IndexOf(" if ");
                        
                        if (ifIdx != -1)
                        {
                            var reqPart = clean.Slice(0, ifIdx).Trim();
                            string conditionPkgName = clean.Slice(ifIdx + 4).Trim().ToString();

                            bool conditionMet = false;
                            foreach (var p in plan.Values) { if (p.Name == conditionPkgName) { conditionMet = true; break; } }
                            if (!conditionMet && _installedNames.Contains(conditionPkgName)) conditionMet = true;

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

        string baseName = reqName;
        int parenIdx = baseName.IndexOf('(');
        if (parenIdx > 0) baseName = baseName.Substring(0, parenIdx);

        // Score based approach ensuring matching base names + highest version wins ties
        // This guarantees `lua5.1` beats `luajit` for `lua(abi)`
        var sorted = candidates.OrderByDescending(c => c.Pkg.Name.StartsWith(baseName) ? 2 : (c.Pkg.Name.Contains(baseName) ? 1 : 0))
            .ThenBy(c => FuzzyMatcher.LevenshteinDistance(baseName, c.Pkg.Name))
            .ThenBy(c => c.Pkg.Name.Length)
            .ThenByDescending(c => c.Pkg.FullVersion, new VersionComparer())
            .ToList();

        return sorted[0].Pkg;
    }

    private List<Package> TopologicalSort(IEnumerable<Package> unsorted)
    {
        var unsortedList = unsorted.ToList();
        var result = new List<Package>(unsortedList.Count);
        var visited = new HashSet<string>();
        var tempMark = new HashSet<string>(); 

        var planProviders = new Dictionary<string, Package>();
        
        // Pass 1: Map explicit package names first so they take absolute priority over virtual provides
        foreach (var pkg in unsortedList)
        {
            planProviders[pkg.Name] = pkg;
        }

        // Pass 2: Map virtual provides and path aliases safely
        foreach (var pkg in unsortedList)
        {
            foreach (var pr in pkg.Provides)
            {
                // Safely extract the provide name ignoring version operators
                int opIdx = -1;
                for (int i = 0; i < pr.Length; i++)
                {
                    char c = pr[i];
                    if (c == '>' || c == '<' || c == '=' || c == '!') 
                    { 
                        opIdx = i; 
                        break; 
                    }
                }
                
                string n;
                if (opIdx != -1)
                {
                    n = pr.Substring(0, opIdx).TrimEnd();
                }
                else
                {
                    int spaceIdx = pr.IndexOf(' ');
                    n = spaceIdx > 0 ? pr.Substring(0, spaceIdx) : pr;
                }
                
                if (!planProviders.ContainsKey(n)) planProviders[n] = pkg;
                
                // UsrMerge path aliasing
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
            if (tempMark.Contains(pkg.Name)) return; // Prevent circular dependency stack overflow

            tempMark.Add(pkg.Name);

            foreach (var reqStr in pkg.Requires)
            {
                if (reqStr.StartsWith("rpmlib(")) continue;

                // Let the robust RpmRequirement class extract the name reliably 
                // so we don't accidentally try to lookup "lua(abi)=5.1" instead of "lua(abi)"
                var reqName = new RpmRequirement(reqStr).Name;

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

        // Initiate Deep First Search for topological sorting
        foreach (var pkg in unsortedList)
        {
            if (!visited.Contains(pkg.Name)) Visit(pkg);
        }

        return result;
    }
}