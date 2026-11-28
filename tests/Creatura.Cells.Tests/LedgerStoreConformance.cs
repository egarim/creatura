using System.Diagnostics;
using System.Text.Json.Nodes;
using Creatura.Cells.Sqlite;

namespace Creatura.Cells.Tests;

/// <summary>
/// The conformance suite every ledger provider runs (issue #88, check P1). A provider's test class derives from this one
/// and says only how to create its store on a location; every test below must pass unchanged. E-tests check the
/// provider contract with plain rows (a provider never parses a row); V-tests check the core over the provider with real
/// signed entries.
/// </summary>
public abstract class LedgerStoreConformance : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    protected TempHome Dir { get; } = new();

    /// <summary>A fresh location for this test (a file path, a name).</summary>
    protected abstract string NewLocation();

    /// <summary>A store on the location; two calls with the same location must see each other's writes.</summary>
    protected abstract ILedgerEntryStore Create(string location);

    public virtual void Dispose()
    {
        Dir.Dispose();
        GC.SuppressFinalize(this);
    }

    private static StoredEntry Row(string cell, long sequence, string kind = "Progress", string? json = null)
    {
        json ??= $"{{\"cell\":\"{cell}\",\"sequence\":{sequence},\"kind\":\"{kind}\",\"note\":\"{Guid.NewGuid():N}\"}}";
        return new StoredEntry(cell, sequence, StoredEntry.HashOf(json), kind, T0.AddMinutes(sequence), json);
    }

    [Fact]
    public void E1_AnEmptyStoreReadsNothing()
    {
        var store = Create(NewLocation());
        Assert.Empty(store.Read(LedgerQuery.All));
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void E2_RowsComeBackOrderedByCellThenSequence()
    {
        var store = Create(NewLocation());
        var rows = new[] { Row("b", 2), Row("a", 10), Row("b", 1), Row("a", 2), Row("A", 1) };
        Assert.Equal(5, store.Write(rows));

        var read = store.Read(LedgerQuery.All).ToList();
        Assert.Equal(["A#1", "a#2", "a#10", "b#1", "b#2"], read.Select(r => r.Id));
        var original = rows.ToDictionary(r => r.Id);
        Assert.All(read, r => Assert.Equal(original[r.Id].Json, r.Json));
        Assert.All(read, r => Assert.Equal(original[r.Id].Hash, r.Hash));
        Assert.Equal(5, store.Count());
    }

    [Fact]
    public void E3_WritingARowItAlreadyHoldsChangesNothing()
    {
        var store = Create(NewLocation());
        var rows = new[] { Row("a", 1), Row("a", 2) };
        Assert.Equal(2, store.Write(rows));
        Assert.Equal(0, store.Write(rows));
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void E4_AConflictKeepsTheSmallerHashInEitherOrder()
    {
        var one = Row("a", 1);
        var two = Row("a", 1);
        var (small, large) = string.CompareOrdinal(one.Hash, two.Hash) < 0 ? (one, two) : (two, one);

        var first = Create(NewLocation());
        first.Write([large]);
        Assert.Equal(1, first.Write([small]));
        Assert.Equal(small.Hash, Assert.Single(first.Read(LedgerQuery.All)).Hash);

        var second = Create(NewLocation());
        second.Write([small]);
        Assert.Equal(0, second.Write([large]));
        Assert.Equal(small.Hash, Assert.Single(second.Read(LedgerQuery.All)).Hash);
    }

    [Fact]
    public void E5_QueriesFilterByCellPositionKindAndLimit()
    {
        var store = Create(NewLocation());
        store.Write([Row("a", 1, "Hello"), Row("a", 2, "Claim"), Row("a", 3, "Progress"), Row("a", 4, "Progress"), Row("b", 1, "Hello"), Row("b", 2, "Progress")]);

        Assert.Equal(["a#1", "a#2", "a#3", "a#4"], store.Read(new LedgerQuery(Cell: "a")).Select(r => r.Id));
        Assert.Equal(["a#3", "a#4"], store.Read(new LedgerQuery(Cell: "a", AfterSequence: 2)).Select(r => r.Id));
        Assert.Equal(["a#3", "a#4", "b#2"], store.Read(new LedgerQuery(Kind: "Progress")).Select(r => r.Id));
        Assert.Equal(["a#1", "a#2"], store.Read(new LedgerQuery(Limit: 2)).Select(r => r.Id));
        Assert.Equal(["b#2"], store.Read(new LedgerQuery(Cell: "b", Kind: "Progress", AfterSequence: 1, Limit: 5)).Select(r => r.Id));
        Assert.Empty(store.Read(new LedgerQuery(Cell: "nobody")));
    }

    [Fact]
    public void E6_RowsSurviveANewInstanceOnTheSameLocation()
    {
        var location = NewLocation();
        Create(location).Write([Row("a", 1), Row("a", 2)]);
        var reopened = Create(location);
        Assert.Equal(2, reopened.Count());
        Assert.Equal(["a#1", "a#2"], reopened.Read(LedgerQuery.All).Select(r => r.Id));
    }

    [Fact]
    public void E7_TwoWritersOnOneLocationEndWithTheSameRows()
    {
        var location = NewLocation();
        var shared = Row("c", 1);
        var rival = Row("c", 1);
        var kept = string.CompareOrdinal(shared.Hash, rival.Hash) < 0 ? shared : rival;

        var writerA = Create(location);
        var writerB = Create(location);
        writerA.Write([Row("a", 1), shared]);
        writerB.Write([Row("b", 1), rival]);
        writerA.Write([Row("a", 2)]);

        var fromA = writerA.Read(LedgerQuery.All).Select(r => (r.Id, r.Hash)).ToList();
        var fromB = writerB.Read(LedgerQuery.All).Select(r => (r.Id, r.Hash)).ToList();
        Assert.Equal(fromA, fromB);
        Assert.Equal(["a#1", "a#2", "b#1", "c#1"], fromA.Select(r => r.Id));
        Assert.Equal(kept.Hash, fromA.Single(r => r.Id == "c#1").Hash);
    }

    [Fact]
    public void E8_FiveThousandRowsWriteAndReadInReasonableTime()
    {
        var store = Create(NewLocation());
        var rows = Enumerable.Range(1, 5000).Select(i => Row($"cell-{i % 7}", i)).ToList();
        var clock = Stopwatch.StartNew();
        Assert.Equal(5000, store.Write(rows));
        Assert.Equal(5000, store.Read(LedgerQuery.All).Count());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"5,000 rows took {clock.Elapsed}");
    }

    // ---- The core over the provider, with real signed entries ----

    private (WorkLedger Ledger, string Owner) SignedLedger()
    {
        var home = new CellHome(Dir.Path);
        var owner = home.EnsureOwner(out _);
        var cell = home.Endorse(home.OpenCell(new FakeVessel("raw-vessel-" + Guid.NewGuid().ToString("N")[..6]), Position.InProcess("alpha"), "alpha"));
        var ledger = new WorkLedger(owner.PublicKey);
        ledger.Append(cell, new HelloPayload(cell.Identity), T0);
        var claim = ledger.Append(cell, new ClaimPayload("provider conformance") { Tool = "claude-code" }, T0.AddMinutes(1));
        ledger.Append(cell, new ProgressPayload(claim.Id, "turn 1"), T0.AddMinutes(2));
        ledger.Append(cell, new HeartbeatPayload(0.2), T0.AddMinutes(3));
        cell.Dispose();
        return (ledger, owner.PublicKey);
    }

    [Fact]
    public void V1_ALedgerSavedAndLoadedKeepsItsDigest()
    {
        var (ledger, owner) = SignedLedger();
        var location = NewLocation();
        var store = new VerifiedLedgerStore(Create(location)) { VerifyOnLoad = true };
        store.Save(ledger);
        Assert.Equal(ledger.Count, store.LastSaveWrites);

        var loaded = new WorkLedger(owner);
        var result = new VerifiedLedgerStore(Create(location)) { VerifyOnLoad = true }.LoadInto(loaded);
        Assert.Empty(result.Rejected);
        Assert.Equal(ledger.Digest(), loaded.Digest());
    }

    [Fact]
    public void V2_ATamperedRowIsRefusedByNameAndTheRestLoads()
    {
        var (ledger, owner) = SignedLedger();
        var location = NewLocation();
        var rows = Create(location);
        var claim = ledger.Entries.First(e => e.Kind == EntryKind.Claim);
        var node = JsonNode.Parse(claim.ToJson())!;
        node["payload"]!["goal"] = "rewritten after signing";
        var forged = node.ToJsonString();
        // The forger stores the edited JSON under the original hash, and a second copy rehashed so the hash matches.
        rows.Write(ledger.Entries.Where(e => e.Kind != EntryKind.Claim).Select(StoredEntry.From).ToList());
        rows.Write([new StoredEntry(claim.Cell, claim.Sequence, claim.Hash(), claim.Kind.ToString(), claim.Timestamp, forged)]);

        var loaded = new WorkLedger(owner);
        var result = new VerifiedLedgerStore(Create(location)) { VerifyOnLoad = true }.LoadInto(loaded);
        // Caught by whichever check sees it first: the hash where the provider keeps one, the signature where it
        // doesn't (the JSON file has no hash column).
        var refused = Assert.Single(result.Rejected);
        Assert.Equal(claim.Id, refused.EntryId);
        Assert.True(refused.Reason.Contains("hash", StringComparison.Ordinal) || refused.Reason.Contains("signature", StringComparison.Ordinal), refused.Reason);
        Assert.Equal(ledger.Count - 1, loaded.Count);

        var rehashed = Create(NewLocation());
        rehashed.Write([new StoredEntry(claim.Cell, claim.Sequence, StoredEntry.HashOf(forged), claim.Kind.ToString(), claim.Timestamp, forged)]);
        var second = new WorkLedger(owner);
        var verdict = new VerifiedLedgerStore(rehashed) { VerifyOnLoad = true }.LoadInto(second);
        Assert.Equal(claim.Id, Assert.Single(verdict.Rejected).EntryId);
        Assert.Equal(0, second.Count);
    }

    [Fact]
    public void V3_ASaveAfterOneAppendWritesOneRow()
    {
        var home = new CellHome(Dir.Path);
        var owner = home.EnsureOwner(out _);
        using var cell = home.Endorse(home.OpenCell(new FakeVessel("raw-vessel-v3"), Position.InProcess("beta"), "beta"));
        var ledger = new WorkLedger(owner.PublicKey);
        ledger.Append(cell, new HelloPayload(cell.Identity), T0);
        var store = new VerifiedLedgerStore(Create(NewLocation()));
        store.Save(ledger);
        ledger.Append(cell, new HeartbeatPayload(0.4), T0.AddMinutes(1));
        store.Save(ledger);
        Assert.Equal(1, store.LastSaveSent);
        Assert.Equal(1, store.LastSaveWrites);
        store.Save(ledger);
        Assert.Equal(0, store.LastSaveSent);
    }

    [Fact]
    public void V4_ReadReturnsCheckedEntriesForAQuery()
    {
        var (ledger, _) = SignedLedger();
        var store = new VerifiedLedgerStore(Create(NewLocation()));
        store.Save(ledger);
        var read = store.Read(new LedgerQuery(Kind: "Claim"));
        Assert.Empty(read.Rejected);
        Assert.Equal(ledger.Entries.Single(e => e.Kind == EntryKind.Claim).Id, Assert.Single(read.Entries).Id);
    }
}

