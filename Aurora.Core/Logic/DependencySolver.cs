using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic;

/// <summary>
///     Resolves RPM dependency graphs using breadth-first search with version-aware
///     candidate selection and topological sorting. The provider map is built in
///     parallel for fast initialization over large repository sets.
/// </summary>
public class DependencySolver
{
    private readonly HashSet<string> _installedNevras;
    private readonly HashSet<string> _installedNames;
    private readonly Dictionary<string, List<(Package Pkg, string ProvString)>> _providersMap;

    /// <summary>
    ///     Creates a new solver over the given available and installed package sets.
    ///     The provider map is built in parallel using all available CPU cores.
    /// </summary>
    /// <param name="availablePackages">Packages available in configured repositories.</param>
    /// <param name="installedPackages">Packages currently installed on the system.</param>
    public DependencySolver(IEnumerable<Package> availablePackages, IEnumerable<Package> installedPackages)
    {
        var installedList = installedPackages.ToList();

        _installedNevras = installedList.Select(p => p.Nevra).ToHashSet();
        _installedNames = installedList.Select(p => p.Name).ToHashSet();

        var allPackages = availablePackages.Concat(installedList).DistinctBy(p => p.Nevra).ToList();

        _providersMap = BuildProvidersMapParallel(allPackages);
    }

    // ─── Public API ──────────────────────────────────────────────────

    /// <summary>
    ///     Resolves all transitive dependencies for the given target package names
    ///     and returns them in topologically-sorted install order.
    /// </summary>
    /// <param name="targets">Package names or provides to resolve.</param>
    /// <param name="onProgress">Optional callback invoked with (resolvedCount, currentPackageName) after each resolution step.</param>
    /// <returns>Packages in dependency-first order.</returns>
    public List<Package> Resolve(IEnumerable<string> targets, Action<int, string>? onProgress = null)
    {
        var plan = ResolveToPlan(targets, onProgress);
        return TopologicalSort(plan.Values);
    }

    // ─── Provider Map Construction (Parallel) ────────────────────────

    /// <summary>
    ///     Builds the provider lookup map in parallel. Each thread accumulates
    ///     entries into a thread-local dictionary, then we merge at the end —
    ///     zero locking during the hot path.
    /// </summary>
    private static Dictionary<string, List<(Package Pkg, string ProvString)>> BuildProvidersMapParallel(
        List<Package> allPackages)
    {
        // Thread-local accumulation: each thread gets its own dictionary
        var threadLocalMaps = new ThreadLocal<Dictionary<string, List<(Package Pkg, string ProvString)>>>(() =>
            new Dictionary<string, List<(Package Pkg, string ProvString)>>(64 * 1024), trackAllValues: true);

        var partitionCount = Environment.ProcessorCount;
        var partitionSize = Math.Max(1, allPackages.Count / partitionCount);

        // Parallel pass: each thread builds its own provider map
        Parallel.For(0, partitionCount, partitionIndex =>
        {
            var start = partitionIndex * partitionSize;
            var end = partitionIndex == partitionCount - 1 ? allPackages.Count : start + partitionSize;
            var localMap = threadLocalMaps.Value!;

            for (var pkgIdx = start; pkgIdx < end; pkgIdx++)
            {
                var pkg = allPackages[pkgIdx];
                AddProviderLocal(localMap, pkg.Name, pkg, pkg.Name);

                foreach (var prov in pkg.Provides)
                {
                    var provName = ParseProvideName(prov);
                    AddProviderLocal(localMap, provName, pkg, prov);

                    // UsrMerge path aliasing: /usr/bin ↔ /bin, /usr/sbin ↔ /sbin
                    AddPathAliases(localMap, provName, pkg, prov);
                }
            }
        });

        // Merge all thread-local maps into the final dictionary
        var merged = new Dictionary<string, List<(Package Pkg, string ProvString)>>(64 * 1024);

        foreach (var localMap in threadLocalMaps.Values)
        {
            foreach (var (name, entries) in localMap)
            {
                if (!merged.TryGetValue(name, out var existing))
                {
                    merged[name] = entries;
                }
                else
                {
                    existing.AddRange(entries);
                }
            }
        }

        threadLocalMaps.Dispose();

        // Parallel deduplication + version sort
        Parallel.ForEach(merged.Keys, key =>
        {
            var list = merged[key];
            if (list.Count <= 1) return;

            // Deduplicate by NEVRA (already sorted by version desc, keep first)
            var seen = new HashSet<string>(list.Count);
            var deduped = new List<(Package Pkg, string ProvStr)>(list.Count);
            foreach (var entry in list)
            {
                if (seen.Add(entry.Pkg.Nevra))
                    deduped.Add(entry);
            }

            // Sort highest version first
            deduped.Sort((a, b) => VersionComparer.Compare(b.Pkg.FullVersion, a.Pkg.FullVersion));
            merged[key] = deduped;
        });

        return merged;
    }

