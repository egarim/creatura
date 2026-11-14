using System.Security.Cryptography;
using System.Text;

namespace Creatura.Cells;

/// <summary>
/// One stored row of the ledger, as a provider keeps it: the entry's place (<see cref="Cell"/>, <see cref="Sequence"/>),
/// the SHA-256 of its canonical JSON, its kind and time for queries, and the JSON itself. A provider treats
/// <see cref="Json"/> as opaque text: it never parses or checks it. The core does that (<see cref="VerifiedLedgerStore"/>).
/// </summary>
public sealed record StoredEntry(string Cell, long Sequence, string Hash, string Kind, DateTimeOffset Timestamp, string Json)
{
    /// <summary>The row for an entry a ledger admitted.</summary>
    public static StoredEntry From(SignedEntry entry)
    {
        var json = entry.ToJson();
        return new StoredEntry(entry.Cell, entry.Sequence, HashOf(json), entry.Kind.ToString(), entry.Timestamp.ToUniversalTime(), json);
    }

    /// <summary>SHA-256 of the text, lower-case hex: the hash a row is filed under.</summary>
    public static string HashOf(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    /// <summary>The entry id this row claims to hold, <c>cell#sequence</c>.</summary>
    public string Id => $"{Cell}#{Sequence}";
}

/// <summary>
/// Which rows to read. Every field narrows: <see cref="Cell"/> one cell's rows, <see cref="AfterSequence"/> only
/// sequences above it (the cursor of a reader that has the rest), <see cref="Kind"/> one entry kind,
/// <see cref="Limit"/> at most that many rows. Results come ordered by cell (ordinal) then sequence.
/// </summary>
public sealed record LedgerQuery(string? Cell = null, long AfterSequence = 0, string? Kind = null, int? Limit = null)
{
    public static LedgerQuery All { get; } = new();

    /// <summary>Whether a row passes every filter except <see cref="Limit"/>.</summary>
    public bool Matches(StoredEntry row) =>
        (Cell is null || string.Equals(row.Cell, Cell, StringComparison.Ordinal))
        && row.Sequence > AfterSequence
        && (Kind is null || string.Equals(row.Kind, Kind, StringComparison.Ordinal));
}

/// <summary>
/// The contract a ledger provider implements (issue #88): where rows live, not what they mean. Creatura's core checks
/// hashes, ids and signatures on the way in (<see cref="VerifiedLedgerStore"/>), so a provider can lose rows but cannot
/// make the core accept a forged one. Every provider runs the same conformance suite.
/// <list type="bullet">
/// <item><see cref="Write"/> upserts by (cell, sequence). When a place already holds another row, the row with the
/// smaller hash (ordinal) is kept, in whichever order the two arrive: the rule <see cref="WorkLedger.Merge"/> uses, so
/// writers that race end with the same row. Writing a row that is already held changes nothing. It returns the number
/// of rows inserted or replaced, and the rows are durable when it returns.</item>
/// <item><see cref="Read"/> returns the rows that match, ordered by cell (ordinal) then sequence.</item>
/// <item>Two instances on the same <see cref="Location"/> see each other's writes.</item>
/// </list>
/// </summary>
public interface ILedgerEntryStore
{
    /// <summary>Where the rows live: a file path, a connection description without secrets, a table name.</summary>
    string Location { get; }

    IEnumerable<StoredEntry> Read(LedgerQuery query);

    int Write(IReadOnlyCollection<StoredEntry> rows);

    long Count();
}

/// <summary>Rows read through the core: the entries that passed the checks, and the rows refused with the reason.</summary>
public sealed record LedgerRead(IReadOnlyList<SignedEntry> Entries, IReadOnlyList<Rejection> Rejected);

/// <summary>
/// Any <see cref="ILedgerEntryStore"/> as a cell's <see cref="ILedgerStore"/>, with the checks in the core: a row is
/// admitted only when its JSON matches its hash, parses as a signed entry and is filed under that entry's own id, and
/// then only through <see cref="WorkLedger.Merge"/> (or <see cref="WorkLedger.MergeVerified"/> when the provider is
/// the cell's own trusted file; see <see cref="VerifyOnLoad"/>). <see cref="Save"/> writes only the entries this store
/// has not loaded or written.
/// </summary>
public sealed class VerifiedLedgerStore(ILedgerEntryStore rows) : ILedgerStore
{
    private readonly Dictionary<(string Cell, long Sequence), SignedEntry> _held = [];

    public ILedgerEntryStore Rows { get; } = rows;

    public string Path => Rows.Location;

    /// <summary>
    /// Check both signatures of every row on load, as a merge from a peer does. Off by default only for a provider that
    /// is the cell's own owner-only file, where anyone who could write a row could also read the cell's key; a store
    /// that others can write (a server database, a shared service) should keep it on.
    /// </summary>
    public bool VerifyOnLoad { get; set; }

