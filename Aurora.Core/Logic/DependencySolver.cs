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

    private void Visit(string request, HashSet<string> visited, HashSet<string> stack, List<Package> plan)
    {
        if (_installed.Contains(request) || visited.Contains(request)) return;

        if (stack.Contains(request))
            throw new Exception($"Circular dependency detected: {request}");

        stack.Add(request);

        // 1. Find the provider for this request
        // A request could be a real package name OR a virtual "provides" (soname)
        Package? provider = null;

        if (_repository.ContainsKey(request))
        {
            provider = _repository[request];
        }
        else if (_providesMap.TryGetValue(request, out var providers))
        {
            // Pick the first provider (in the future, we can add logic to pick the best)
            provider = providers.First();
        }

        if (provider == null)
            throw new Exception($"Target not found: {request}");

        // If we found a provider but its NAME is different from the request (e.g. request libssl.so provided by openssl)
        // Check if the actual provider name is already visited/installed
        if (visited.Contains(provider.Name) || _installed.Contains(provider.Name))
        {
            stack.Remove(request);
            return;
        }

        visited.Add(provider.Name);

        // 2. Visit dependencies of the provider
        foreach (var dep in provider.Depends)
        {
            Visit(dep, visited, stack, plan);
        }

        stack.Remove(request);
        plan.Add(provider);
    }
}