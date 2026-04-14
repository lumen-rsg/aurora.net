using System.Text.Json;
using Aurora.Core.Logging;
using Aurora.Core.Models;

namespace Aurora.Core.State;

/// <summary>
///     On-disk JSON cache for repository package data.
///     Stores a manifest of SQLite file modification times alongside serialized packages
///     for fast deserialization without re-querying databases.
/// </summary>
public static class RepoCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false // Compact JSON for speed
    };

    private const string CacheFileName = "packages.cache.json";
    private const string ManifestFileName = "packages.cache.manifest";

    /// <summary>
    ///     Checks whether the on-disk cache is valid for the given .sqlite files.
    ///     Returns false if the cache or manifest doesn't exist, or any .sqlite file
    ///     has been modified since the cache was written.
    /// </summary>
    public static bool IsCacheValid(string repoDir, string[] sqliteFiles)
    {
        var manifestPath = Path.Combine(repoDir, ManifestFileName);
        var cachePath = Path.Combine(repoDir, CacheFileName);

        if (!File.Exists(manifestPath) || !File.Exists(cachePath)) return false;

        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson);
            if (manifest == null || manifest.Count != sqliteFiles.Length) return false;

            foreach (var sqliteFile in sqliteFiles)
            {
                var fileName = Path.GetFileName(sqliteFile);
                if (!manifest.TryGetValue(fileName, out var recordedTime)) return false;

                var actualTime = File.GetLastWriteTimeUtc(sqliteFile).ToString("O");
                if (actualTime != recordedTime) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AuLogger.Debug($"Cache manifest check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Reads cached packages from disk. Returns null if the cache is missing or corrupted.
    /// </summary>
    public static List<Package>? ReadCache(string repoDir)
    {
        var cachePath = Path.Combine(repoDir, CacheFileName);
        if (!File.Exists(cachePath)) return null;

        try
        {
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            return JsonSerializer.Deserialize<List<Package>>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            AuLogger.Debug($"Cache read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Writes packages and a staleness manifest to disk.
    /// </summary>
    public static void WriteCache(string repoDir, string[] sqliteFiles, List<Package> packages)
    {
        try
        {
            // Write manifest: maps sqlite filename → last-write-time UTC
            var manifest = new Dictionary<string, string>(sqliteFiles.Length);
            foreach (var sqliteFile in sqliteFiles)
            {
                var fileName = Path.GetFileName(sqliteFile);
                manifest[fileName] = File.GetLastWriteTimeUtc(sqliteFile).ToString("O");
            }

            var manifestPath = Path.Combine(repoDir, ManifestFileName);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

            // Write package data (compact JSON via stream to avoid huge string allocations)
            var cachePath = Path.Combine(repoDir, CacheFileName);
            using var stream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            JsonSerializer.Serialize(stream, packages, JsonOptions);
        }
        catch (Exception ex)
        {
            AuLogger.Debug($"Cache write failed: {ex.Message}");
            // Non-fatal: cache write failure shouldn't break the app
        }
    }

    /// <summary>
    ///     Removes all cache files from the repo directory.
    /// </summary>
    public static void ClearDiskCache(string repoDir)
    {
        try
        {
            var cachePath = Path.Combine(repoDir, CacheFileName);
            var manifestPath = Path.Combine(repoDir, ManifestFileName);
            if (File.Exists(cachePath)) File.Delete(cachePath);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
        catch (Exception ex)
        {
            AuLogger.Debug($"Cache clear failed: {ex.Message}");
        }
    }
}