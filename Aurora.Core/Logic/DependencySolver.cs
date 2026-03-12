using System;
using System.Collections.Generic;
using System.Linq;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly List<Package> _installedPackages;
    private readonly Dictionary<string, List<(Package Pkg, string ProvString)>> _providersMap;

    public DependencySolver(IEnumerable<Package> availablePackages, IEnumerable<Package> installedPackages)
    {
        _installedPackages = installedPackages.ToList();
        _providersMap = new Dictionary<string, List<(Package, string)>>();

        foreach (var pkg in availablePackages)
        {
            AddProvider(pkg.Name, pkg, pkg.Name);

            foreach (var prov in pkg.Provides)
            {
                var provName = new RpmRequirement(prov).Name;
                AddProvider(provName, pkg, prov);
            }
        }

        foreach (var key in _providersMap.Keys.ToList())
        {
            _providersMap[key] = _providersMap[key]
                .OrderByDescending(x => x.Pkg.Version, new VersionComparer())
                .ToList();
        }
    }

    private void AddProvider(string name, Package pkg, string provString)
    {
        if (!_providersMap.ContainsKey(name))
            _providersMap[name] = new List<(Package, string)>();
            
        if (!_providersMap[name].Any(x => x.Pkg.Nevra == pkg.Nevra))
            _providersMap[name].Add((pkg, provString));
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

            if (IsSatisfiedByList(req, _installedPackages)) continue;
            if (IsSatisfiedByList(req, plan)) continue;

            // --- CRITICAL FIX: UsrMerge Normalization ---
            // If the strict name isn't found, try its alias.
            string lookupName = req.Name;
            
            if (!_providersMap.ContainsKey(lookupName))
            {
                if (lookupName.StartsWith("/usr/bin/"))
                {
                    string alias = lookupName.Replace("/usr/bin/", "/bin/");
                    if (_providersMap.ContainsKey(alias)) lookupName = alias;
                }
                else if (lookupName.StartsWith("/bin/"))
                {
                    string alias = lookupName.Replace("/bin/", "/usr/bin/");
                    if (_providersMap.ContainsKey(alias)) lookupName = alias;
                }
                else if (lookupName.StartsWith("/usr/sbin/"))
                {
                    string alias = lookupName.Replace("/usr/sbin/", "/sbin/");
                    if (_providersMap.ContainsKey(alias)) lookupName = alias;
                }
                else if (lookupName.StartsWith("/sbin/"))
                {
                    string alias = lookupName.Replace("/sbin/", "/usr/sbin/");
                    if (_providersMap.ContainsKey(alias)) lookupName = alias;
                }
            }

            if (!_providersMap.TryGetValue(lookupName, out var candidates))
            {
                if (req.Name.StartsWith("user(") || req.Name.StartsWith("group("))
                {
                    AnsiConsole.MarkupLine($"[grey]Bypassing virtual identity:[/] {req.Name} [grey](Handled by sysusers)[/]");
                    continue;
                }

                string requesterInfo = requester != null ? $" (required by [bold]{requester.Name}[/])" : "";
                
                var suggestions = _providersMap.Keys
                    .Select(k => new { Name = k, Distance = FuzzyMatcher.LevenshteinDistance(req.Name, k) })
                    .OrderBy(x => x.Distance)
                    .Take(5)
                    .ToList();

                var msg = $"Unresolvable dependency: '{currentReqStr}'{requesterInfo}. No package provides '{req.Name}'.";
                if (suggestions.Any())
                {
                    msg += "\nDid you mean one of these?";
                    foreach (var s in suggestions) msg += $"\n  - {s.Name} (dist: {s.Distance})";
                }
                
                throw new Exception(msg);
            }

            // Notice we use the original req but pass the validCandidates found via the alias lookup
            var validCandidates = candidates.Where(c => req.IsSatisfiedBy(c.Pkg, c.ProvString)).ToList();

            if (validCandidates.Count == 0)
            {
                string reqInfo = requester != null ? $" (required by {requester.Name})" : "";
                throw new Exception($"Version conflict for '{currentReqStr}'{reqInfo}.");
            }

            var chosenPkg = PickBestCandidate(lookupName, validCandidates);

            if (plan.Add(chosenPkg))
            {
                foreach (var childReq in chosenPkg.Requires)
                {
                    if (childReq.StartsWith("rpmlib(")) continue;
                    // Ignore self-references to prevent infinite trivial loops
                    if (new RpmRequirement(childReq).Name == chosenPkg.Name) continue;
                    
                    queue.Enqueue((childReq, chosenPkg));
                }
            }
        }

        return TopologicalSort(plan);
    }

    private Package PickBestCandidate(string reqName, List<(Package Pkg, string ProvStr)> candidates)
    {
        var exactMatch = candidates.FirstOrDefault(c => c.Pkg.Name == reqName);
        if (exactMatch.Pkg != null) return exactMatch.Pkg;

        var shortestName = candidates.OrderBy(c => c.Pkg.Name.Length).First();
        return shortestName.Pkg;
    }

    private bool IsSatisfiedByList(RpmRequirement req, IEnumerable<Package> packages)
    {
        foreach (var pkg in packages)
        {
            if (pkg.Name == req.Name && req.IsSatisfiedBy(pkg, pkg.Name)) return true;
            foreach (var prov in pkg.Provides)
            {
                var provReq = new RpmRequirement(prov);
                
                // UsrMerge normalization for satisfaction checks too
                bool match = provReq.Name == req.Name;
                if (!match && req.Name.StartsWith("/"))
                {
                     match = (provReq.Name.Replace("/usr/bin/", "/bin/") == req.Name.Replace("/usr/bin/", "/bin/")) ||
                             (provReq.Name.Replace("/usr/sbin/", "/sbin/") == req.Name.Replace("/usr/sbin/", "/sbin/"));
                }

                if (match && req.IsSatisfiedBy(pkg, prov)) return true;
            }
        }
        return false;
    }

    private List<Package> TopologicalSort(HashSet<Package> unsorted)
    {
        var result = new List<Package>();
        var visited = new HashSet<string>();
        var tempMark = new HashSet<string>(); 

        void Visit(Package pkg)
        {
            if (visited.Contains(pkg.Name)) return;
            if (tempMark.Contains(pkg.Name)) return; 

            tempMark.Add(pkg.Name);

            foreach (var reqStr in pkg.Requires)
            {
                var reqName = new RpmRequirement(reqStr).Name;
                var providerInPlan = unsorted.FirstOrDefault(p => 
                    p.Name == reqName || 
                    p.Provides.Any(pr => 
                    {
                        var n = new RpmRequirement(pr).Name;
                        // Handle topological sort aliasing
                        if (n == reqName) return true;
                        if (n.StartsWith("/") && reqName.StartsWith("/"))
                        {
                            return n.Replace("/usr/bin/", "/bin/") == reqName.Replace("/usr/bin/", "/bin/") ||
                                   n.Replace("/usr/sbin/", "/sbin/") == reqName.Replace("/usr/sbin/", "/sbin/");
                        }
                        return false;
                    })
                );

                if (providerInPlan != null)
                {
                    Visit(providerInPlan);
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