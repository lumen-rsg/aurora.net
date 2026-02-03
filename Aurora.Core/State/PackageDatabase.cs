using Microsoft.Data.Sqlite;
using Aurora.Core.Models;
using Aurora.Core.Logging;

namespace Aurora.Core.State;

public class PackageDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public PackageDatabase(string dbPath = "/var/lib/aurora/aurora.db")
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeTables();
    }

    private void InitializeTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS packages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                version TEXT NOT NULL,
                arch TEXT NOT NULL,
                description TEXT,
                install_date TEXT,
                install_reason TEXT,
                is_broken INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS files (
                package_id INTEGER,
                path TEXT NOT NULL,
                FOREIGN KEY(package_id) REFERENCES packages(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS dependencies (
                package_id INTEGER,
                dep_name TEXT NOT NULL,
                type TEXT NOT NULL, -- 'dep', 'conflict'
                FOREIGN KEY(package_id) REFERENCES packages(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_pkg_name ON packages(name);
        ";
        cmd.ExecuteNonQuery();
    }

    public int GetPackageCount(SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM packages";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    // Replace the existing GetAllPackages with this optimized version
    public List<Package> GetAllPackages(SqliteTransaction? tx = null)
    {
        var packages = new Dictionary<long, Package>();
    
        // 1. Fetch all packages
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, name, version, arch, description, is_broken FROM packages";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                packages[id] = new Package 
                { 
                    Name = reader.GetString(1),
                    Version = reader.GetString(2),
                    Arch = reader.GetString(3),
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                    IsBroken = reader.GetInt32(5) == 1
                };
            }
        }

        if (packages.Count == 0) return new List<Package>();

        // 2. Fetch all dependencies/conflicts in one query
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT package_id, dep_name, type FROM dependencies";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pid = reader.GetInt64(0);
                if (packages.TryGetValue(pid, out var pkg))
                {
                    var name = reader.GetString(1);
                    var type = reader.GetString(2);
                
                    if (type == "dep") pkg.Depends.Add(name);
                    else if (type == "conflict") pkg.Conflicts.Add(name);
                }
            }
        }

        // 3. Optional: Fetch files if needed (usually not needed for simple listing, 
        // strictly lazily load files or fetch in batch only when strictly required).
    
        return packages.Values.ToList();
    }

    public Package? GetPackage(string packageName, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, name, version, arch, description, is_broken FROM packages WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", packageName);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var id = reader.GetInt64(0);
        var pkg = new Package
        {
            Name = reader.GetString(1),
            Version = reader.GetString(2),
            Arch = reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            IsBroken = reader.GetInt32(5) == 1
        };
        
        pkg.Files = GetFiles(id, tx);
        pkg.Depends = GetDeps(id, "dep", tx);
        pkg.Conflicts = GetDeps(id, "conflict", tx); // FIX: Load Conflicts

        return pkg;
    }

    public List<Package> GetBrokenPackages(SqliteTransaction? tx = null)
    {
        var list = new List<Package>();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, name, version, arch FROM packages WHERE is_broken = 1";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var pkg = new Package 
            { 
                Name = reader.GetString(1),
                Version = reader.GetString(2),
                Arch = reader.GetString(3),
                IsBroken = true
            };
            pkg.Depends = GetDeps(id, "dep", tx); 
            pkg.Conflicts = GetDeps(id, "conflict", tx); // FIX: Load Conflicts
            list.Add(pkg);
        }
        return list;
    }

    public void RegisterPackage(Package pkg, SqliteTransaction? tx = null)
    {
        using var cmdPkg = _connection.CreateCommand();
        cmdPkg.Transaction = tx;
        cmdPkg.CommandText = @"
            INSERT INTO packages (name, version, arch, description, install_date, install_reason, is_broken)
            VALUES ($name, $ver, $arch, $desc, $date, $reason, $broken);
            SELECT last_insert_rowid();";

        cmdPkg.Parameters.AddWithValue("$name", pkg.Name);
        cmdPkg.Parameters.AddWithValue("$ver", pkg.Version);
        cmdPkg.Parameters.AddWithValue("$arch", pkg.Arch);
        cmdPkg.Parameters.AddWithValue("$desc", pkg.Description ?? (object)DBNull.Value);
        cmdPkg.Parameters.AddWithValue("$date", DateTime.UtcNow.ToString("o"));
        cmdPkg.Parameters.AddWithValue("$reason", pkg.InstallReason);
        cmdPkg.Parameters.AddWithValue("$broken", pkg.IsBroken ? 1 : 0);

        long pkgId = (long)cmdPkg.ExecuteScalar()!;

        // Insert Files
        using var cmdFile = _connection.CreateCommand();
        cmdFile.Transaction = tx;
        cmdFile.CommandText = "INSERT INTO files (package_id, path) VALUES ($id, $path)";
        var pId = cmdFile.Parameters.AddWithValue("$id", pkgId);
        var pPath = cmdFile.Parameters.AddWithValue("$path", "");
        foreach (var file in pkg.Files) { pPath.Value = file; cmdFile.ExecuteNonQuery(); }

        // Insert Deps AND Conflicts
        using var cmdDep = _connection.CreateCommand();
        cmdDep.Transaction = tx;
        cmdDep.CommandText = "INSERT INTO dependencies (package_id, dep_name, type) VALUES ($id, $name, $type)";
        var pDepId = cmdDep.Parameters.AddWithValue("$id", pkgId);
        var pDepName = cmdDep.Parameters.AddWithValue("$name", "");
        var pDepType = cmdDep.Parameters.AddWithValue("$type", "");

        foreach (var dep in pkg.Depends)
        {
            pDepName.Value = dep;
            pDepType.Value = "dep";
            cmdDep.ExecuteNonQuery();
        }
        
        // FIX: Insert Conflicts
        foreach (var conflict in pkg.Conflicts)
        {
            pDepName.Value = conflict;
            pDepType.Value = "conflict";
            cmdDep.ExecuteNonQuery();
        }
    }

    public void RemovePackage(string packageName, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM packages WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", packageName);
        cmd.ExecuteNonQuery();
    }

    public void MarkPackageHealthy(string packageName, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE packages SET is_broken = 0 WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", packageName);
        cmd.ExecuteNonQuery();
    }

    private List<string> GetFiles(long packageId, SqliteTransaction? tx)
    {
        var list = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path FROM files WHERE package_id = $id";
        cmd.Parameters.AddWithValue("$id", packageId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }
    
    public bool IsInstalled(string packageName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT count(1) FROM packages WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", packageName);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private List<string> GetDeps(long packageId, string type, SqliteTransaction? tx)
    {
        var list = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT dep_name FROM dependencies WHERE package_id = $id AND type = $type";
        cmd.Parameters.AddWithValue("$id", packageId);
        cmd.Parameters.AddWithValue("$type", type);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    public SqliteTransaction BeginTransaction() => _connection.BeginTransaction();
    public void Dispose() { _connection.Close(); _connection.Dispose(); }
}