using Aurora.Core.Logging;
using Aurora.Core.Models;
using Aurora.Core.Parsing;

namespace Aurora.Core.State;

public static class RepoLoader
{
    private static List<Package>? _memoryCache;
    private static string? _memoryCacheRepoDir;

    public static void InvalidateCache(string? repoDir = null)
    {
        _memoryCache = null;
        _memoryCacheRepoDir = null;

        if (repoDir != null)
        {
            RepoCache.ClearDiskCache(repoDir);
        }
    }

    public static List<Package> LoadAllPackages(string repoDir)
    {
        if (_memoryCache != null && _memoryCacheRepoDir == repoDir)
        {
            return _memoryCache;
        }

        var repoFiles = DiscoverRepoDatabases(repoDir);
        if (repoFiles.Length == 0) return new List<Package>();

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

        // Cold start — load from SQLite or XML databases in parallel
        var allPackages = new List<Package>[repoFiles.Length];

        Parallel.For(0, repoFiles.Length, i =>
        {
            var dbFile = repoFiles[i];
            try
            {
                allPackages[i] = LoadSingleRepo(dbFile);
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to load repo DB {Path.GetFileName(dbFile)}: {ex.Message}");
                allPackages[i] = new List<Package>();
            }
        });

        // Merge filelists data for repos that need it
        MergeFilelists(repoDir, allPackages, repoFiles);

        int totalCount = 0;
        foreach (var list in allPackages) totalCount += list.Count;

        var result = new List<Package>(totalCount);
        foreach (var list in allPackages) result.AddRange(list);

        RepoCache.WriteCache(repoDir, repoFiles, result);
        _memoryCache = result;
        _memoryCacheRepoDir = repoDir;

        return result;
    }

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

    public static string[] DiscoverRepoDatabases(string repoDir)
    {
        if (!Directory.Exists(repoDir)) return Array.Empty<string>();

        var files = new List<string>();
        files.AddRange(Directory.GetFiles(repoDir, "*.sqlite"));

        // Only pick up _primary.xml files that don't have a corresponding .sqlite
        foreach (var xmlFile in Directory.GetFiles(repoDir, "*_primary.xml"))
        {
            var repoId = xmlFile.Replace("_primary.xml", "");
            var sqlitePath = Path.Combine(repoDir, $"{Path.GetFileName(repoId)}.sqlite");
            if (!File.Exists(sqlitePath))
                files.Add(xmlFile);
        }

        return files.ToArray();
    }

    private static List<Package> LoadSingleRepo(string dbFile)
    {
        var fileName = Path.GetFileName(dbFile);

        if (fileName.EndsWith("_primary.xml"))
        {
            // XML primary — extract repo ID from filename: "{repoId}_primary.xml"
            var repoId = fileName.Substring(0, fileName.Length - "_primary.xml".Length);
            return PrimaryXmlParser.ParseFile(dbFile, repoId);
        }

        // SQLite primary
        var sqliteRepoId = Path.GetFileNameWithoutExtension(dbFile);
        using var db = new RpmRepoDb(dbFile);
        return db.GetAllPackages(sqliteRepoId);
    }

    private static void MergeFilelists(string repoDir, List<Package>[] allPackages, string[] repoFiles)
    {
        // Build a checksum → package lookup for fast matching
        var pkgByChecksum = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in allPackages)
        {
            foreach (var pkg in list)
            {
                if (!string.IsNullOrEmpty(pkg.Checksum))
                    pkgByChecksum[pkg.Checksum] = pkg;
            }
        }

        // Check for standalone filelists files
        if (!Directory.Exists(repoDir)) return;

        // filelists XML
        foreach (var flFile in Directory.GetFiles(repoDir, "*_filelists.xml"))
        {
            try
            {
                var fileMap = FilelistsXmlParser.ParseFile(flFile);
                foreach (var kvp in fileMap)
                {
                    if (pkgByChecksum.TryGetValue(kvp.Key, out var pkg))
                    {
                        foreach (var file in kvp.Value)
                        {
                            if (!pkg.Provides.Contains(file))
                                pkg.Provides.Add(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to load filelists XML {Path.GetFileName(flFile)}: {ex.Message}");
            }
        }

        // filelists SQLite (separate from primary.sqlite)
        foreach (var flSqlite in Directory.GetFiles(repoDir, "*_filelists.sqlite"))
        {
            try
            {
                using var flDb = new RpmRepoDb(flSqlite);
                var fileMap = flDb.GetFileListsByChecksum();
                foreach (var kvp in fileMap)
                {
                    if (pkgByChecksum.TryGetValue(kvp.Key, out var pkg))
                    {
                        foreach (var file in kvp.Value)
                        {
                            if (!pkg.Provides.Contains(file))
                                pkg.Provides.Add(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AuLogger.Error($"Failed to load filelists SQLite {Path.GetFileName(flSqlite)}: {ex.Message}");
            }
        }
    }
}
