using Aurora.Core.Models;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly Dictionary<string, Package> _repository;
    private readonly HashSet<string> _installed;
    
    // Map: VirtualName -> List of (Package that provides it, Version it provides)
    private readonly Dictionary<string, List<(Package Pkg, string? ProvidedVersion)>> _providesMap;

    public DependencySolver(List<Package> repoPackages, List<Package> installedPackages)
    {
        _installed = installedPackages.Select(p => p.Name).ToHashSet();
        _repository = new Dictionary<string, Package>();
        _providesMap = new Dictionary<string, List<(Package, string?)>>();

        foreach (var pkg in repoPackages)
        {
            // 1. Standard Package Indexing
            if (!_repository.TryGetValue(pkg.Name, out var existing) || 
                VersionComparer.IsNewer(existing.Version, pkg.Version))
            {
                _repository[pkg.Name] = pkg;
            }

            // 2. Virtual Provides Indexing
            foreach (var provStr in pkg.Provides)
            {
                // Parse "libreadline.so=8-64"
                var prov = new DependencyRequest(provStr); 
                
                if (!_providesMap.ContainsKey(prov.Name)) 
                    _providesMap[prov.Name] = new List<(Package, string?)>();
                
                _providesMap[prov.Name].Add((pkg, prov.Version));
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
        var request = new DependencyRequest(rawRequest);

        if (_installed.Contains(request.Name) || visited.Contains(request.Name)) return;

        if (stack.Contains(request.Name))
            throw new Exception($"Circular dependency detected: {request.Name}");

        stack.Add(request.Name);

        Package? selectedProvider = null;

        // --- SEARCH LOGIC ---

        // 1. Check if a real package matches the name
        if (_repository.TryGetValue(request.Name, out var pkg))
        {
            if (request.IsSatisfiedBy(pkg))
            {
                selectedProvider = pkg;
            }
        }

        // 2. Check if any package provides this name/version
        if (selectedProvider == null && _providesMap.TryGetValue(request.Name, out var providers))
        {
            foreach (var entry in providers)
            {
                // If the request has a version (e.g. =8-64), we MUST check 
                // the version provided by the package, NOT the package's own version.
                if (request.Operator != null)
                {
                    // Create a dummy package object to reuse the IsSatisfiedBy logic
                    // and check the provided version
                    var virtualPkg = new Package { Version = entry.ProvidedVersion ?? "" };
                    if (request.IsSatisfiedBy(virtualPkg))
                    {
                        selectedProvider = entry.Pkg;
                        break;
                    }
                }
                else
                {
                    // No version requested, any provider will do
                    selectedProvider = entry.Pkg;
                    break;
                }
            }
        }

        if (selectedProvider == null)
            throw new Exception($"Target not found: {rawRequest}");

        // 3. Resolve child dependencies
        if (!visited.Contains(selectedProvider.Name))
        {
            visited.Add(selectedProvider.Name);
            foreach (var dep in selectedProvider.Depends)
            {
                Visit(dep, visited, stack, plan);
            }
            plan.Add(selectedProvider);
        }

        stack.Remove(request.Name);
    }
}