public sealed class MemoryLedgerStoreConformance : LedgerStoreConformance
{
    private readonly List<MemoryLedgerEntryStore> _made = [];

    protected override string NewLocation() => "conformance-" + Guid.NewGuid().ToString("N");

    protected override ILedgerEntryStore Create(string location)
    {
        var store = new MemoryLedgerEntryStore(location);
        _made.Add(store);
        return store;
    }

    public override void Dispose()
    {
        foreach (var store in _made) store.Clear();
        base.Dispose();
    }
}

public sealed class JsonLedgerStoreConformance : LedgerStoreConformance
{
    protected override string NewLocation() => Path.Combine(Dir.Path, Guid.NewGuid().ToString("N")[..8] + ".json");

    protected override ILedgerEntryStore Create(string location) => new JsonLedgerEntryStore(location);

    [Fact]
    public void J1_TheProviderAndTheClassicJsonStoreReadEachOthersFiles()
    {
        var home = new CellHome(Dir.Path);
        var owner = home.EnsureOwner(out _);
        using var cell = home.Endorse(home.OpenCell(new FakeVessel("raw-vessel-j1"), Position.InProcess("gamma"), "gamma"));
        var ledger = new WorkLedger(owner.PublicKey);
        ledger.Append(cell, new HelloPayload(cell.Identity), DateTimeOffset.UnixEpoch.AddYears(56));
        ledger.Append(cell, new HeartbeatPayload(0.1), DateTimeOffset.UnixEpoch.AddYears(56).AddMinutes(1));

        var path = NewLocation();
        new JsonLedgerStore(path).Save(ledger);
        var viaProvider = new WorkLedger(owner.PublicKey);
        Assert.Empty(new VerifiedLedgerStore(new JsonLedgerEntryStore(path)) { VerifyOnLoad = true }.LoadInto(viaProvider).Rejected);
        Assert.Equal(ledger.Digest(), viaProvider.Digest());

        var other = NewLocation();
        new VerifiedLedgerStore(new JsonLedgerEntryStore(other)).Save(ledger);
        var viaClassic = new WorkLedger(owner.PublicKey);
        Assert.Empty(new JsonLedgerStore(other).LoadInto(viaClassic).Rejected);
        Assert.Equal(ledger.Digest(), viaClassic.Digest());
    }
}

