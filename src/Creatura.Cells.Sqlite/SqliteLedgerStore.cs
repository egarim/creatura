using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Creatura.Cells.Sqlite;

/// <summary>
/// The work ledger in an owner-only SQLite file: one row per entry, keyed by (cell, sequence), in WAL mode so a
/// reader never waits for a writer.
/// <para>
/// An entry is verified once, when it enters a ledger from outside (a peer, a relay, the JSON file). This store holds
/// only what a ledger admitted, so <see cref="LoadInto"/> checks each row's hash and the owner's trust and skips the
/// two signature checks, which are what a load of the JSON file spends its time on. The file is the boundary: anyone
/// who can write it as the owner can also read the cell's key. <see cref="VerifyOnLoad"/> checks everything.
/// </para>
/// <para>
/// <see cref="Save"/> writes only the entries this store has not loaded or written, and settles a conflict for one
/// (cell, sequence) by keeping the smaller hash, the rule <see cref="WorkLedger.Merge"/> uses, so two writers that
/// race end with the same row. Callers that append still take the home's lock, so two writers never pick the same
/// sequence.
/// </para>
/// </summary>
public sealed class SqliteLedgerStore : ILedgerStore
{
    public const int Schema = 1;

    public const string FileName = "ledger.db";

    private readonly Dictionary<(string Cell, long Sequence), SignedEntry> _held = [];

    public SqliteLedgerStore(string path) => Path = System.IO.Path.GetFullPath(path);

    /// <summary>The home's SQLite ledger, beside its JSON one.</summary>
    public static SqliteLedgerStore For(CellHome home) => new(System.IO.Path.Combine(home.Root, FileName));

    public string Path { get; }

    /// <summary>Check both signatures of every row on load, as a merge from a peer does.</summary>
    public bool VerifyOnLoad { get; init; }

    /// <summary>Rows the last <see cref="Save"/> inserted or replaced.</summary>
    public int LastSaveWrites { get; private set; }

    /// <summary>Entries the last <see cref="Save"/> sent to the database: the ones this store had not loaded or written.</summary>
    public int LastSaveSent { get; private set; }

    public MergeResult LoadInto(WorkLedger ledger)
    {
        var rows = new List<SignedEntry>();
        var rejected = new List<Rejection>();
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT cell, sequence, hash, json FROM entries ORDER BY cell, sequence";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = $"{reader.GetString(0)}#{reader.GetInt64(1)}";
                var json = reader.GetString(3);
                if (!string.Equals(HashOf(json), reader.GetString(2), StringComparison.Ordinal))
                {
                    rejected.Add(new Rejection(id, "a row whose json does not match its hash"));
                    continue;
                }

                SignedEntry entry;
                try
                {
                    entry = SignedEntry.Parse(json);
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or NotSupportedException)
                {
                    rejected.Add(new Rejection(id, "a row that is not a signed entry"));
                    continue;
                }

                if (entry.Id != id)
                {
                    rejected.Add(new Rejection(id, "a row filed under another cell or sequence"));
                    continue;
                }
                rows.Add(entry);
            }
        }

        var merged = VerifyOnLoad ? ledger.Merge(rows) : ledger.MergeVerified(rows);
        foreach (var entry in rows)
        {
            if (ReferenceEquals(ledger.Get(entry.Cell, entry.Sequence), entry)) _held[(entry.Cell, entry.Sequence)] = entry;
        }
        return merged with { Rejected = [.. rejected, .. merged.Rejected] };
    }

    public void Save(WorkLedger ledger)
    {
        var pending = ledger.Entries
            .Where(e => !(_held.TryGetValue((e.Cell, e.Sequence), out var held) && ReferenceEquals(held, e)))
            .ToList();
        var writes = 0;
        if (pending.Count > 0)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO entries (cell, sequence, hash, kind, timestamp, json) VALUES ($cell, $sequence, $hash, $kind, $timestamp, $json)
                ON CONFLICT (cell, sequence) DO UPDATE SET hash = excluded.hash, kind = excluded.kind, timestamp = excluded.timestamp, json = excluded.json
                WHERE excluded.hash < entries.hash
                """;
            var cell = command.Parameters.Add("$cell", SqliteType.Text);
            var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
            var hash = command.Parameters.Add("$hash", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
            var json = command.Parameters.Add("$json", SqliteType.Text);
            command.Prepare();
            foreach (var entry in pending)
            {
                var text = entry.ToJson();
                cell.Value = entry.Cell;
                sequence.Value = entry.Sequence;
                hash.Value = HashOf(text);
                kind.Value = entry.Kind.ToString();
                timestamp.Value = entry.Timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                json.Value = text;
                writes += command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        foreach (var entry in pending) _held[(entry.Cell, entry.Sequence)] = entry;
        LastSaveWrites = writes;
        LastSaveSent = pending.Count;
    }

    /// <summary>
    /// When the database holds no entry and the JSON ledger exists, merges the JSON file into a fresh ledger that
    /// trusts <paramref name="trustedOwners"/> (every signature checked, as for any arrival) and saves what it
    /// admitted. Null when the database already holds entries or there is nothing to import.
    /// </summary>
    public MergeResult? ImportIfEmpty(ILedgerStore source, IEnumerable<string> trustedOwners)
    {
        if (!File.Exists(source.Path) || Count() > 0) return null;
        var ledger = new WorkLedger(trustedOwners);
        var result = source.LoadInto(ledger);
        Save(ledger);
        return result;
    }

    /// <summary>The rows the database holds.</summary>
    public long Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries";
        return (long)command.ExecuteScalar()!;
    }

    private SqliteConnection Open()
    {
        if (!File.Exists(Path)) CreateOwnerOnly();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS entries (
                cell TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                hash TEXT NOT NULL,
                kind TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY (cell, sequence)
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version > Schema) throw new FormatException($"ledger schema {version} is newer than {Schema}");
        if (version < Schema)
        {
            using var set = connection.CreateCommand();
            set.CommandText = $"PRAGMA user_version = {Schema}";
            set.ExecuteNonQuery();
        }
        return connection;
    }

    /// <summary>An empty file with owner-only permissions, so SQLite's own files (the WAL and shared memory) inherit them.</summary>
    private void CreateOwnerOnly()
    {
        try
        {
            OwnerOnly.WriteNew(Path, []);
        }
        catch (IOException) when (File.Exists(Path))
        {
            // Another process created it first.
        }
    }

    private static string HashOf(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}
