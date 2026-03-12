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
                var provName = prov.Split(' ')[0]; 
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

    // --- NEW: Universal UsrMerge Path Normalizer ---
    private string? FindPathAlias(string requestedPath)
    {
        // Extract the filename (e.g., "alternatives")
        string fileName = requestedPath.Split('/').Last();

        var possiblePaths = new[]
        {
            $"/usr/bin/{fileName}",
            $"/usr/sbin/{fileName}",
            $"/bin/{fileName}",
            $"/sbin/{fileName}"
        };

        foreach (var path in possiblePaths)
        {
            if (_providersMap.ContainsKey(path))
            {
                return path;
            }
        }
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

            if (IsSatisfiedByList(req, _installedPackages)) continue;
            if (IsSatisfiedByList(req, plan)) continue;

            string lookupName = req.Name;
            
            // Apply universal path normalization if it looks like an absolute path
            if (lookupName.StartsWith("/") && !_providersMap.ContainsKey(lookupName))
            {
                var alias = FindPathAlias(lookupName);
                if (alias != null)
                {
                    lookupName = alias;
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
                
                // Universal normalization for satisfaction checking
                bool match = provReq.Name == req.Name;
                if (!match && req.Name.StartsWith("/") && provReq.Name.StartsWith("/"))
                {
                    string reqFile = req.Name.Split('/').Last();
                    string provFile = provReq.Name.Split('/').Last();
                    match = (reqFile == provFile);
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
                        if (n == reqName) return true;
                        
                        if (n.StartsWith("/") && reqName.StartsWith("/"))
                        {
                            return n.Split('/').Last() == reqName.Split('/').Last();
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