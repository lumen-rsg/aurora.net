using Aurora.Core.Models;

namespace Aurora.Core.State;

/// <summary>
///     Centralized, performant loader for repository package databases.
///     Loads all .sqlite files in parallel and merges results.
/// </summary>
public static class RepoLoader
{
    /// <summary>
    ///     Loads all packages from all repository .sqlite databases in the given directory.
    ///     Repositories are loaded in parallel for maximum throughput.
    /// </summary>
    public static List<Package> LoadAllPackages(string repoDir)
    {
        var repoFiles = Directory.GetFiles(repoDir, "*.sqlite");
        if (repoFiles.Length == 0) return new List<Package>();

        var allPackages = new List<Package>[repoFiles.Length];

        Parallel.For(0, repoFiles.Length, i =>
        {
            var dbFile = repoFiles[i];
            try
            {
                using var db = new RpmRepoDb(dbFile);
                string repoId = Path.GetFileNameWithoutExtension(dbFile);
                allPackages[i] = db.GetAllPackages(repoId);
            }
            catch (Exception ex)
            {
                Logging.AuLogger.Error($"Failed to load repo DB {Path.GetFileName(dbFile)}: {ex.Message}");
                allPackages[i] = new List<Package>();
            }
        });

        // Calculate total capacity to avoid reallocations
        int totalCount = 0;
        foreach (var list in allPackages) totalCount += list.Count;

        var result = new List<Package>(totalCount);
        foreach (var list in allPackages) result.AddRange(list);
        return result;
    }

    /// <summary>
    ///     Loads all packages indexed by name (case-insensitive).
    ///     Useful for fast lookups (e.g., InfoCommand, DependencySolver).
    /// </summary>
    public static Dictionary<string, List<Package>> LoadPackagesByName(string repoDir)
    {
        var packages = LoadAllPackages(repoDir);
        var dict = new Dictionary<string, List<Package>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in packages)
        {
            if (!dict.TryGetValue(pkg.Name, out var list))
            {
                list = new List<Package>(1);
                dict[pkg.Name] = list;
            }
            list.Add(pkg);
        }

        return dict;
    }

    /// <summary>
    ///     Discovers all .sqlite repo database files in the given directory.
    ///     Returns an empty array if the directory doesn't exist.
    /// </summary>
    public static string[] DiscoverRepoDatabases(string repoDir)
    {
        if (!Directory.Exists(repoDir)) return Array.Empty<string>();
        return Directory.GetFiles(repoDir, "*.sqlite");
    }
}