using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Creatura.Cells.Sqlite;

/// <summary>
/// The work ledger in an owner-only SQLite file: one row per entry, keyed by (cell, sequence), in WAL mode so a
/// reader never waits for a writer. Since issue #88 it is the core's <see cref="VerifiedLedgerStore"/> over the
/// <see cref="SqliteLedgerEntryStore"/> provider; its public surface is unchanged.
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
    public const int Schema = SqliteLedgerEntryStore.Schema;

    public const string FileName = "ledger.db";

    private readonly VerifiedLedgerStore _store;

    static SqliteLedgerStore() => LedgerStoreProviders.Register("sqlite", For);

    public SqliteLedgerStore(string path)
    {
        Rows = new SqliteLedgerEntryStore(path);
        _store = new VerifiedLedgerStore(Rows);
    }

    /// <summary>The home's SQLite ledger, beside its JSON one.</summary>
    public static SqliteLedgerStore For(CellHome home) => new(System.IO.Path.Combine(home.Root, FileName));

    /// <summary>Registers the <c>sqlite</c> provider with <see cref="LedgerStoreProviders"/> (it is also registered on first use of this type).</summary>
    public static void Register() => LedgerStoreProviders.Register("sqlite", For);

    /// <summary>The provider underneath: the rows as SQLite holds them.</summary>
    public SqliteLedgerEntryStore Rows { get; }

    public string Path => Rows.Location;

    /// <summary>Check both signatures of every row on load, as a merge from a peer does.</summary>
    public bool VerifyOnLoad
    {
        get => _store.VerifyOnLoad;
        init => _store.VerifyOnLoad = value;
    }

    /// <summary>Rows the last <see cref="Save"/> inserted or replaced.</summary>
    public int LastSaveWrites => _store.LastSaveWrites;

    /// <summary>Entries the last <see cref="Save"/> sent to the database: the ones this store had not loaded or written.</summary>
    public int LastSaveSent => _store.LastSaveSent;

    public MergeResult LoadInto(WorkLedger ledger) => _store.LoadInto(ledger);

    public void Save(WorkLedger ledger) => _store.Save(ledger);

    /// <summary>The rows that match, checked for hash, form and id.</summary>
    public LedgerRead Read(LedgerQuery query) => _store.Read(query);

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
    public long Count() => Rows.Count();
}

/// <summary>
/// The SQLite ledger provider (issue #88): rows in an owner-only file, table <c>entries</c> keyed by (cell, sequence),
/// WAL mode, schema version in <c>PRAGMA user_version</c>. It stores rows as given and checks nothing about them; the
/// core does (<see cref="VerifiedLedgerStore"/>).
/// </summary>
public sealed class SqliteLedgerEntryStore(string path) : ILedgerEntryStore
{
    public const int Schema = 1;

    public string Location { get; } = System.IO.Path.GetFullPath(path);

    public IEnumerable<StoredEntry> Read(LedgerQuery query)
    {
        var rows = new List<StoredEntry>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = new List<string> { "sequence > $after" };
        command.Parameters.AddWithValue("$after", query.AfterSequence);
        if (query.Cell is not null)
        {
            where.Add("cell = $cell");
            command.Parameters.AddWithValue("$cell", query.Cell);
        }
        if (query.Kind is not null)
        {
            where.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", query.Kind);
        }
        command.CommandText = $"SELECT cell, sequence, hash, kind, timestamp, json FROM entries WHERE {string.Join(" AND ", where)} ORDER BY cell, sequence"
            + (query.Limit is { } limit ? $" LIMIT {Math.Max(0, limit)}" : "");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time);
            rows.Add(new StoredEntry(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), time, reader.GetString(5)));
        }
        return rows;
    }

    public int Write(IReadOnlyCollection<StoredEntry> rows)
    {
        if (rows.Count == 0) return 0;
        var writes = 0;
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
        foreach (var row in rows)
        {
            cell.Value = row.Cell;
            sequence.Value = row.Sequence;
            hash.Value = row.Hash;
            kind.Value = row.Kind;
            timestamp.Value = row.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            json.Value = row.Json;
            writes += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return writes;
    }

    public long Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries";
        return (long)command.ExecuteScalar()!;
    }

    private SqliteConnection Open()
    {
        if (!File.Exists(Location)) CreateOwnerOnly();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Location, Pooling = false }.ToString());
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
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
            OwnerOnly.WriteNew(Location, []);
        }
        catch (IOException) when (File.Exists(Location))
        {
            // Another process created it first.
        }
    }
}
