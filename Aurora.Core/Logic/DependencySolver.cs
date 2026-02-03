using Aurora.Core.Models;
using Spectre.Console;

namespace Aurora.Core.Logic;

public class DependencySolver
{
    private readonly Dictionary<string, Package> _repository;
    private readonly HashSet<string> _installed;
    
    // Key: Capability Name (e.g. "libreadline.so")
    // Value: List of tuples containing the Package and the specific Provision info
    private readonly Dictionary<string, List<(Package Pkg, DependencyRequest Provision)>> _providesMap;

    public DependencySolver(List<Package> repoPackages, List<Package> installedPackages)
    {
        _installed = installedPackages.Select(p => p.Name).ToHashSet();
        _repository = new Dictionary<string, Package>();
        _providesMap = new Dictionary<string, List<(Package, DependencyRequest)>>();

        foreach (var pkg in repoPackages)
        {
            // 1. Store Package (pick latest)
            if (!_repository.TryGetValue(pkg.Name, out var existing) || 
                VersionComparer.IsNewer(existing.Version, pkg.Version))
            {
                _repository[pkg.Name] = pkg;
            }

            // 2. Map Provisions
            foreach (var provStr in pkg.Provides)
            {
                var prov = new DependencyRequest(provStr);
                if (!_providesMap.ContainsKey(prov.Name)) 
                    _providesMap[prov.Name] = new List<(Package, DependencyRequest)>();
                
                _providesMap[prov.Name].Add((pkg, prov));
            }
        }
    }

    public List<Package> Resolve(string targetName)
    {
        var plan = new List<Package>();
        var visited = new HashSet<string>();
        var stack = new HashSet<string>();
        Visit(targetName, visited, stack, plan);
        return plan;
    }

    private void Visit(string rawRequest, HashSet<string> visited, HashSet<string> stack, List<Package> plan)
    {
        var request = new DependencyRequest(rawRequest);

        if (_installed.Contains(request.Name) || visited.Contains(request.Name)) return;
        if (stack.Contains(request.Name)) throw new Exception($"Circular dependency: {request.Name}");

        stack.Add(request.Name);

        Package? candidate = null;

        // 1. Try Name Match
        if (_repository.TryGetValue(request.Name, out var pkg))
        {
            if (request.IsSatisfiedBy(pkg))
            {
                candidate = pkg;
            }
            else
            {
                AnsiConsole.MarkupLine($"[grey]DEBUG: Rejected '{pkg.Name} {pkg.Version}' for '{rawRequest}' (Version mismatch)[/]");
            }
        }

        // 2. Try Provides Match
        if (candidate == null && _providesMap.TryGetValue(request.Name, out var providers))
        {
            // Debugging the provides lookup
            foreach (var p in providers)
            {
                bool match = p.Provision.Satisfies(request);
                if (match)
                {
                    candidate = p.Pkg;
                    break;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[grey]DEBUG: Rejected provider '{p.Pkg.Name}' provides '{p.Provision.Name} {p.Provision.Operator} {p.Provision.Version}' for request '{rawRequest}'[/]");
                }
            }
        }

        if (candidate == null)
        {
            // Help the user debug the repo state
            if (_providesMap.ContainsKey(request.Name))
            {
                throw new Exception($"Version mismatch for {request.Name}. See DEBUG logs above.");
            }
            throw new Exception($"Target not found: {rawRequest}");
        }

        if (visited.Contains(candidate.Name) || _installed.Contains(candidate.Name))
        {
            stack.Remove(request.Name);
            return;
        }

        visited.Add(candidate.Name);

        foreach (var dep in candidate.Depends)
        {
            Visit(dep, visited, stack, plan);
        }

        stack.Remove(request.Name);
        plan.Add(candidate);
    }
}