public sealed class SqliteLedgerStoreConformance : LedgerStoreConformance
{
    protected override string NewLocation() => Path.Combine(Dir.Path, Guid.NewGuid().ToString("N")[..8] + ".db");

    protected override ILedgerEntryStore Create(string location) => new SqliteLedgerEntryStore(location);
}

/// <summary>The provider registry (issue #88, check P5): names resolve, unknown names list the known ones, a new provider needs no change to Creatura.</summary>
public sealed class LedgerStoreProviderTests
{
    [Fact]
    public void R1_JsonIsBuiltInAndSqliteRegistersItself()
    {
        SqliteLedgerStore.Register();
        Assert.Contains("json", LedgerStoreProviders.Names);
        Assert.Contains("sqlite", LedgerStoreProviders.Names);
        using var dir = new TempHome();
        var home = new CellHome(dir.Path);
        Assert.IsType<JsonLedgerStore>(LedgerStoreProviders.Resolve(null)(home));
        Assert.IsType<SqliteLedgerStore>(LedgerStoreProviders.Resolve("SQLite")(home));
    }

    [Fact]
    public void R2_AProviderRegisteredFromOutsideResolvesByName()
    {
        var name = "test-memory-" + Guid.NewGuid().ToString("N")[..6];
        LedgerStoreProviders.Register(name, home => new VerifiedLedgerStore(new MemoryLedgerEntryStore(home.Root)));
        using var dir = new TempHome();
        Assert.IsType<VerifiedLedgerStore>(LedgerStoreProviders.Resolve(name)(new CellHome(dir.Path)));
    }

    [Fact]
    public void R3_AnUnknownNameListsTheKnownOnes()
    {
        var error = Assert.Throws<ArgumentException>(() => LedgerStoreProviders.Resolve("no-such-store"));
        Assert.Contains("json", error.Message, StringComparison.Ordinal);
    }
}
