using System;
using System.Collections.Generic;
using System.Linq;
using Aurora.Core.Models;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly List<Package> _installedPackages;
    
    // Fast lookup dictionary: Capability Name -> List of Packages providing it
    // Sorted by version descending in the constructor
    private readonly Dictionary<string, List<(Package Pkg, string ProvString)>> _providersMap;

    public DependencySolver(IEnumerable<Package> availablePackages, IEnumerable<Package> installedPackages)
    {
        _installedPackages = installedPackages.ToList();
        _providersMap = new Dictionary<string, List<(Package, string)>>();

        // 1. Build the capability map
        foreach (var pkg in availablePackages)
        {
            // A package always implicitly provides its own name
            AddProvider(pkg.Name, pkg, pkg.Name);

            // Add explicit provides (virtual capabilities)
            foreach (var prov in pkg.Provides)
            {
                var provName = new RpmRequirement(prov).Name;
                AddProvider(provName, pkg, prov);
            }
        }

        // 2. Sort providers so the newest/best matches are evaluated first
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
        {
            _providersMap[name] = new List<(Package, string)>();
        }
        // Avoid duplicates if the same package provides the same capability multiple times
        if (!_providersMap[name].Any(x => x.Pkg.Nevra == pkg.Nevra))
        {
            _providersMap[name].Add((pkg, provString));
        }
    }

    /// <summary>
    /// Resolves dependencies for multiple targets using an Iterative Worklist (BFS) algorithm.
    /// Returns a topologically sorted list of packages ready for installation.
    /// </summary>
    public List<Package> Resolve(IEnumerable<string> targets)
    {
        var plan = new HashSet<Package>();
        var queue = new Queue<string>();
        var processedReqs = new HashSet<string>();

        // 1. Enqueue all user-requested targets as root nodes
        foreach (var t in targets)
        {
            queue.Enqueue(t);
        }

        // 2. Process the queue (BFS)
        while (queue.Count > 0)
        {
            string currentReqStr = queue.Dequeue();
            
            // Optimization: Skip if we already checked this exact capability string
            if (!processedReqs.Add(currentReqStr)) continue;

            var req = new RpmRequirement(currentReqStr);

            // A. Check if satisfied by INSTALLED packages
            if (IsSatisfiedByList(req, _installedPackages)) continue;

            // B. Check if satisfied by packages already in the PLAN
            if (IsSatisfiedByList(req, plan)) continue;

            // C. Find candidates in the REPOSITORY
            if (!_providersMap.TryGetValue(req.Name, out var candidates))
            {
                // In a real manager, we might check file provides (e.g. /usr/bin/bash) here.
                // For now, strict capability matching.
                throw new Exception($"Unresolvable dependency: '{currentReqStr}'. No package provides '{req.Name}'.");
            }

            // D. Filter candidates that satisfy specific version constraints (if any)
            var validCandidates = candidates.Where(c => req.IsSatisfiedBy(c.Pkg, c.ProvString)).ToList();

            if (validCandidates.Count == 0)
            {
                throw new Exception($"Version conflict: Found packages providing '{req.Name}', but none satisfy '{currentReqStr}'.");
            }

            // E. Selection Heuristic: Pick the best candidate
            var chosenPkg = PickBestCandidate(req.Name, validCandidates);

            // F. Add to plan and enqueue its dependencies
            if (plan.Add(chosenPkg))
            {
                foreach (var childReq in chosenPkg.Requires)
                {
                    // Ignore internal RPM namespace requirements (rpmlib)
                    if (childReq.StartsWith("rpmlib(")) continue;
                    
                    queue.Enqueue(childReq);
                }
            }
        }

        // 3. Sort the final plan so dependencies install before the apps that need them
        return TopologicalSort(plan);
    }

    private Package PickBestCandidate(string reqName, List<(Package Pkg, string ProvStr)> candidates)
    {
        // Heuristic 1: Exact Name Match.
        // If I request 'bash', and 'bash' provides it, pick 'bash' over 'sh-implementation'.
        var exactMatch = candidates.FirstOrDefault(c => c.Pkg.Name == reqName);
        if (exactMatch.Pkg != null) return exactMatch.Pkg;

        // Heuristic 2: Shortest Name.
        // Often base packages have shorter names than compat packages.
        var shortestName = candidates.OrderBy(c => c.Pkg.Name.Length).First();
        return shortestName.Pkg;
        
        // Note: 'candidates' is already sorted by Version Descending in constructor, 
        // so First() implies the newest version.
    }

    private bool IsSatisfiedByList(RpmRequirement req, IEnumerable<Package> packages)
    {
        foreach (var pkg in packages)
        {
            // Check implicit package name
            if (pkg.Name == req.Name && req.IsSatisfiedBy(pkg, pkg.Name)) return true;

            // Check explicit provides
            foreach (var prov in pkg.Provides)
            {
                var provReq = new RpmRequirement(prov);
                if (provReq.Name == req.Name && req.IsSatisfiedBy(pkg, prov)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sorts the packages so dependencies are installed first.
    /// </summary>
    private List<Package> TopologicalSort(HashSet<Package> unsorted)
    {
        var result = new List<Package>();
        var visited = new HashSet<string>();
        var tempMark = new HashSet<string>(); // For cycle detection
        
        // Create a quick lookup for packages currently in the transaction plan
        // We only care about sorting dependencies that ARE in the plan.
        var planLookup = unsorted.ToDictionary(p => p.Name, p => p);

        void Visit(Package pkg)
        {
            if (visited.Contains(pkg.Name)) return;
            
            // If we hit a temp mark, we found a circular dependency (A->B->A).
            // RPM handles cycles (usually), so we just break the loop here.
            if (tempMark.Contains(pkg.Name)) return; 

            tempMark.Add(pkg.Name);

            foreach (var reqStr in pkg.Requires)
            {
                var reqName = new RpmRequirement(reqStr).Name;
                
                // We need to find WHICH package in the plan satisfies this requirement.
                // It might be a package with the same name, or a provider.
                var providerInPlan = unsorted.FirstOrDefault(p => 
                    p.Name == reqName || 
                    p.Provides.Any(pr => new RpmRequirement(pr).Name == reqName)
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
            if (!visited.Contains(pkg.Name))
            {
                Visit(pkg);
            }
        }

        return result;
    }
}