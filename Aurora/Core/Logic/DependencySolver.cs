using Aurora.Core.Models;
using Aurora.Core.Logging;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly Dictionary<string, Package> _repository;
    private readonly HashSet<string> _installed;

    public DependencySolver(List<Package> repoPackages, List<Package> installedPackages)
    {
        _repository = repoPackages.ToDictionary(p => p.Name, p => p);
        _installed = installedPackages.Select(p => p.Name).ToHashSet();
    }

    public List<Package> Resolve(string targetName)
    {
        var plan = new List<Package>();
        var visited = new HashSet<string>(); // Visited in global traversal
        var recursionStack = new HashSet<string>(); // Visited in current branch (cycle detection)

        if (!_repository.ContainsKey(targetName))
            throw new Exception($"Package '{targetName}' not found in repository.");

        Visit(targetName, visited, recursionStack, plan);
        return plan;
    }

    private void Visit(string pkgName, HashSet<string> visited, HashSet<string> stack, List<Package> plan)
    {
        // 1. Check if installed
        if (_installed.Contains(pkgName)) return;

        // 2. Check if already planned
        if (visited.Contains(pkgName)) return;

        // 3. Cycle detection
        if (stack.Contains(pkgName))
            throw new Exception($"Circular dependency detected: {pkgName}");

        // 4. Look up package
        if (!_repository.TryGetValue(pkgName, out var pkg))
            throw new Exception($"Dependency '{pkgName}' not found.");

        stack.Add(pkgName);
        visited.Add(pkgName);

        // 5. Visit Dependencies
        foreach (var dep in pkg.Depends)
        {
            Visit(dep, visited, stack, plan);
        }

        stack.Remove(pkgName);
        
        // 6. Add to plan (leaves first)
        plan.Add(pkg);
    }
}