    /// <summary>
    ///     Adds a provider entry to a thread-local map. No locking needed.
    /// </summary>
    private static void AddProviderLocal(
        Dictionary<string, List<(Package Pkg, string ProvString)>> map,
        string name, Package pkg, string provString)
    {
        if (!map.TryGetValue(name, out var list))
        {
            list = new List<(Package Pkg, string ProvStr)>();
            map[name] = list;
        }

        // Quick duplicate check — avoids blowup in the merge step
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Pkg.Nevra == pkg.Nevra) return;
        }

        list.Add((pkg, provString));
    }

    /// <summary>
    ///     Registers UsrMerge path aliases (/usr/bin ↔ /bin, /usr/sbin ↔ /sbin).
    /// </summary>
    private static void AddPathAliases(
        Dictionary<string, List<(Package Pkg, string ProvString)>> map,
        string provName, Package pkg, string prov)
    {
        if (!provName.StartsWith('/')) return;

        if (provName.StartsWith("/usr/bin/"))
            AddProviderLocal(map, string.Concat("/bin/", provName.AsSpan(9)), pkg, prov);
        else if (provName.StartsWith("/bin/"))
            AddProviderLocal(map, string.Concat("/usr/bin/", provName.AsSpan(5)), pkg, prov);
        else if (provName.StartsWith("/usr/sbin/"))
            AddProviderLocal(map, string.Concat("/sbin/", provName.AsSpan(10)), pkg, prov);
        else if (provName.StartsWith("/sbin/"))
            AddProviderLocal(map, string.Concat("/usr/sbin/", provName.AsSpan(6)), pkg, prov);
    }

    // ─── Shared Parsing Helper ───────────────────────────────────────

    /// <summary>
    ///     Extracts the bare capability name from a provides/requires string,
    ///     stripping any trailing version operators (e.g. "lua(abi)=5.1" → "lua(abi)").
    /// </summary>
    public static string ParseProvideName(string provides)
    {
        for (var i = 0; i < provides.Length; i++)
        {
            var c = provides[i];
            if (c is '>' or '<' or '=' or '!')
                return provides.Substring(0, i).TrimEnd();
        }

        var sp = provides.IndexOf(' ');
        return sp > 0 ? provides.Substring(0, sp) : provides;
    }

    // ─── BFS Dependency Resolution ───────────────────────────────────

    /// <summary>
    ///     Performs a breadth-first traversal of the dependency graph, building
    ///     a plan (package name → chosen package) that satisfies all requirements.
    /// </summary>
    private Dictionary<string, Package> ResolveToPlan(
        IEnumerable<string> targets, Action<int, string>? onProgress)
    {
        var plan = new Dictionary<string, Package>();
        var queue = new Queue<(string ReqStr, Package? Requester)>();
        var processedReqs = new HashSet<string>();

        foreach (var t in targets)
            queue.Enqueue((t, null));

        var resolvedCount = 0;

        while (queue.Count > 0)
        {
            var (currentReqStr, requester) = queue.Dequeue();

            if (!processedReqs.Add(currentReqStr))
                continue;

            var req = new RpmRequirement(currentReqStr);
            var lookupName = ResolveLookupName(req.Name);

            // Find providers for this requirement
            if (!_providersMap.TryGetValue(lookupName, out var candidates))
            {
                HandleMissingProvider(currentReqStr, lookupName, requester);
                continue;
            }

            // Filter to candidates that satisfy the version constraint
            var validCandidates = FilterValidCandidates(candidates, req);
            if (validCandidates.Count == 0)
            {
                var reqInfo = requester != null ? $" (required by {requester.Name})" : "";
                throw new Exception($"Version conflict for '{currentReqStr}'{reqInfo}.");
            }

            // If an installed package already satisfies this requirement, skip
            if (IsSatisfiedByInstalled(validCandidates))
                continue;

            var chosenPkg = PickBestCandidate(lookupName, validCandidates);

            if (TryAddToPlan(plan, chosenPkg, req, requester))
            {
                resolvedCount++;
                onProgress?.Invoke(resolvedCount, chosenPkg.Name);
                EnqueueDependencies(chosenPkg, plan, queue);
            }
        }

        return plan;
    }

    /// <summary>
    ///     Resolves path aliases for file-based requirements (e.g. /bin/ls → /usr/bin/ls).
    /// </summary>
    private string ResolveLookupName(string lookupName)
    {
        if (!lookupName.StartsWith('/') || _providersMap.ContainsKey(lookupName))
            return lookupName;

        var alias = FindPathAlias(lookupName);
        return alias ?? lookupName;
    }

    /// <summary>
    ///     Filters candidates to only those that satisfy the given requirement's version constraints.
    /// </summary>
    private static List<(Package Pkg, string ProvStr)> FilterValidCandidates(
        IReadOnlyList<(Package Pkg, string ProvStr)> candidates, RpmRequirement req)
    {
        var valid = new List<(Package Pkg, string ProvStr)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (req.IsSatisfiedBy(c.Pkg, c.ProvStr))
                valid.Add(c);
        }

        return valid;
    }

    /// <summary>
    ///     Checks whether any valid candidate is already physically installed on the system.
    /// </summary>
    private bool IsSatisfiedByInstalled(IReadOnlyList<(Package Pkg, string ProvStr)> candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (_installedNevras.Contains(candidates[i].Pkg.Nevra))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Attempts to add or upgrade a package in the plan. Returns true if the
    ///     package was newly added or upgraded (meaning its deps should be enqueued).
    /// </summary>
    private bool TryAddToPlan(
        Dictionary<string, Package> plan, Package chosenPkg,
        RpmRequirement req, Package? requester)
    {
        if (!plan.TryGetValue(chosenPkg.Name, out var existingPkg))
        {
            plan[chosenPkg.Name] = chosenPkg;
            return true;
        }

        if (existingPkg.Nevra == chosenPkg.Nevra)
            return false;

        // Check if the existing plan entry already satisfies this requirement
        if (ExistingSatisfiesRequirement(existingPkg, req))
            return false;

        // Upgrade if the new candidate is newer
        if (VersionComparer.Compare(chosenPkg.FullVersion, existingPkg.FullVersion) > 0)
        {
            plan[chosenPkg.Name] = chosenPkg;
            return true;
        }

        // Hard conflict
        var reqInfo = requester != null ? $" (required by {requester.Name})" : "";
        throw new Exception(
            $"Dependency conflict: '{req}'{reqInfo} requires {chosenPkg.Name} {chosenPkg.FullVersion}, " +
            $"but {existingPkg.FullVersion} is locked in the plan.");
    }

    /// <summary>
    ///     Checks whether a package already in the plan satisfies the given requirement.
    /// </summary>
    private static bool ExistingSatisfiesRequirement(Package existingPkg, RpmRequirement req)
    {
        if (req.IsSatisfiedBy(existingPkg, existingPkg.Name))
            return true;

        for (var i = 0; i < existingPkg.Provides.Count; i++)
        {
            if (req.IsSatisfiedBy(existingPkg, existingPkg.Provides[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Enqueues all non-rpmlib, non-already-satisfied dependencies of a package.
    ///     Handles conditional ("if") rich dependencies.
    /// </summary>
    private void EnqueueDependencies(
        Package pkg, Dictionary<string, Package> plan,
        Queue<(string ReqStr, Package? Requester)> queue)
    {
        foreach (var childReqStr in pkg.Requires)
        {
            // Skip RPM internal capabilities
            if (childReqStr.StartsWith("rpmlib("))
                continue;

            // Handle conditional rich deps: "(requirement if conditionPkg)"
            if (childReqStr.StartsWith('(') && childReqStr.Contains(" if "))
            {
                if (!EvaluateConditionalDependency(childReqStr, plan, out var actualReq))
                    continue;

                queue.Enqueue((actualReq, pkg));
                continue;
            }

            queue.Enqueue((childReqStr, pkg));
        }
    }

    /// <summary>
    ///     Evaluates a conditional rich dependency like "(foo >= 1.0 if bar)".
    ///     Returns false (and null req) if the condition package is not in the plan or installed.
    /// </summary>
    private bool EvaluateConditionalDependency(
        string rawReq, Dictionary<string, Package> plan, out string actualReq)
    {
        actualReq = rawReq;
        var clean = rawReq.AsSpan().Trim("()");
        var ifIdx = clean.IndexOf(" if ");

        if (ifIdx == -1)
            return true;

        var reqPart = clean.Slice(0, ifIdx).Trim();
        var conditionPkgName = clean.Slice(ifIdx + 4).Trim().ToString();

        var conditionMet = plan.Values.Any(p => p.Name == conditionPkgName)
                           || _installedNames.Contains(conditionPkgName);

        if (!conditionMet)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Bypassing conditional:[/] {rawReq} [grey](Condition '{conditionPkgName}' not met)[/]");
            return false;
        }

        actualReq = reqPart.ToString();
        return true;
    }

    /// <summary>
    ///     Handles the case where no package provides a required capability.
    ///     Throws with a helpful error message including fuzzy suggestions.
    ///     Virtual identity requirements (user/group) are silently skipped.
    /// </summary>
    private void HandleMissingProvider(string currentReqStr, string lookupName, Package? requester)
    {
        // Virtual identity deps are handled by sysusers
        if (lookupName.StartsWith("user(") || lookupName.StartsWith("group("))
        {
            AnsiConsole.MarkupLine(
                $"[grey]Bypassing virtual identity:[/] {lookupName}[grey](Handled by sysusers)[/]");
            return;
        }

        var requesterInfo = requester != null ? $" (required by [bold]{requester.Name}[/])" : "";

        var suggestions = _providersMap.Keys
            .Select(k => new { Name = k, Distance = FuzzyMatcher.LevenshteinDistance(lookupName, k) })
            .OrderBy(x => x.Distance)
            .Take(5)
            .ToList();

        var msg = $"Unresolvable dependency: '{currentReqStr}'{requesterInfo}. No package provides '{lookupName}'.";
        if (suggestions.Count > 0)
        {
            msg += "\nDid you mean one of these?";
            foreach (var s in suggestions)
                msg += $"\n  - {s.Name} (dist: {s.Distance})";
        }

        throw new Exception(msg);
    }

    // ─── Path Alias Lookup ────────────────────────────────────────────

    /// <summary>
    ///     Tries to find a UsrMerge alias for a file-based requirement by
    ///     searching /usr/bin, /usr/sbin, /bin, and /sbin for the same filename.
    /// </summary>
    private string? FindPathAlias(string requestedPath)
    {
        var lastSlash = requestedPath.LastIndexOf('/');
        if (lastSlash == -1) return null;

        var fileName = requestedPath.Substring(lastSlash + 1);

        if (_providersMap.ContainsKey($"/usr/bin/{fileName}")) return $"/usr/bin/{fileName}";
        if (_providersMap.ContainsKey($"/usr/sbin/{fileName}")) return $"/usr/sbin/{fileName}";
        if (_providersMap.ContainsKey($"/bin/{fileName}")) return $"/bin/{fileName}";
        if (_providersMap.ContainsKey($"/sbin/{fileName}")) return $"/sbin/{fileName}";

        return null;
    }

    // ─── Candidate Selection ──────────────────────────────────────────

    /// <summary>
    ///     Picks the best candidate for a requirement from a list of valid providers.
    ///     Prefers exact name matches, then name similarity, then highest version.
    /// </summary>
    private Package PickBestCandidate(string reqName, List<(Package Pkg, string ProvStr)> candidates)
    {
        // Fast path: exact name match
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Pkg.Name == reqName)
                return candidates[i].Pkg;
        }

        // Compute base name for scoring (e.g. "lua(abi)" → "lua")
        var baseName = reqName;
        var parenIdx = baseName.IndexOf('(');
        if (parenIdx > 0) baseName = baseName.Substring(0, parenIdx);

        // Score: exact base-name prefix → 2, contains → 1, else → 0
        // Then by Levenshtein distance, name length, and version descending
        var sorted = candidates
            .OrderByDescending(c => c.Pkg.Name.StartsWith(baseName) ? 2 : c.Pkg.Name.Contains(baseName) ? 1 : 0)
            .ThenBy(c => FuzzyMatcher.LevenshteinDistance(baseName, c.Pkg.Name))
            .ThenBy(c => c.Pkg.Name.Length)
            .ThenByDescending(c => c.Pkg.FullVersion, new VersionComparer())
            .ToList();

        return sorted[0].Pkg;
    }

    // ─── Topological Sort (Dependency-First Order) ────────────────────

    /// <summary>
    ///     Sorts resolved packages so that dependencies appear before dependents,
    ///     ensuring correct install order for RPM transactions.
    /// </summary>
    private List<Package> TopologicalSort(IEnumerable<Package> unsorted)
    {
        var unsortedList = unsorted.ToList();
        var result = new List<Package>(unsortedList.Count);
        var visited = new HashSet<string>();
        var tempMark = new HashSet<string>();

        // Build provider lookup within the resolved plan
        var planProviders = BuildPlanProviderMap(unsortedList);

        // Recursive DFS for topological ordering
        void Visit(Package pkg)
        {
            if (visited.Contains(pkg.Name)) return;
            if (tempMark.Contains(pkg.Name)) return; // Break circular chains

            tempMark.Add(pkg.Name);

            foreach (var reqStr in pkg.Requires)
            {
                if (reqStr.StartsWith("rpmlib(")) continue;

                var reqName = new RpmRequirement(reqStr).Name;

                if (planProviders.TryGetValue(reqName, out var provider))
                {
                    Visit(provider);
                    continue;
                }

                // Fallback: try path aliases for file requirements
                if (reqName.StartsWith('/'))
                    TryVisitPathAlias(reqName, planProviders, Visit);
            }

            tempMark.Remove(pkg.Name);
            visited.Add(pkg.Name);
            result.Add(pkg);
        }

        foreach (var pkg in unsortedList)
        {
            if (!visited.Contains(pkg.Name))
                Visit(pkg);
        }

        return result;
    }

    /// <summary>
    ///     Builds a name → package lookup for all packages in the resolved plan,
    ///     including virtual provides and UsrMerge path aliases.
    /// </summary>
    private static Dictionary<string, Package> BuildPlanProviderMap(List<Package> packages)
    {
        var map = new Dictionary<string, Package>(packages.Count * 4);

        // Pass 1: explicit package names (highest priority)
        foreach (var pkg in packages)
            map[pkg.Name] = pkg;

        // Pass 2: virtual provides + path aliases (lower priority, won't overwrite)
        foreach (var pkg in packages)
        {
            foreach (var pr in pkg.Provides)
            {
                var n = ParseProvideName(pr);
                if (!map.ContainsKey(n))
                    map[n] = pkg;

                // UsrMerge aliasing
                if (!n.StartsWith('/')) continue;

                if (n.StartsWith("/usr/bin/") && !map.ContainsKey(string.Concat("/bin/", n.AsSpan(9))))
                    map[string.Concat("/bin/", n.AsSpan(9))] = pkg;
                else if (n.StartsWith("/bin/") && !map.ContainsKey(string.Concat("/usr/bin/", n.AsSpan(5))))
                    map[string.Concat("/usr/bin/", n.AsSpan(5))] = pkg;
                else if (n.StartsWith("/usr/sbin/") && !map.ContainsKey(string.Concat("/sbin/", n.AsSpan(10))))
                    map[string.Concat("/sbin/", n.AsSpan(10))] = pkg;
                else if (n.StartsWith("/sbin/") && !map.ContainsKey(string.Concat("/usr/sbin/", n.AsSpan(6))))
                    map[string.Concat("/usr/sbin/", n.AsSpan(6))] = pkg;
            }
        }

        return map;
    }

    /// <summary>
    ///     Attempts to resolve a file-based requirement through UsrMerge path aliases
    ///     and visit the providing package in the topological sort.
    /// </summary>
    private static void TryVisitPathAlias(
        string reqName, Dictionary<string, Package> planProviders, Action<Package> visit)
    {
        var lastSlash = reqName.LastIndexOf('/');
        if (lastSlash == -1) return;

        var fileName = reqName.Substring(lastSlash + 1);

        if (planProviders.TryGetValue($"/usr/bin/{fileName}", out var p)) visit(p);
        else if (planProviders.TryGetValue($"/bin/{fileName}", out p)) visit(p);
        else if (planProviders.TryGetValue($"/usr/sbin/{fileName}", out p)) visit(p);
        else if (planProviders.TryGetValue($"/sbin/{fileName}", out p)) visit(p);
    }
}