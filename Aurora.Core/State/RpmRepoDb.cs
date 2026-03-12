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

    public List<Package> GetAllPackages(string repoId)
    {
        var packages = new Dictionary<long, Package>();

        // 1. Fetch Core Package Data
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM packages WHERE arch = $arch OR arch = 'noarch'";
            cmd.Parameters.AddWithValue("$arch", _hostArch);
            
            using var reader = cmd.ExecuteReader();
            // Map ordinals once for performance
            int idKey = reader.GetOrdinal("pkgKey");
            int idName = reader.GetOrdinal("name");
            int idEpoch = reader.GetOrdinal("epoch");
            int idVer = reader.GetOrdinal("version");
            int idRel = reader.GetOrdinal("release");
            int idArch = reader.GetOrdinal("arch");
            int idLoc = reader.GetOrdinal("location_href");
            int idId = reader.GetOrdinal("pkgId"); // Checksum
            int idSizeP = reader.GetOrdinal("size_package");

            while (reader.Read())
            {
                long pkgKey = reader.GetInt64(idKey);
                packages[pkgKey] = new Package
                {
                    Name = reader.GetString(idName),
                    RepositoryId = repoId, 
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

        // 2. Fetch Provides (Capabilities)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name FROM provides";
            using var reader = cmd.ExecuteReader();
            int idKey = reader.GetOrdinal("pkgKey");
            int idName = reader.GetOrdinal("name");

            while (reader.Read())
            {
                if (packages.TryGetValue(reader.GetInt64(idKey), out var pkg))
                {
                    pkg.Provides.Add(reader.GetString(idName)); 
                }
            }
        }

        // 3. Fetch Requires
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pkgKey, name FROM requires WHERE name NOT LIKE 'rpmlib(%'";
            using var reader = cmd.ExecuteReader();
            int idKey = reader.GetOrdinal("pkgKey");
            int idName = reader.GetOrdinal("name");

            while (reader.Read())
            {
                if (packages.TryGetValue(reader.GetInt64(idKey), out var pkg))
                {
                    pkg.Requires.Add(reader.GetString(idName));
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