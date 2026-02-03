using Aurora.Core.Models;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly Dictionary<string, Package> _repository;
    private readonly HashSet<string> _installed;
    private readonly Dictionary<string, List<Package>> _providesMap;

    public DependencySolver(List<Package> repoPackages, List<Package> installedPackages)
    {
        _installed = installedPackages.Select(p => p.Name).ToHashSet();
        _repository = new Dictionary<string, Package>();
        _providesMap = new Dictionary<string, List<Package>>();

        // 1. Deduplicate and pick LATEST versions
        foreach (var pkg in repoPackages)
        {
            if (!_repository.TryGetValue(pkg.Name, out var existing) || 
                VersionComparer.IsNewer(existing.Version, pkg.Version))
            {
                _repository[pkg.Name] = pkg;
            }

            // 2. Build the Provides Map (for sonames like libacl.so)
            foreach (var prov in pkg.Provides)
            {
                if (!_providesMap.ContainsKey(prov)) _providesMap[prov] = new List<Package>();
                _providesMap[prov].Add(pkg);
            }
        }
    }

    public List<Package> Resolve(string targetName)
    {
        var plan = new List<Package>();
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        Visit(targetName, visited, recursionStack, plan);
        return plan;
    }

    private void Visit(string rawRequest, HashSet<string> visited, HashSet<string> stack, List<Package> plan)
    {
        // 1. Parse the request (e.g., "linux-api-headers>=4.10")
        var request = new DependencyRequest(rawRequest);

        // Check if already satisfied by installed packages or planned packages
        if (_installed.Contains(request.Name) || visited.Contains(request.Name)) return;

        if (stack.Contains(request.Name))
            throw new Exception($"Circular dependency detected: {request.Name}");

        stack.Add(request.Name);

        // 2. Find Candidate
        Package? candidate = null;

        // Try direct name match
        if (_repository.TryGetValue(request.Name, out var pkg))
        {
            candidate = pkg;
        }
        // Try virtual provides (sonames)
        else if (_providesMap.TryGetValue(request.Name, out var providers))
        {
            candidate = providers.First();
        }

        if (candidate == null)
            throw new Exception($"Target not found: {rawRequest}");

        // 3. Validate Version Constraint
        if (!request.IsSatisfiedBy(candidate))
        {
            throw new Exception($"Version mismatch for {request.Name}. " +
                                $"Requested: {request.Operator}{request.Version}, " +
                                $"Available: {candidate.Version}");
        }

        // 4. Recurse
        visited.Add(candidate.Name);

        foreach (var dep in candidate.Depends)
        {
            Visit(dep, visited, stack, plan);
        }

        stack.Remove(request.Name);
        plan.Add(candidate);
    }
}