using System.Globalization;
using BIT.Data.Sync;
using BIT.Data.Sync.EventArgs;
using BIT.Data.Sync.Imp;
using Creatura.Cells.Sync;
using Microsoft.Data.Sqlite;

namespace Creatura.Cells.Hub;

/// <summary>What the hub has done with what it was sent since it started.</summary>
public sealed record HubCounts(long Stored, long Duplicates, long Rejected);

/// <summary>
/// The hub's delta store: one owner-only SQLite file.
/// <list type="bullet">
/// <item>Every pushed delta is parsed as a signed entry and verified against the owner keys the hub trusts
/// (<see cref="WorkLedger.Verify"/>). An entry that does not verify is never stored: the mesh admits machines, and
/// another machine on the mesh must not be able to fill the hub (C14).</item>
/// <item>An entry is stored once per (entry, hash), whoever pushes it and however often. The delta id is computed from
/// the entry, not taken from the sender.</item>
/// <item>Indexes are the row number, zero-padded, so they sort as strings and survive a restart (C15). The stock memory
/// store numbers from an in-memory counter that starts again at 1.</item>
/// </list>
/// The client cursors of <see cref="IDeltaStore"/> are kept too, but the hub's clients keep their own.
/// </summary>
public sealed class HubDeltaStore : DeltaStoreBase
{
    private readonly WorkLedger _verifier;
    private readonly SemaphoreSlim _write = new(1, 1);
    private long _stored, _duplicates, _rejected;

