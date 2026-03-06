using Microsoft.Data.Sqlite;
using Aurora.Core.Models;

namespace Aurora.Core.State;

public class RpmRepoDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public RpmRepoDb(string sqliteFilePath)
    {
        if (!File.Exists(sqliteFilePath)) throw new FileNotFoundException("Repo DB not found", sqliteFilePath);
        
        _connection = new SqliteConnection($"Data Source={sqliteFilePath};Mode=ReadOnly;");
        _connection.Open();
    }

    public List<Package> GetAllPackages()
    {
        var packages = new Dictionary<string, Package>();

        // 1. Fetch Core Package Data
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT pkgKey, name, epoch, version, release, arch, 
                       summary, description, url, rpm_license, 
                       location_href, pkgId, size_package, size_installed
                FROM packages";
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pkgKey = reader.GetInt32(0).ToString();
                packages[pkgKey] = new Package
                {
                    Name = reader.GetString(1),
                    Epoch = reader.GetString(2),
                    Version = reader.GetString(3),
                    Release = reader.GetString(4),
                    Arch = reader.GetString(5),
                    Summary = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    Description = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    Url = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    License = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    LocationHref = reader.GetString(10),
                    Checksum = reader.GetString(11), // pkgId is the sha256 checksum in rpm repos
                    Size = reader.GetInt64(12),
                    InstalledSize = reader.GetInt64(13)
                };
            }
        }

        // 2. Fetch Provides
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name, flags, epoch, version, release FROM provides";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetInt32(0).ToString();
                if (packages.TryGetValue(key, out var pkg))
                {
                    var name = reader.GetString(1);
                    // Formatting capabilities (e.g. libX.so = 1.0)
                    // RPM flags: EQ (EQ), GE (GE), LE (LE).
                    // We'll simplify and just add the name for now, exact solver matching comes later.
                    pkg.Provides.Add(name); 
                }
            }
        }

        // 3. Fetch Requires
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name FROM requires WHERE name NOT LIKE 'rpmlib(%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetInt32(0).ToString();
                if (packages.TryGetValue(key, out var pkg))
                {
                    pkg.Requires.Add(reader.GetString(1));
                }
            }
        }

        return packages.Values.ToList();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}