    /// <summary>Rows the last <see cref="Save"/> inserted or replaced.</summary>
    public int LastSaveWrites { get; private set; }

    /// <summary>Entries the last <see cref="Save"/> sent to the provider: the ones this store had not loaded or written.</summary>
    public int LastSaveSent { get; private set; }

    /// <summary>The rows that match, checked for hash, form and id (not yet for signatures: merging into a ledger does that).</summary>
    public LedgerRead Read(LedgerQuery query)
    {
        var entries = new List<SignedEntry>();
        var rejected = new List<Rejection>();
        foreach (var row in Rows.Read(query))
        {
            var checkedRow = Check(row, out var reason);
            if (checkedRow is null) rejected.Add(new Rejection(row.Id, reason!));
            else entries.Add(checkedRow);
        }
        return new LedgerRead(entries, rejected);
    }

    public MergeResult LoadInto(WorkLedger ledger)
    {
        var read = Read(LedgerQuery.All);
        var merged = VerifyOnLoad ? ledger.Merge(read.Entries) : ledger.MergeVerified(read.Entries);
        foreach (var entry in read.Entries)
        {
            if (ReferenceEquals(ledger.Get(entry.Cell, entry.Sequence), entry)) _held[(entry.Cell, entry.Sequence)] = entry;
        }
        return merged with { Rejected = [.. read.Rejected, .. merged.Rejected] };
    }

    public void Save(WorkLedger ledger)
    {
        var pending = ledger.Entries
            .Where(e => !(_held.TryGetValue((e.Cell, e.Sequence), out var held) && ReferenceEquals(held, e)))
            .ToList();
        LastSaveWrites = pending.Count == 0 ? 0 : Rows.Write(pending.Select(StoredEntry.From).ToList());
        foreach (var entry in pending) _held[(entry.Cell, entry.Sequence)] = entry;
        LastSaveSent = pending.Count;
    }

    private static SignedEntry? Check(StoredEntry row, out string? reason)
    {
        reason = null;
        if (!string.Equals(StoredEntry.HashOf(row.Json), row.Hash, StringComparison.Ordinal))
        {
            reason = "a row whose json does not match its hash";
            return null;
        }

        SignedEntry entry;
        try
        {
            entry = SignedEntry.Parse(row.Json);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or NotSupportedException or KeyNotFoundException or InvalidOperationException)
        {
            reason = "a row that is not a signed entry";
            return null;
        }

        if (entry.Id != row.Id)
        {
            reason = "a row filed under another cell or sequence";
            return null;
        }
        return entry;
    }
}

/// <summary>
/// The reference provider: rows in memory, shared by every instance created with the same location name in this
/// process. For tests, for embedding, and as the plainest statement of the contract.
/// </summary>
public sealed class MemoryLedgerEntryStore(string location = "memory") : ILedgerEntryStore
{
    private static readonly Dictionary<string, SortedDictionary<(string Cell, long Sequence), StoredEntry>> Shared = [];

    private static readonly IComparer<(string Cell, long Sequence)> Order = Comparer<(string Cell, long Sequence)>.Create((a, b) =>
    {
        var c = string.CompareOrdinal(a.Cell, b.Cell);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    });

    public string Location { get; } = location;

    private SortedDictionary<(string Cell, long Sequence), StoredEntry> Rows
    {
        get
        {
            if (!Shared.TryGetValue(Location, out var rows)) Shared[Location] = rows = new(Order);
            return rows;
        }
    }

    public IEnumerable<StoredEntry> Read(LedgerQuery query)
    {
        lock (Shared)
        {
            var matched = Rows.Values.Where(query.Matches);
            return (query.Limit is { } limit ? matched.Take(limit) : matched).ToList();
        }
    }

    public int Write(IReadOnlyCollection<StoredEntry> rows)
    {
        lock (Shared)
        {
            var writes = 0;
            foreach (var row in rows)
            {
                var key = (row.Cell, row.Sequence);
                if (Rows.TryGetValue(key, out var held) && string.CompareOrdinal(row.Hash, held.Hash) >= 0) continue;
                Rows[key] = row;
                writes++;
            }
            return writes;
        }
    }

    public long Count()
    {
        lock (Shared) return Rows.Count;
    }

