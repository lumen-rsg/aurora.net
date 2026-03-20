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

        // 2. Fetch Provides (Now with Versions!)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name, flags, epoch, version, release FROM provides";
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

        // 3. Fetch Important Files
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT pkgKey, name FROM files 
                WHERE name LIKE '/usr/bin/%' 
                   OR name LIKE '/bin/%' 
                   OR name LIKE '/usr/sbin/%' 
                   OR name LIKE '/sbin/%'
                   OR name LIKE '/etc/%'";
            
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

        // 4. Fetch Requires (Now with Versions!)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name, flags, epoch, version, release FROM requires WHERE name NOT LIKE 'rpmlib(%'";
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