    public HubDeltaStore(string path, IEnumerable<string> trustedOwners) : base(new MemorySequenceService(new YearSequencePrefixStrategy()))
    {
        Path = System.IO.Path.GetFullPath(path);
        _verifier = new WorkLedger(trustedOwners);
        if (_verifier.TrustedOwners.Count == 0) throw new ArgumentException("a hub needs at least one trusted owner key", nameof(trustedOwners));
        if (!File.Exists(Path))
        {
            try { OwnerOnly.WriteNew(Path, []); } catch (IOException) when (File.Exists(Path)) { }
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS deltas (
                n INTEGER PRIMARY KEY AUTOINCREMENT,
                delta_id TEXT NOT NULL UNIQUE,
                identity TEXT NOT NULL,
                cell TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                epoch REAL NOT NULL,
                operation BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS cursors (identity TEXT PRIMARY KEY, processed TEXT, pushed TEXT);
            """;
        command.ExecuteNonQuery();
    }

    public string Path { get; }

    public HubCounts Counts => new(Interlocked.Read(ref _stored), Interlocked.Read(ref _duplicates), Interlocked.Read(ref _rejected));

    /// <summary>Raised for each delta refused, with the reason (the host logs it).</summary>
    public event Action<IDelta, string>? Refused;

    public override async Task SaveDeltasAsync(IEnumerable<IDelta> deltas, CancellationToken cancellationToken = default)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO deltas (delta_id, identity, cell, sequence, epoch, operation) VALUES ($id, $identity, $cell, $sequence, $epoch, $operation)";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var identity = command.Parameters.Add("$identity", SqliteType.Text);
            var cell = command.Parameters.Add("$cell", SqliteType.Text);
            var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
            var epoch = command.Parameters.Add("$epoch", SqliteType.Real);
            var operation = command.Parameters.Add("$operation", SqliteType.Blob);

            foreach (var delta in deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var saving = new SavingDeltaEventArgs(delta);
                OnSavingDelta(saving);
                if (saving.CustomHandled)
                {
                    OnSavedDelta(new SavedDeltaEventArgs(delta, true));
                    continue;
                }

                SignedEntry entry;
                try
                {
                    entry = LedgerDelta.Entry(delta);
                }
                catch (FormatException e)
                {
                    Refuse(delta, e.Message);
                    continue;
                }

                if (_verifier.Verify(entry) is { } reason)
                {
                    Refuse(delta, $"{entry.Id}: {reason}");
                    continue;
                }

                var canonical = LedgerDelta.From(entry);
                id.Value = canonical.DeltaId;
                identity.Value = string.IsNullOrEmpty(delta.Identity) ? entry.Cell : delta.Identity;
                cell.Value = entry.Cell;
                sequence.Value = entry.Sequence;
                epoch.Value = canonical.Epoch;
                operation.Value = canonical.Operation;
                if (command.ExecuteNonQuery() == 1) Interlocked.Increment(ref _stored);
                else Interlocked.Increment(ref _duplicates);
                OnSavedDelta(new SavedDeltaEventArgs(delta, false));
            }
            transaction.Commit();
        }
        finally
        {
            _write.Release();
        }
    }

    public override Task<IEnumerable<IDelta>> GetDeltasFromOtherClients(string startIndex, string identity, CancellationToken cancellationToken = default) =>
        Query("n > $start AND identity <> $identity", startIndex, identity);

    public override Task<IEnumerable<IDelta>> GetDeltasAsync(string startIndex, CancellationToken cancellationToken = default) =>
        Query("n > $start", startIndex, null);

    public override Task<IEnumerable<IDelta>> GetDeltasByIdentityAsync(string startIndex, string identity, CancellationToken cancellationToken = default) =>
        Query("n > $start AND identity = $identity", startIndex, identity);

    public override async Task<int> GetDeltaCountAsync(string startIndex, string identity, CancellationToken cancellationToken = default) =>
        (await GetDeltasByIdentityAsync(startIndex, identity, cancellationToken).ConfigureAwait(false)).Count();

    public override async Task<IDelta> GetDeltaAsync(string deltaId, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT n, delta_id, identity, epoch, operation FROM deltas WHERE delta_id = $id";
        command.Parameters.AddWithValue("$id", deltaId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return reader.Read() ? Read(reader) : null!;
    }

    public override Task<string> GetLastProcessedDeltaAsync(string identity, CancellationToken cancellationToken = default) => Cursor("processed", identity, "-1");

    public override Task SetLastProcessedDeltaAsync(string Index, string identity, CancellationToken cancellationToken = default) => SetCursor("processed", identity, Index);

    public override Task<string> GetLastPushedDeltaAsync(string identity, CancellationToken cancellationToken = default) => Cursor("pushed", identity, "");

    public override Task SetLastPushedDeltaAsync(string Index, string identity, CancellationToken cancellationToken = default) => SetCursor("pushed", identity, Index);

    public override Task ResetDeltasStatusAsync(string identity, CancellationToken cancellationToken = default) => Execute("DELETE FROM cursors WHERE identity = $identity", identity);

    public override Task PurgeDeltasAsync(string identity, CancellationToken cancellationToken = default) => Execute("DELETE FROM deltas WHERE identity = $identity", identity);

    public override Task PurgeDeltaStoreAsync(CancellationToken cancellationToken) => Execute("DELETE FROM deltas", null);

    /// <summary>The number in an index; anything that is not one ("-1", "", null, a year-prefixed index from another store) means from the start.</summary>
    public static long NumberOf(string? index) => long.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public static string IndexOf(long n) => n.ToString("D19", CultureInfo.InvariantCulture);

    private void Refuse(IDelta delta, string reason)
    {
        Interlocked.Increment(ref _rejected);
        Refused?.Invoke(delta, reason);
    }

    private async Task<IEnumerable<IDelta>> Query(string where, string startIndex, string? identity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT n, delta_id, identity, epoch, operation FROM deltas WHERE {where} ORDER BY n";
        command.Parameters.AddWithValue("$start", NumberOf(startIndex));
        if (identity is not null) command.Parameters.AddWithValue("$identity", identity);
        var result = new List<IDelta>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    private static Delta Read(SqliteDataReader reader)
    {
        var epoch = reader.GetDouble(3);
        return new Delta
        {
            Index = IndexOf(reader.GetInt64(0)),
            DeltaId = reader.GetString(1),
            Identity = reader.GetString(2),
            Epoch = epoch,
            Date = DateTimeOffset.FromUnixTimeMilliseconds((long)epoch).UtcDateTime,
            Operation = (byte[])reader.GetValue(4),
        };
    }

    private async Task<string> Cursor(string column, string identity, string none)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM cursors WHERE identity = $identity";
        command.Parameters.AddWithValue("$identity", identity);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string ?? none;
    }

    private Task SetCursor(string column, string identity, string index) =>
        Execute($"INSERT INTO cursors (identity, {column}) VALUES ($identity, $index) ON CONFLICT (identity) DO UPDATE SET {column} = excluded.{column}", identity, index);

    private async Task Execute(string sql, string? identity, string? index = null)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (identity is not null) command.Parameters.AddWithValue("$identity", identity);
            if (index is not null) command.Parameters.AddWithValue("$index", index);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        return connection;
    }
}