    /// <summary>Forget every row held under this location (tests).</summary>
    public void Clear()
    {
        lock (Shared) Shared.Remove(Location);
    }
}

/// <summary>
/// The JSON ledger file as a provider: the same format as <see cref="JsonLedgerStore"/>
/// (<c>{"schema":1,"entries":[...]}</c>, one canonical entry per line), so the two read each other's files. A write
/// reads the file, applies the upsert rule and replaces the file whole through a rename. Writers in different
/// processes must hold the home's lock, as every JSON ledger writer already does.
/// <para>
/// The format has no hash column: a row's hash is computed from its JSON on read, so an edited row always "matches its
/// hash". Only the signature check catches it, so wrap this provider with <see cref="VerifiedLedgerStore.VerifyOnLoad"/>
/// on, as <see cref="JsonLedgerStore"/> does by always verifying.
/// </para>
/// </summary>
public sealed class JsonLedgerEntryStore(string path) : ILedgerEntryStore
{
    public string Location { get; } = System.IO.Path.GetFullPath(path);

    public IEnumerable<StoredEntry> Read(LedgerQuery query)
    {
        var matched = Load().Values.Where(query.Matches);
        return (query.Limit is { } limit ? matched.Take(limit) : matched).ToList();
    }

    public int Write(IReadOnlyCollection<StoredEntry> rows)
    {
        var held = Load();
        var writes = 0;
        foreach (var row in rows)
        {
            var key = (row.Cell, row.Sequence);
            if (held.TryGetValue(key, out var existing) && string.CompareOrdinal(row.Hash, existing.Hash) >= 0) continue;
            held[key] = row;
            writes++;
        }
        if (writes == 0) return 0;

        var text = new StringBuilder();
        text.Append("{\"schema\":").Append(JsonLedgerStore.Schema).Append(",\"entries\":[");
        var first = true;
        foreach (var row in held.Values)
        {
            text.Append(first ? "\n" : ",\n").Append(row.Json);
            first = false;
        }
        text.Append("\n]}\n");
        OwnerOnly.Replace(Location, Encoding.UTF8.GetBytes(text.ToString()));
        return writes;
    }

    public long Count() => Load().Count;

    /// <summary>
    /// The file's rows, ordered. A row's place, kind and time are read from its JSON without checking it; the core checks
    /// it on load. A row too broken to place is kept under a place of its own (<c>?</c>, its index) so the core can refuse
    /// it by name instead of the provider dropping it silently.
    /// </summary>
    private SortedDictionary<(string Cell, long Sequence), StoredEntry> Load()
    {
        var rows = new SortedDictionary<(string Cell, long Sequence), StoredEntry>(Comparer<(string Cell, long Sequence)>.Create((a, b) =>
        {
            var c = string.CompareOrdinal(a.Cell, b.Cell);
            return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
        }));
        if (!File.Exists(Location)) return rows;

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Location));
        var root = document.RootElement;
        if (root.TryGetProperty("schema", out var schema) && schema.GetInt32() > JsonLedgerStore.Schema)
            throw new FormatException($"ledger schema {schema.GetInt32()} is newer than {JsonLedgerStore.Schema}");

        var index = 0L;
        foreach (var element in root.GetProperty("entries").EnumerateArray())
        {
            index++;
            var json = element.GetRawText();
            string cell = "?";
            long sequence = -index;
            string kind = "";
            DateTimeOffset time = default;
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (element.TryGetProperty("cell", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String) cell = c.GetString()!;
                if (element.TryGetProperty("sequence", out var s) && s.TryGetInt64(out var n)) sequence = n;
                if (element.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String) kind = k.GetString()!;
                if (element.TryGetProperty("timestamp", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String && DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)) time = parsed;
            }
            var row = new StoredEntry(cell, sequence, StoredEntry.HashOf(json), kind, time, json);
            var key = (cell, sequence);
            if (!rows.TryGetValue(key, out var existing) || string.CompareOrdinal(row.Hash, existing.Hash) < 0) rows[key] = row;
        }
        return rows;
    }
}

/// <summary>
/// The ledger providers a home can use, by name (issue #88). <c>json</c> is built in; other assemblies register theirs
/// (the SQLite package registers <c>sqlite</c>; a private app can register its own), so choosing a store needs no change
/// to Creatura. A factory receives the home and returns its <see cref="ILedgerStore"/>.
/// </summary>
public static class LedgerStoreProviders
{
    private static readonly Dictionary<string, Func<CellHome, ILedgerStore>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["json"] = home => home.JsonLedger,
    };

    /// <summary>The registered names, sorted.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            lock (Factories) return [.. Factories.Keys.Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Registers (or replaces) a provider under a name.</summary>
    public static void Register(string name, Func<CellHome, ILedgerStore> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        lock (Factories) Factories[name.Trim()] = factory;
    }

    /// <summary>The factory registered under the name; <c>json</c> when the name is null. Unknown names throw, listing the known ones.</summary>
    public static Func<CellHome, ILedgerStore> Resolve(string? name)
    {
        lock (Factories)
        {
            if (Factories.TryGetValue(name?.Trim() ?? "json", out var factory)) return factory;
        }
        throw new ArgumentException($"no ledger store named '{name}'; known stores: {string.Join(", ", Names)}");
    }
}
