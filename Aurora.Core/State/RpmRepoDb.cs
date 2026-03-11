using Microsoft.Data.Sqlite;
using Aurora.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace Aurora.Core.State;

public class RpmRepoDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public RpmRepoDb(string sqliteFilePath)
    {
        if (!File.Exists(sqliteFilePath)) throw new FileNotFoundException("Repo DB not found", sqliteFilePath);
        
        // Ensure shared cache/read-only mode for speed
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
                
                // NULL-Safe data retrieval
                packages[pkgKey] = new Package
                {
                    Name = reader.GetString(1),
                    // Epoch is frequently NULL in RPM databases
                    Epoch = reader.IsDBNull(2) ? "0" : reader.GetValue(2).ToString() ?? "0",
                    Version = reader.GetString(3),
                    Release = reader.GetString(4),
                    Arch = reader.GetString(5),
                    Summary = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    Description = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    Url = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    License = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    LocationHref = reader.GetString(10),
                    Checksum = reader.GetString(11), // pkgId is the sha256
                    Size = reader.GetInt64(12),
                    InstalledSize = reader.GetInt64(13)
                };
            }
        }

        // 2. Fetch Provides
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name FROM provides";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetInt32(0).ToString();
                if (packages.TryGetValue(key, out var pkg))
                {
                    pkg.Provides.Add(reader.GetString(1)); 
                }
            }
        }

        // 3. Fetch Requires
        using (var cmd = _connection.CreateCommand())
        {
            // We ignore internal rpmlib requirements for the solver
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

        return new List<Package>(packages.Values);
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}