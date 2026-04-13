using Microsoft.Data.Sqlite;

namespace Aurora.Core.State;

/// <summary>
/// Represents a single recorded package transaction (install, remove, update).
/// </summary>
public record HistoryTransaction
{
    public long Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string Type { get; init; } = string.Empty;    // "install", "remove", "update"
    public string Status { get; init; } = "completed";
    public List<HistoryEntry> Entries { get; init; } = new();
}

/// <summary>
/// A single package change within a transaction.
/// </summary>
public record HistoryEntry
{
    public long Id { get; init; }
    public long TransactionId { get; init; }
    public string Action { get; init; } = string.Empty;  // "install", "remove", "upgrade"
    public string PackageName { get; init; } = string.Empty;
    public string Epoch { get; init; } = "0";
    public string? OldVersion { get; init; }
    public string? NewVersion { get; init; }
    public string Arch { get; init; } = string.Empty;
}

/// <summary>
/// Describes what a rollback operation would do.
/// </summary>
public record RollbackPlan
{
    public HistoryTransaction TargetTransaction { get; init; } = null!;
    public List<RollbackAction> Actions { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// A single action within a rollback plan.
/// </summary>
public record RollbackAction
{
    public string Type { get; init; } = string.Empty; // "remove", "install", "downgrade"
    public string PackageName { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Arch { get; init; }
    public bool Available { get; init; } = true;
}

/// <summary>
/// SQLite-backed transaction history store.
/// </summary>
public static class TransactionHistory
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Ensures the history schema exists in the given database file.
    /// </summary>
    public static async Task EnsureSchemaAsync(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS transactions (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp   TEXT NOT NULL,
                type        TEXT NOT NULL,
                status      TEXT NOT NULL DEFAULT 'completed'
            );

            CREATE TABLE IF NOT EXISTS transaction_entries (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                transaction_id  INTEGER NOT NULL,
                action          TEXT NOT NULL,
                package_name    TEXT NOT NULL,
                epoch           TEXT DEFAULT '0',
                old_version     TEXT,
                new_version     TEXT,
                arch            TEXT,
                FOREIGN KEY (transaction_id) REFERENCES transactions(id)
            );

            CREATE INDEX IF NOT EXISTS idx_entries_tid ON transaction_entries(transaction_id);
            CREATE INDEX IF NOT EXISTS idx_entries_pkg ON transaction_entries(package_name);
            CREATE INDEX IF NOT EXISTS idx_tx_timestamp ON transactions(timestamp);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Records a completed transaction with its package entries.
    /// </summary>
    public static async Task<long> RecordTransactionAsync(string dbPath, string type, IEnumerable<HistoryEntry> entries)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureSchemaAsync(dbPath);

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

            // Insert transaction row
            long txId;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO transactions (timestamp, type, status)
                    VALUES ($ts, $type, $status);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$type", type);
                cmd.Parameters.AddWithValue("$status", "completed");
                txId = (long)(await cmd.ExecuteScalarAsync())!;
            }

            // Insert entry rows
            foreach (var entry in entries)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO transaction_entries (transaction_id, action, package_name, epoch, old_version, new_version, arch)
                    VALUES ($tid, $action, $pkg, $epoch, $oldV, $newV, $arch)
                    """;
                cmd.Parameters.AddWithValue("$tid", txId);
                cmd.Parameters.AddWithValue("$action", entry.Action);
                cmd.Parameters.AddWithValue("$pkg", entry.PackageName);
                cmd.Parameters.AddWithValue("$epoch", entry.Epoch);
                cmd.Parameters.AddWithValue("$oldV", (object?)entry.OldVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$newV", (object?)entry.NewVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$arch", (object?)entry.Arch ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return txId;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets a paginated list of transactions (newest first). Entries are populated.
    /// </summary>
    public static async Task<List<HistoryTransaction>> GetHistoryAsync(string dbPath, int limit = 50, int offset = 0)
    {
        await EnsureSchemaAsync(dbPath);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        var result = new List<HistoryTransaction>();

        // Fetch transactions
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, timestamp, type, status
                FROM transactions
                ORDER BY id DESC
                LIMIT $limit OFFSET $offset
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new HistoryTransaction
                {
                    Id = reader.GetInt64(0),
                    Timestamp = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Type = reader.GetString(2),
                    Status = reader.GetString(3)
                });
            }
        }

        // Fetch entries for all fetched transactions
        if (result.Count > 0)
        {
            var ids = result.Select(t => t.Id).ToList();
            var idList = string.Join(",", ids);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, transaction_id, action, package_name, epoch, old_version, new_version, arch
                FROM transaction_entries
                WHERE transaction_id IN ({idList})
                ORDER BY id
                """;

            await using var reader = await cmd.ExecuteReaderAsync();
            var entryMap = new Dictionary<long, List<HistoryEntry>>();

            while (await reader.ReadAsync())
            {
                var entry = new HistoryEntry
                {
                    Id = reader.GetInt64(0),
                    TransactionId = reader.GetInt64(1),
                    Action = reader.GetString(2),
                    PackageName = reader.GetString(3),
                    Epoch = reader.IsDBNull(4) ? "0" : reader.GetString(4),
                    OldVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
                    NewVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Arch = reader.IsDBNull(7) ? null : reader.GetString(7)
                };

                if (!entryMap.TryGetValue(entry.TransactionId, out var list))
                {
                    list = new List<HistoryEntry>();
                    entryMap[entry.TransactionId] = list;
                }
                list.Add(entry);
            }

            foreach (var tx in result)
            {
                if (entryMap.TryGetValue(tx.Id, out var entries))
                {
                    // Mutation of init property via reflection alternative: use `with` expression
                    // Since we created these above, we can directly modify the Entries list
                    tx.Entries.AddRange(entries);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets a single transaction with all its entries.
    /// </summary>
    public static async Task<HistoryTransaction?> GetTransactionAsync(string dbPath, long id)
    {
        await EnsureSchemaAsync(dbPath);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        HistoryTransaction? tx = null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, timestamp, type, status
                FROM transactions
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                tx = new HistoryTransaction
                {
                    Id = reader.GetInt64(0),
                    Timestamp = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Type = reader.GetString(2),
                    Status = reader.GetString(3)
                };
            }
        }

        if (tx == null) return null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, transaction_id, action, package_name, epoch, old_version, new_version, arch
                FROM transaction_entries
                WHERE transaction_id = $tid
                ORDER BY id
                """;
            cmd.Parameters.AddWithValue("$tid", id);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tx.Entries.Add(new HistoryEntry
                {
                    Id = reader.GetInt64(0),
                    TransactionId = reader.GetInt64(1),
                    Action = reader.GetString(2),
                    PackageName = reader.GetString(3),
                    Epoch = reader.IsDBNull(4) ? "0" : reader.GetString(4),
                    OldVersion = reader.IsDBNull(5) ? null : reader.GetString(5),
                    NewVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Arch = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }
        }

        return tx;
    }

    /// <summary>
    /// Gets the total count of transactions.
    /// </summary>
    public static async Task<int> GetTransactionCountAsync(string dbPath)
    {
        await EnsureSchemaAsync(dbPath);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM transactions";
        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }
}