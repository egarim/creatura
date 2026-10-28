using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BIT.Data.Sync;
using BIT.Data.Sync.Client;
using BIT.Data.Sync.Imp;
using BIT.Data.Sync.Server;
using Creatura.Cells.Sqlite;
using Creatura.Cells.Sync;
using Microsoft.Data.Sqlite;

namespace Creatura.Cells.Tests;

/// <summary>
/// The pre-registered tests of slice 3 (docs/design-cells.md, "Slice 3"), written before the code: the ledger in
/// SQLite (C6 to C8) and synced through SyncFramework's server and in-memory client, offline (C9 to C12).
/// </summary>
public sealed class Slice3Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    private const string ServerNode = "ledger";

    private static (CellHome Home, OwnerKey Owner) NewHome(TempHome dir)
    {
        var home = new CellHome(dir.Path);
        return (home, home.EnsureOwner(out _));
    }

    private static LocalCell NewCell(CellHome home, string raw, string creatura) =>
        home.Endorse(home.OpenCell(new FakeVessel(raw), Position.InProcess(creatura), creatura));

    /// <summary>A hello, a claim, a progress row and a heartbeat: four entries of the cell's own.</summary>
    private static void Work(WorkLedger ledger, LocalCell cell, string goal, int minute)
    {
        if (ledger.HelloOf(cell.Id) is null) ledger.Append(cell, new HelloPayload(cell.Identity), T0.AddMinutes(minute));
        var claim = ledger.Append(cell, new ClaimPayload(goal) { Tool = "claude-code" }, T0.AddMinutes(minute));
        ledger.Append(cell, new ProgressPayload(claim.Id, "turn 1: Bash x2"), T0.AddMinutes(minute + 1));
        ledger.Append(cell, new HeartbeatPayload(0.3), T0.AddMinutes(minute + 2));
    }

    private static SyncFrameworkServer NewServer() => new(new SyncServerNode(new MemoryDeltaStore(), null, ServerNode));

    private static LedgerSyncNode Node(SyncFrameworkServer server, WorkLedger ledger, LocalCell cell) =>
        new(ledger, cell.Id, new SyncFrameworkInMemoryClient(server, ServerNode));

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The goal of a claim entry rewritten after signing: the signature no longer covers it.</summary>
    private static string Tampered(SignedEntry claim, string goal)
    {
        var node = JsonNode.Parse(claim.ToJson())!;
        node["payload"]!["goal"] = goal;
        return node.ToJsonString();
    }

    /// <summary>C6: a ledger saved to SQLite and loaded back holds the same entries; a save after one append writes one row.</summary>
    [Fact]
    public void C6_SqliteRoundTripsTheLedgerAndWritesOnlyWhatIsNew()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var cell = NewCell(home, "raw-vessel-a", "alpha");
        var ledger = new WorkLedger(owner.PublicKey);
        Work(ledger, cell, "slice 3", 0);
        Work(ledger, cell, "the measurement", 10);

        var path = Path.Combine(dir.Path, "ledger.db");
        var store = new SqliteLedgerStore(path);
        store.Save(ledger);
        Assert.Equal(ledger.Count, store.LastSaveWrites);
        Assert.True(OwnerOnly.IsOwnerOnly(path), "the database is not owner-only");

        var reopened = new SqliteLedgerStore(path);
        var loaded = new WorkLedger(owner.PublicKey);
        var result = reopened.LoadInto(loaded);
        Assert.Empty(result.Rejected);
        Assert.Equal(ledger.Digest(), loaded.Digest());
        Assert.Equal(ledger.Entries, loaded.Entries);

        loaded.Append(cell, new ProgressPayload(loaded.OpenClaims()[^1].Id, "turn 2: Edit x1"), T0.AddMinutes(30));
        reopened.Save(loaded);
        Assert.Equal(1, reopened.LastSaveWrites);
        Assert.Equal(1, reopened.LastSaveSent);

        // A store that has loaded nothing still saves correctly: what the file already holds is not written again.
        var cold = new SqliteLedgerStore(path);
        cold.Save(loaded);
        Assert.Equal(loaded.Count, cold.LastSaveSent);
        Assert.Equal(0, cold.LastSaveWrites);

        var again = new WorkLedger(owner.PublicKey);
        new SqliteLedgerStore(path).LoadInto(again);
        Assert.Equal(loaded.Digest(), again.Digest());
    }

    /// <summary>
    /// Added after the pre-registered set, when a mutation of the conflict rule survived it: two writers that saved
    /// different entries for one (cell, sequence) leave the row a merge of both would keep, in either order.
    /// </summary>
    [Fact]
    public void AConflictInTheDatabaseSettlesAsTheLedgerDoes()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var cell = NewCell(home, "raw-vessel-a", "alpha");
        var first = new WorkLedger(owner.PublicKey);
        var second = new WorkLedger(owner.PublicKey);
        var one = first.Append(cell, new ClaimPayload("one writer's goal"), T0);
        var other = second.Append(cell, new ClaimPayload("the other writer's goal"), T0);
        Assert.Equal(one.Sequence, other.Sequence);
        var merged = new WorkLedger(owner.PublicKey);
        merged.Merge([one, other]);
        var kept = merged.Get(one.Cell, one.Sequence);

        foreach (var (a, b, name) in new[] { (first, second, "ab.db"), (second, first, "ba.db") })
        {
            var path = Path.Combine(dir.Path, name);
            new SqliteLedgerStore(path).Save(a);
            new SqliteLedgerStore(path).Save(b);
            var loaded = new WorkLedger(owner.PublicKey);
            new SqliteLedgerStore(path).LoadInto(loaded);
            Assert.Equal(kept, Assert.Single(loaded.Entries));
        }
    }

    /// <summary>
    /// C7: a row altered in the database is left out on load; with VerifyOnLoad, a row re-hashed after the change is
    /// rejected too. Without VerifyOnLoad the store trusts its own owner-only file, and the test says so.
    /// </summary>
    [Fact]
    public void C7_AnAlteredRowIsLeftOut()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var cell = NewCell(home, "raw-vessel-a", "alpha");
        var ledger = new WorkLedger(owner.PublicKey);
        Work(ledger, cell, "slice 3", 0);
        var claim = ledger.Entries.Single(e => e.Kind == EntryKind.Claim);

        var path = Path.Combine(dir.Path, "ledger.db");
        new SqliteLedgerStore(path).Save(ledger);

        void Rewrite(string json, string? hash)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = hash is null
                ? "UPDATE entries SET json = $json WHERE cell = $cell AND sequence = $sequence"
                : "UPDATE entries SET json = $json, hash = $hash WHERE cell = $cell AND sequence = $sequence";
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$cell", claim.Cell);
            command.Parameters.AddWithValue("$sequence", claim.Sequence);
            if (hash is not null) command.Parameters.AddWithValue("$hash", hash);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var altered = Tampered(claim, "something else entirely");
        Rewrite(altered, hash: null);
        var first = new WorkLedger(owner.PublicKey);
        var result = new SqliteLedgerStore(path).LoadInto(first);
        Assert.Equal(claim.Id, Assert.Single(result.Rejected).EntryId);
        Assert.Equal(ledger.Count - 1, first.Count);
        Assert.DoesNotContain(first.Entries, e => e.Kind == EntryKind.Claim);

        Rewrite(altered, Sha256(altered));
        var trusting = new WorkLedger(owner.PublicKey);
        new SqliteLedgerStore(path).LoadInto(trusting);
        Assert.Contains(trusting.Entries, e => e.Kind == EntryKind.Claim && e.Body<ClaimPayload>().Goal == "something else entirely");

        var verifying = new WorkLedger(owner.PublicKey);
        var checkedResult = new SqliteLedgerStore(path) { VerifyOnLoad = true }.LoadInto(verifying);
        Assert.Equal(claim.Id, Assert.Single(checkedResult.Rejected).EntryId);
        Assert.DoesNotContain(verifying.Entries, e => e.Kind == EntryKind.Claim);
    }

    /// <summary>C8: an empty database imports ledger.json with the same digest and refuses what the JSON load refuses.</summary>
    [Fact]
    public void C8_AnEmptyDatabaseImportsTheJsonLedger()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var cell = NewCell(home, "raw-vessel-a", "alpha");
        var ledger = new WorkLedger(owner.PublicKey);
        Work(ledger, cell, "slice 3", 0);
        var claim = ledger.Entries.Single(e => e.Kind == EntryKind.Claim);
        var json = new JsonLedgerStore(Path.Combine(dir.Path, "ledger.json"));
        json.Save(ledger);

        // One entry altered by hand in the JSON file: both loads must refuse it.
        var text = File.ReadAllText(json.Path).Replace(claim.ToJson(), Tampered(claim, "edited by hand"), StringComparison.Ordinal);
        File.WriteAllText(json.Path, text);
        var fromJson = new WorkLedger(owner.PublicKey);
        var jsonResult = json.LoadInto(fromJson);
        Assert.Single(jsonResult.Rejected);

        var sqlite = new SqliteLedgerStore(Path.Combine(dir.Path, "ledger.db"));
        var imported = sqlite.ImportIfEmpty(json, [owner.PublicKey]);
        Assert.NotNull(imported);
        Assert.Equal(claim.Id, Assert.Single(imported.Rejected).EntryId);
        Assert.Equal(fromJson.Count, imported.Added);

        var fromSqlite = new WorkLedger(owner.PublicKey);
        Assert.Empty(sqlite.LoadInto(fromSqlite).Rejected);
        Assert.Equal(fromJson.Digest(), fromSqlite.Digest());

        Assert.Null(sqlite.ImportIfEmpty(json, [owner.PublicKey]));
    }

    /// <summary>C9: two cells that each claim work converge through SyncFrameworkServer and the in-memory client, in either sync order.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task C9_TwoCellsConvergeThroughTheSyncServer(bool alphaFirst)
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        Work(ledgerA, a, "migration step 16", 0);
        Work(ledgerB, b, "cells slice 3", 1);

        var server = NewServer();
        var nodeA = Node(server, ledgerA, a);
        var nodeB = Node(server, ledgerB, b);
        var order = alphaFirst ? new[] { nodeA, nodeB, nodeA, nodeB } : [nodeB, nodeA, nodeB, nodeA];
        foreach (var node in order)
        {
            var report = await node.SyncAsync(CancellationToken.None);
            Assert.True(report.Pushed, report.Message);
            Assert.Empty(report.Pulled.Rejected);
        }

        Assert.Equal(8, ledgerA.Count);
        Assert.Equal(ledgerA.Entries, ledgerB.Entries);
        Assert.Equal(ledgerA.Digest(), ledgerB.Digest());

        // A third sync round moves nothing.
        var quiet = await nodeA.SyncAsync(CancellationToken.None);
        Assert.Equal(0, quiet.Staged);
        Assert.Equal(0, quiet.Pulled.Added);
    }

    /// <summary>
    /// C10: a delta altered after signing, one that is not an entry, and one endorsed by an untrusted owner never reach
    /// a ledger; a valid entry relayed under another identity is accepted. The server relays all of them unchanged.
    /// </summary>
    [Fact]
    public async Task C10_TheProcessorMergesOnlyEntriesThatVerify()
    {
        using var dir = new TempHome();
        using var strangerDir = new TempHome();
        var (home, owner) = NewHome(dir);
        var (strangerHome, _) = NewHome(strangerDir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        using var stranger = NewCell(strangerHome, "raw-vessel-s", "mallory");

        var ledgerA = new WorkLedger(owner.PublicKey);
        Work(ledgerA, a, "the real goal", 0);
        var claim = ledgerA.Entries.Single(e => e.Kind == EntryKind.Claim);
        var unsent = ledgerA.Append(a, new ReleasePayload(claim.Id, "relayed by a peer"), T0.AddMinutes(9));

        var strangerLedger = new WorkLedger(stranger.Identity.Endorsement!.OwnerPublicKey);
        var foreign = strangerLedger.Append(stranger, new HelloPayload(stranger.Identity), T0);

        var server = NewServer();
        var client = new SyncFrameworkInMemoryClient(server, ServerNode);
        var bad = new List<IDelta>
        {
            new Delta(a.Id, Encoding.UTF8.GetBytes(Tampered(claim, "rm -rf ~"))),
            new Delta(a.Id, Encoding.UTF8.GetBytes("rm -rf ~")),
            LedgerDelta.From(foreign),
        };
        Assert.True((await client.PushAsync(bad, CancellationToken.None)).Success);
        var relayed = LedgerDelta.From(unsent);
        relayed.Identity = "a-relay-that-is-not-alpha";
        Assert.True((await client.PushAsync([relayed], CancellationToken.None)).Success);

        var ledgerB = new WorkLedger(owner.PublicKey);
        var nodeB = Node(server, ledgerB, b);
        var report = await nodeB.SyncAsync(CancellationToken.None);

        Assert.Equal(3, report.Pulled.Rejected.Count);
        Assert.Equal(1, report.Pulled.Added);
        Assert.Equal(unsent, Assert.Single(ledgerB.Entries));
        Assert.DoesNotContain(ledgerB.Entries, e => e.Cell == stranger.Id);
        Assert.DoesNotContain(ledgerB.Entries, e => e.Kind == EntryKind.Claim);
    }

    /// <summary>C11: a cell offline during three claims catches up with one sync and ends identical.</summary>
    [Fact]
    public async Task C11_AnOfflineCellCatchesUpWithOneSync()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        var server = NewServer();
        var nodeA = Node(server, ledgerA, a);
        var nodeB = Node(server, ledgerB, b);

        Work(ledgerA, a, "before b went offline", 0);
        await nodeA.SyncAsync(CancellationToken.None);
        await nodeB.SyncAsync(CancellationToken.None);

        for (var i = 1; i <= 3; i++)
        {
            Work(ledgerA, a, $"claim {i} while b is offline", i * 10);
            await nodeA.SyncAsync(CancellationToken.None);
        }

        var report = await nodeB.SyncAsync(CancellationToken.None);
        Assert.Equal(9, report.Pulled.Added);
        Assert.Equal(0, report.Pulled.Duplicates);
        Assert.Equal(ledgerA.Digest(), ledgerB.Digest());
        Assert.Equal(13, ledgerB.Count);
    }

    /// <summary>C12: a cell that pulls before it ever pushed still pushes all its own entries.</summary>
    [Fact]
    public async Task C12_PullingFirstDoesNotSkipTheCellsOwnEntries()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        var server = NewServer();
        var nodeA = Node(server, ledgerA, a);
        var nodeB = Node(server, ledgerB, b);

        Work(ledgerA, a, "alpha's work", 0);
        Work(ledgerA, a, "more of alpha's work", 5);
        await nodeA.SyncAsync(CancellationToken.None);

        // b has written entries but pulls before its first push; the server's index is ahead of b's own by then.
        Work(ledgerB, b, "beta's work", 1);
        await nodeB.PullAsync(CancellationToken.None);
        var report = await nodeB.SyncAsync(CancellationToken.None);
        Assert.True(report.Pushed, report.Message);

        await nodeA.SyncAsync(CancellationToken.None);
        Assert.Equal(11, ledgerA.Count);
        Assert.Equal(ledgerA.Digest(), ledgerB.Digest());
    }

    /// <summary>The command with <c>--store sqlite</c>: the first run imports ledger.json, later runs write only the database.</summary>
    [Fact]
    public void TheCommandKeepsTheLedgerInSqliteWhenAsked()
    {
        using var dir = new TempHome();
        var source = new FakeVessel("raw-cli");
        string Run(params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", dir.Path, "--no-mesh"], output, output, source);
            Assert.True(code == 0, $"{string.Join(' ', args)} exited {code}: {output}");
            return output.ToString();
        }

        Run("cell", "init");
        Run("cell", "claim", "--goal", "written to json");
        var json = File.ReadAllText(Path.Combine(dir.Path, "ledger.json"));

        Run("cell", "claim", "--goal", "written to sqlite", "--store", "sqlite");
        Assert.Equal(json, File.ReadAllText(Path.Combine(dir.Path, "ledger.json")));
        Assert.Equal(3, new SqliteLedgerStore(Path.Combine(dir.Path, "ledger.db")).Count());

        var fromSqlite = Run("cell", "ledger", "--store", "sqlite");
        Assert.Contains("written to json", fromSqlite);
        Assert.Contains("written to sqlite", fromSqlite);
        Assert.DoesNotContain("written to sqlite", Run("cell", "ledger"));

        var bad = new StringWriter();
        Assert.Equal(2, CellCommand.Run(["cell", "ledger", "--store", "csv", "--home", dir.Path, "--no-mesh"], bad, bad, source));
        Assert.Contains("--store sqlite", Run("cell", "hooks-config", "--store", "sqlite"));
        Assert.DoesNotContain("--store", Run("cell", "hooks-config"));
    }
}
