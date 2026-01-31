using Aurora.Core.Logging;
using Aurora.Core.State;
using Microsoft.Data.Sqlite;
using Aurora.Core.Models;

namespace Aurora.Core.Logic;

public class Transaction : IDisposable
{
    public readonly PackageDatabase Database;
    private readonly LockManager _lock;
    private SqliteTransaction? _sqlTx;
    private bool _committed = false;
    private readonly string _journalPath;

    public Transaction(string dbPath)
    {
        _lock = new LockManager(Path.Combine(Path.GetDirectoryName(dbPath)!, "aurora.lock"));
        _lock.Acquire();

        Database = new PackageDatabase(dbPath);
        _sqlTx = Database.BeginTransaction();

        _journalPath = dbPath + ".journal";

        if (File.Exists(_journalPath))
        {
            _sqlTx.Dispose();
            Database.Dispose();
            _lock.Dispose();
            throw new InvalidOperationException(
                $"System is in an inconsistent state (Journal found at {_journalPath}). " +
                "You must run recovery before starting a new transaction."
            );
        }

        // Initialize empty journal
        File.WriteAllText(_journalPath, string.Empty);
        AuLogger.Info("Transaction started.");
    }
    
    public void AppendToJournal(string path)
    {
        // Logic: Append line-by-line. 
        // This is O(1), extremely fast, and crash-safe. 
        // If power fails after this line, the path is safely on disk.
        File.AppendAllText(_journalPath, path + Environment.NewLine);
    }

    public void Commit()
    {
        _sqlTx?.Commit();
        if (File.Exists(_journalPath)) File.Delete(_journalPath);
        _committed = true;
        AuLogger.Info("Transaction committed.");
    }

    public void Rollback()
    {
        try { _sqlTx?.Rollback(); } catch { }

        if (File.Exists(_journalPath))
        {
            // Read line-by-line to handle the format used in AppendToJournal
            var lines = File.ReadAllLines(_journalPath);
            foreach (var file in lines)
            {
                var cleanPath = file.Trim();
                if (string.IsNullOrWhiteSpace(cleanPath)) continue;

                if (File.Exists(cleanPath))
                {
                    try { File.Delete(cleanPath); } catch { }
                    
                    // Cleanup empty dirs
                    var dir = Path.GetDirectoryName(cleanPath);
                    if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        try { Directory.Delete(dir); } catch { }
                    }
                }
            }
            File.Delete(_journalPath);
        }
    }

    // Wrappers
    public int GetPackageCount() => Database.GetPackageCount(_sqlTx);
    public List<Package> GetAllPackages() => Database.GetAllPackages(_sqlTx);
    public Package? GetPackage(string name) => Database.GetPackage(name, _sqlTx);
    public void RegisterPackage(Package pkg) => Database.RegisterPackage(pkg, _sqlTx);
    public void RemovePackage(string name) => Database.RemovePackage(name, _sqlTx);
    public List<Package> GetBrokenPackages() => Database.GetBrokenPackages(_sqlTx);
    public void MarkPackageHealthy(string name) => Database.MarkPackageHealthy(name, _sqlTx);

    public void Dispose()
    {
        if (!_committed) Rollback();
        _sqlTx?.Dispose();
        Database.Dispose();
        _lock.Dispose();
    }

    public static bool HasPendingRecovery(string dbPath) => File.Exists(dbPath + ".journal");
    
    public static void RunRecovery(string dbPath)
    {
        // Re-use the logic from Rollback, but standalone
        var journalPath = dbPath + ".journal";
        if (!File.Exists(journalPath)) return;

        using var lockManager = new LockManager(Path.Combine(Path.GetDirectoryName(dbPath)!, "aurora.lock"));
        lockManager.Acquire();

        var lines = File.ReadAllLines(journalPath);
        foreach (var file in lines)
        {
            var cleanPath = file.Trim();
            if (!string.IsNullOrWhiteSpace(cleanPath) && File.Exists(cleanPath))
            {
                try { File.Delete(cleanPath); } catch { }
            }
        }
        File.Delete(journalPath);
    }
}