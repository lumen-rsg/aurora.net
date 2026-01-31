using Aurora.Core.Models;
using Aurora.Core.Logging;

namespace Aurora.Core.Logic;

public static class ConflictValidator
{
    public static void Validate(List<Package> plan, List<Package> installedPackages)
    {
        var installedMap = installedPackages.ToDictionary(p => p.Name, p => p);

        foreach (var newPkg in plan)
        {
            // Check 1: Does New Package conflict with any Installed Package?
            foreach (var conflict in newPkg.Conflicts)
            {
                if (installedMap.ContainsKey(conflict))
                {
                    throw new InvalidOperationException(
                        $"Conflict Detected: '{newPkg.Name}' conflicts with installed package '{conflict}'.");
                }
            }

            // Check 2: Does any Installed Package conflict with New Package?
            // (Reverse conflict: e.g. installed 'vim' says it conflicts with 'nano')
            foreach (var installed in installedPackages)
            {
                if (installed.Conflicts.Contains(newPkg.Name))
                {
                    throw new InvalidOperationException(
                        $"Conflict Detected: Installed package '{installed.Name}' conflicts with new package '{newPkg.Name}'.");
                }
            }
        }
    }
}