using Aurora.Core.Logging;
using Aurora.Core.Models;

namespace Aurora.Core.State;

/// <summary>
///     Centralized, performant loader for repository package databases.
///     Uses a two-tier cache (in-memory + on-disk JSON) to avoid re-querying
///     SQLite databases on every invocation.
/// </summary>
public static class RepoLoader
{
    // --- In-memory cache (single process invocation) ---
    private static List<Package>? _memoryCache;
    private static string? _memoryCacheRepoDir;

    /// <summary>
    ///     Invalidates both in-memory and on-disk caches.
    ///     Called after a successful sync operation.
    /// </summary>
    public static void InvalidateCache(string? repoDir = null)
    {
        _memoryCache = null;
        _memoryCacheRepoDir = null;

        if (repoDir != null)
        {
            RepoCache.ClearDiskCache(repoDir);
        }
    }

    /// <summary>
    ///     Loads all packages from all repository .sqlite databases in the given directory.
    ///     Uses a two-tier cache strategy:
    ///       1. In-memory cache (hot) — zero I/O if already loaded in this process
    ///       2. On-disk JSON cache (warm) — fast deserialization, skips SQLite queries
    ///       3. Cold start — queries SQLite in parallel, then populates both caches
    /// </summary>
    public static List<Package> LoadAllPackages(string repoDir)
    {
        // Tier 1: In-memory cache
        if (_memoryCache != null && _memoryCacheRepoDir == repoDir)
        {
            return _memoryCache;
        }

        var repoFiles = DiscoverRepoDatabases(repoDir);
        if (repoFiles.Length == 0) return new List<Package>();

        // Tier 2: On-disk JSON cache
        if (RepoCache.IsCacheValid(repoDir, repoFiles))
        {
            var cached = RepoCache.ReadCache(repoDir);
            if (cached != null)
            {
                AuLogger.Debug($"Loaded {cached.Count} packages from disk cache.");
                _memoryCache = cached;
                _memoryCacheRepoDir = repoDir;
                return cached;
            }
        }

        // Tier 3: Cold start — load from SQLite databases in parallel
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
                AuLogger.Error($"Failed to load repo DB {Path.GetFileName(dbFile)}: {ex.Message}");
                allPackages[i] = new List<Package>();
            }
        });

        // Calculate total capacity to avoid reallocations
        int totalCount = 0;
        foreach (var list in allPackages) totalCount += list.Count;

        var result = new List<Package>(totalCount);
        foreach (var list in allPackages) result.AddRange(list);

        // Populate caches
        RepoCache.WriteCache(repoDir, repoFiles, result);
        _memoryCache = result;
        _memoryCacheRepoDir = repoDir;

        return result;
    }

    /// <summary>
    ///     Loads all packages indexed by name (case-insensitive).
    ///     Useful for fast lookups (e.g., InfoCommand, DependencySolver).
    ///     Reuses the in-memory cache if available.
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