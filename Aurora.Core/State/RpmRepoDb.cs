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

        // Determine host architecture for filtering
        _hostArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => "x86_64"
        };
    }

    public List<Package> GetAllPackages()
    {
        // pkgKey is the primary link between tables. Using long to prevent overflow.
        var packages = new Dictionary<long, Package>();

        // 1. Fetch Core Package Data (Filtered by Architecture)
        // We only want packages for our arch or architecture-independent (noarch)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT pkgKey, name, epoch, version, release, arch, 
                       summary, description, url, rpm_license, 
                       location_href, pkgId, size_package, size_installed
                FROM packages 
                WHERE arch = $arch OR arch = 'noarch'";
            
            cmd.Parameters.AddWithValue("$arch", _hostArch);
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pkgKey = reader.GetInt64(0);
                
                packages[pkgKey] = new Package
                {
                    Name = GetStringSafe(reader, 1),
                    Epoch = reader.IsDBNull(2) ? "0" : reader.GetValue(2).ToString() ?? "0",
                    Version = GetStringSafe(reader, 3),
                    Release = GetStringSafe(reader, 4),
                    Arch = GetStringSafe(reader, 5),
                    Summary = GetStringSafe(reader, 6),
                    Description = GetStringSafe(reader, 7),
                    Url = GetStringSafe(reader, 8),
                    License = GetStringSafe(reader, 9),
                    LocationHref = GetStringSafe(reader, 10),
                    Checksum = GetStringSafe(reader, 11),
                    Size = reader.GetInt64(12),
                    InstalledSize = reader.GetInt64(13)
                };
            }
        }

        // 2. Fetch Provides (Capabilities)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name FROM provides";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (packages.TryGetValue(reader.GetInt64(0), out var pkg))
                {
                    // This captures 'libtinfo.so.6()(64bit)' exactly as requested by bash
                    pkg.Provides.Add(reader.GetString(1)); 
                }
            }
        }

        // 3. Fetch Requires
        using (var cmd = _connection.CreateCommand())
        {
            // Filter out internal RPM capabilities that Aurora doesn't need to resolve
            cmd.CommandText = "SELECT pkgKey, name FROM requires WHERE name NOT LIKE 'rpmlib(%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (packages.TryGetValue(reader.GetInt64(0), out var pkg))
                {
                    pkg.Requires.Add(reader.GetString(1));
                }
            }
        }

        return new List<Package>(packages.Values);
    }

    private string GetStringSafe(SqliteDataReader reader, int col)
    {
        if (reader.IsDBNull(col)) return string.Empty;
        return reader.GetValue(col).ToString() ?? string.Empty;
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}