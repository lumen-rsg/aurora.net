using Microsoft.Data.Sqlite;
using Aurora.Core.Models;
using System.Runtime.InteropServices;

namespace Aurora.Core.State;

public class RpmRepoDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _hostArch;

    public RpmRepoDb(string sqliteFilePath)
    {
        if (!File.Exists(sqliteFilePath)) throw new FileNotFoundException("Repo DB not found", sqliteFilePath);
        
        _connection = new SqliteConnection($"Data Source={sqliteFilePath};Mode=ReadOnly;");
        _connection.Open();

        // Performance PRAGMAs — read-only workload optimizations
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = @"
                PRAGMA cache_size = -64000;
                PRAGMA mmap_size = 268435456;
                PRAGMA query_only = true;
                PRAGMA journal_mode = OFF;";
            pragma.ExecuteNonQuery();
        }

        _hostArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => "x86_64"
        };
    }

    // --- CRITICAL FIX: Reconstruct versioned dependencies from SQLite columns ---
    private string FormatCapability(string name, string? flags, string? epoch, string? version, string? release)
    {
        if (string.IsNullOrEmpty(flags) || string.IsNullOrEmpty(version))
        {
            return name;
        }

        // Convert SQLite flags back to mathematical operators
        string op = flags switch
        {
            "EQ" => "=",
            "LT" => "<",
            "GT" => ">",
            "LE" => "<=",
            "GE" => ">=",
            _ => flags
        };

        if (string.IsNullOrEmpty(op)) return name;

        string fullVersion = version;
        
        if (!string.IsNullOrEmpty(release))
        {
            fullVersion = $"{version}-{release}";
        }
        
        if (!string.IsNullOrEmpty(epoch) && epoch != "0")
        {
            fullVersion = $"{epoch}:{fullVersion}";
        }

        return $"{name} {op} {fullVersion}";
    }

    public List<Package> GetAllPackages(string repoId)
    {
        var packages = new Dictionary<long, Package>();

        // 1. Fetch Core Package Data (Arch filtered)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM packages WHERE arch = $arch OR arch = 'noarch'";
            cmd.Parameters.AddWithValue("$arch", _hostArch);
            
            using var reader = cmd.ExecuteReader();
            int idKey = reader.GetOrdinal("pkgKey");
            int idName = reader.GetOrdinal("name");
            int idEpoch = reader.GetOrdinal("epoch");
            int idVer = reader.GetOrdinal("version");
            int idRel = reader.GetOrdinal("release");
            int idArch = reader.GetOrdinal("arch");
            int idLoc = reader.GetOrdinal("location_href");
            int idId = reader.GetOrdinal("pkgId");
            int idSizeP = reader.GetOrdinal("size_package");

            while (reader.Read())
            {
                long pkgKey = reader.GetInt64(idKey);
                packages[pkgKey] = new Package
                {
                    RepositoryId = repoId,
                    Name = reader.GetString(idName),
                    Epoch = reader.IsDBNull(idEpoch) ? "0" : reader.GetValue(idEpoch).ToString() ?? "0",
                    Version = reader.GetString(idVer),
                    Release = reader.GetString(idRel),
                    Arch = reader.GetString(idArch),
                    LocationHref = reader.GetString(idLoc),
                    Checksum = reader.GetString(idId),
                    Size = reader.GetInt64(idSizeP)
                };
            }
        }

        // 2. Fetch Provides (filtered to loaded packages only — avoids full table scan)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT p.pkgKey, p.name, p.flags, p.epoch, p.version, p.release 
                FROM provides p
                WHERE p.pkgKey IN (SELECT pkgKey FROM packages WHERE arch = $arch OR arch = 'noarch')";
            cmd.Parameters.AddWithValue("$arch", _hostArch);
            using var reader = cmd.ExecuteReader();
            int idKey = reader.GetOrdinal("pkgKey");
            int idName = reader.GetOrdinal("name");
            int idFlags = reader.GetOrdinal("flags");
            int idEpoch = reader.GetOrdinal("epoch");
            int idVersion = reader.GetOrdinal("version");
            int idRelease = reader.GetOrdinal("release");

            while (reader.Read())
            {
                long pkgKey = reader.GetInt64(idKey);
                if (packages.TryGetValue(pkgKey, out var pkg))
                {
                    string name = reader.GetString(idName);
                    string? flags = reader.IsDBNull(idFlags) ? null : reader.GetString(idFlags);
                    string? epoch = reader.IsDBNull(idEpoch) ? null : reader.GetString(idEpoch);
                    string? version = reader.IsDBNull(idVersion) ? null : reader.GetString(idVersion);
                    string? release = reader.IsDBNull(idRelease) ? null : reader.GetString(idRelease);

                    pkg.Provides.Add(FormatCapability(name, flags, epoch, version, release));
                }
            }
        }

        // 3. Fetch Important Files (filtered to loaded packages only — avoids full table scan)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT f.pkgKey, f.name FROM files f
                WHERE f.pkgKey IN (SELECT pkgKey FROM packages WHERE arch = $arch OR arch = 'noarch')
                  AND (f.name LIKE '/usr/bin/%' 
                   OR f.name LIKE '/bin/%' 
                   OR f.name LIKE '/usr/sbin/%' 
                   OR f.name LIKE '/sbin/%'
                   OR f.name LIKE '/etc/%')";
            cmd.Parameters.AddWithValue("$arch", _hostArch);
            
            using var reader = cmd.ExecuteReader();
            int idKey = reader.GetOrdinal("pkgKey");
            int idName = reader.GetOrdinal("name");
            
            while (reader.Read())
            {
                long pkgKey = reader.GetInt64(idKey);
                if (packages.TryGetValue(pkgKey, out var pkg))
                {
                    pkg.Provides.Add(reader.GetString(idName));
                }
            }
        }

        // 4. Fetch Requires (filtered to loaded packages only — avoids full table scan)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT r.pkgKey, r.name, r.flags, r.epoch, r.version, r.release 
                FROM requires r
                WHERE r.pkgKey IN (SELECT pkgKey FROM packages WHERE arch = $arch OR arch = 'noarch')
                  AND r.name NOT LIKE 'rpmlib(%'";
            cmd.Parameters.AddWithValue("$arch", _hostArch);
            using var reader = cmd.ExecuteReader();
            int idKey = reader.GetOrdinal("pkgKey");
            int idName = reader.GetOrdinal("name");
            int idFlags = reader.GetOrdinal("flags");
            int idEpoch = reader.GetOrdinal("epoch");
            int idVersion = reader.GetOrdinal("version");
            int idRelease = reader.GetOrdinal("release");

            while (reader.Read())
            {
                long pkgKey = reader.GetInt64(idKey);
                if (packages.TryGetValue(pkgKey, out var pkg))
                {
                    string name = reader.GetString(idName);
                    string? flags = reader.IsDBNull(idFlags) ? null : reader.GetString(idFlags);
                    string? epoch = reader.IsDBNull(idEpoch) ? null : reader.GetString(idEpoch);
                    string? version = reader.IsDBNull(idVersion) ? null : reader.GetString(idVersion);
                    string? release = reader.IsDBNull(idRelease) ? null : reader.GetString(idRelease);

                    pkg.Requires.Add(FormatCapability(name, flags, epoch, version, release));
                }
            }
        }

        return new List<Package>(packages.Values);
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}