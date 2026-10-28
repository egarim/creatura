using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BIT.Data.Sync;
using Creatura.Cells.Hub;
using Creatura.Cells.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Creatura.Cells.Tests;

/// <summary>
/// The pre-registered tests of slice 4 (docs/design-cells.md, "Slice 4"): the ledger hub over real HTTP on loopback,
/// sync cursors kept between runs, and a vessel joining without the owner key.
/// </summary>
public sealed class Slice4Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A running hub on a free loopback port.</summary>
    private sealed class RunningHub : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private RunningHub(WebApplication app, Uri url)
        {
            _app = app;
            Url = url;
        }

        public Uri Url { get; }

        public HubDeltaStore Store => _app.Services.GetRequiredService<HubDeltaStore>();

        public static async Task<RunningHub> StartAsync(string database, params string[] trustedOwners)
        {
            var app = HubApp.Create(new HubOptions(database, trustedOwners, "http://127.0.0.1:0"));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new RunningHub(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static (CellHome Home, OwnerKey Owner) NewHome(TempHome dir)
    {
        var home = new CellHome(dir.Path);
        return (home, home.EnsureOwner(out _));
    }

    private static LocalCell NewCell(CellHome home, string raw, string creatura) =>
        home.Endorse(home.OpenCell(new FakeVessel(raw), Position.InProcess(creatura), creatura));

    private static void Work(WorkLedger ledger, LocalCell cell, string goal, int minute)
    {
        if (ledger.HelloOf(cell.Id) is null) ledger.Append(cell, new HelloPayload(cell.Identity), T0.AddMinutes(minute));
        var claim = ledger.Append(cell, new ClaimPayload(goal) { Tool = "claude-code" }, T0.AddMinutes(minute));
        ledger.Append(cell, new ProgressPayload(claim.Id, "turn 1: Bash x2"), T0.AddMinutes(minute + 1));
        ledger.Append(cell, new HeartbeatPayload(0.3), T0.AddMinutes(minute + 2));
    }

    private static LedgerSyncNode Node(RunningHub hub, WorkLedger ledger, LocalCell cell, LedgerSyncState? state = null) =>
        new(ledger, cell.Id, LedgerHub.Http(hub.Url), state);

    /// <summary>C13: two homes converge through the hub over real HTTP.</summary>
    [Fact]
    public async Task C13_TwoHomesConvergeThroughTheHubOverHttp()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        Work(ledgerA, a, "on the air", 0);
        Work(ledgerB, b, "on the surface", 1);

        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        var nodeA = Node(hub, ledgerA, a);
        var nodeB = Node(hub, ledgerB, b);
        foreach (var node in new[] { nodeA, nodeB, nodeA, nodeB })
        {
            var report = await node.SyncAsync(CancellationToken.None);
            Assert.True(report.Pushed, report.Message);
            Assert.Empty(report.Pulled.Rejected);
        }

        Assert.Equal(8, ledgerA.Count);
        Assert.Equal(ledgerA.Digest(), ledgerB.Digest());
        Assert.Equal(new HubCounts(8, 0, 0), hub.Store.Counts);
    }

    /// <summary>C14: the hub never stores an entry that does not verify or whose owner it does not trust; a valid one pushed twice is stored once.</summary>
    [Fact]
    public async Task C14_TheHubStoresOnlyEntriesThatVerifyAndEachOnce()
    {
        using var dir = new TempHome();
        using var strangerDir = new TempHome();
        var (home, owner) = NewHome(dir);
        var (strangerHome, _) = NewHome(strangerDir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var stranger = NewCell(strangerHome, "raw-vessel-s", "mallory");
        var ledger = new WorkLedger(owner.PublicKey);
        Work(ledger, a, "the real goal", 0);
        var claim = ledger.Entries.Single(e => e.Kind == EntryKind.Claim);
        var foreign = new WorkLedger(stranger.Identity.Endorsement!.OwnerPublicKey).Append(stranger, new HelloPayload(stranger.Identity), T0);

        var node = JsonNode.Parse(claim.ToJson())!;
        node["payload"]!["goal"] = "rm -rf ~";
        var tampered = new Delta(a.Id, Encoding.UTF8.GetBytes(node.ToJsonString()));

        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        var client = LedgerHub.Http(hub.Url);
        var bad = new List<IDelta> { tampered, new Delta(a.Id, Encoding.UTF8.GetBytes("rm -rf ~")), LedgerDelta.From(foreign) };
        Assert.True((await client.PushAsync(bad, CancellationToken.None)).Success);
        Assert.True((await client.PushAsync([LedgerDelta.From(claim)], CancellationToken.None)).Success);
        var again = LedgerDelta.From(claim);
        again.DeltaId = "a different id the sender made up";
        again.Identity = "someone-else";
        Assert.True((await client.PushAsync([again], CancellationToken.None)).Success);

        Assert.Equal(new HubCounts(1, 1, 3), hub.Store.Counts);
        var stored = (await hub.Store.GetDeltasAsync("-1")).ToList();
        Assert.Equal(claim, LedgerDelta.Entry(Assert.Single(stored)));
    }

    /// <summary>C15: a client whose cursor predates a hub restart still receives what is pushed after it.</summary>
    [Fact]
    public async Task C15_TheHubsNumberingSurvivesARestart()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        var database = Path.Combine(dir.Path, "hub.db");
        Work(ledgerA, a, "before the restart", 0);

        LedgerSyncState stateA, stateB;
        await using (var first = await RunningHub.StartAsync(database, owner.PublicKey))
        {
            var nodeA = Node(first, ledgerA, a);
            var nodeB = Node(first, ledgerB, b);
            await nodeA.SyncAsync(CancellationToken.None);
            await nodeB.SyncAsync(CancellationToken.None);
            (stateA, stateB) = (nodeA.State, nodeB.State);
        }
        Assert.Equal(4, ledgerB.Count);

        await using var second = await RunningHub.StartAsync(database, owner.PublicKey);
        Work(ledgerA, a, "after the restart", 10);
        Assert.True((await Node(second, ledgerA, a, stateA).SyncAsync(CancellationToken.None)).Pushed);
        var report = await Node(second, ledgerB, b, stateB).SyncAsync(CancellationToken.None);
        Assert.Equal(3, report.Pulled.Added);
        Assert.Equal(ledgerA.Digest(), ledgerB.Digest());
    }

    /// <summary>C16: a second sync from the same home pushes and pulls only what is new.</summary>
    [Fact]
    public async Task C16_ASecondSyncSendsAndReceivesOnlyWhatIsNew()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        Work(ledgerA, a, "alpha's", 0);
        Work(ledgerB, b, "beta's", 1);
        var statePath = Path.Combine(dir.Path, "sync-a.json");

        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        await Node(hub, ledgerB, b).SyncAsync(CancellationToken.None);

        // Two runs of the command: each a new ledger read from what the last one saved, and the cursors from the file.
        var saved = new List<SignedEntry>();
        var first = await LedgerHub.SyncAsync(ledgerA, a.Id, hub.Url, statePath, l => saved = [.. l.Entries], CancellationToken.None);
        Assert.Equal((4, 4), (first.Sent, first.Pulled.Added));
        Assert.True(OwnerOnly.IsOwnerOnly(statePath));

        var rerun = new WorkLedger(owner.PublicKey);
        rerun.MergeVerified(saved);
        var second = await LedgerHub.SyncAsync(rerun, a.Id, hub.Url, statePath, _ => { }, CancellationToken.None);
        Assert.Equal((0, 0, 0), (second.Sent, second.Pulled.Added, second.Pulled.Duplicates));

        rerun.Append(a, new HeartbeatPayload(0.9), T0.AddMinutes(30));
        var third = await LedgerHub.SyncAsync(rerun, a.Id, hub.Url, statePath, _ => { }, CancellationToken.None);
        Assert.Equal((1, 0, 0), (third.Sent, third.Pulled.Added, third.Pulled.Duplicates));
        Assert.Equal(new HubCounts(9, 0, 0), hub.Store.Counts);
    }

    /// <summary>
    /// C17: a vessel joins by request, endorse and accept, and its entries reach the owner's ledger through the hub; an
    /// endorsement for another key or vessel is refused, and entries endorsed by another owner reach neither the hub
    /// nor the owner's ledger.
    /// </summary>
    [Fact]
    public async Task C17_AVesselJoinsWithoutTheOwnerKeyLeavingTheOwnersVessel()
    {
        using var air = new TempHome();
        using var surface = new TempHome();
        using var other = new TempHome();
        using var stranger = new TempHome();
        using var hand = new TempHome();
        var airVessel = new FakeVessel("raw-air", "air");
        var surfaceVessel = new FakeVessel("raw-surface", "surface");

        string Run(TempHome home, IVesselSource vessel, params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", home.Path, "--no-mesh", "--creatura", "claude-code"], output, output, vessel);
            Assert.True(code == 0, $"{string.Join(' ', args)} exited {code}: {output}");
            return output.ToString();
        }

        int Fails(TempHome home, IVesselSource vessel, params string[] args)
        {
            var output = new StringWriter();
            return CellCommand.Run([.. args, "--home", home.Path, "--no-mesh", "--creatura", "claude-code"], output, output, vessel);
        }

        Run(air, airVessel, "cell", "init");
        Run(air, airVessel, "cell", "claim", "--goal", "hold the hub");
        var request = Path.Combine(hand.Path, "surface.request.json");
        var endorsement = Path.Combine(hand.Path, "surface.endorsement.json");
        Run(surface, surfaceVessel, "cell", "request", "--out", request);
        Assert.False(File.Exists(new CellHome(surface.Path).OwnerKeyPath), "the new vessel made an owner key");
        Assert.DoesNotContain("PRIVATE", File.ReadAllText(request), StringComparison.Ordinal);
        Run(air, airVessel, "cell", "endorse", "--request", request, "--out", endorsement);

        // An endorsement for another key (another home's cell) or for this home on another vessel is refused.
        Assert.Equal(2, Fails(other, new FakeVessel("raw-other"), "cell", "accept", "--endorsement", endorsement));
        Assert.Equal(2, Fails(surface, new FakeVessel("raw-surface-moved"), "cell", "accept", "--endorsement", endorsement));
        Assert.Contains("hello", Run(surface, surfaceVessel, "cell", "accept", "--endorsement", endorsement));
        Run(surface, surfaceVessel, "cell", "claim", "--goal", "from the surface");

        // Another owner's cell, pushing to the same hub.
        Run(stranger, new FakeVessel("raw-stranger"), "cell", "init");
        Run(stranger, new FakeVessel("raw-stranger"), "cell", "claim", "--goal", "from a stranger");

        using var owner = new CellHome(air.Path).LoadOwner()!;
        await using var hub = await RunningHub.StartAsync(Path.Combine(air.Path, "hub.db"), owner.PublicKey);
        var url = hub.Url.ToString();
        Run(surface, surfaceVessel, "cell", "sync", "--hub", url);
        Run(stranger, new FakeVessel("raw-stranger"), "cell", "sync", "--hub", url);
        var synced = Run(air, airVessel, "cell", "sync", "--hub", url);
        Assert.Contains("received     2 new", synced);

        var ledger = Run(air, airVessel, "cell", "ledger");
        Assert.Contains("from the surface", ledger);
        Assert.DoesNotContain("from a stranger", ledger);
        Assert.Equal(2, hub.Store.Counts.Rejected);

        var back = Run(surface, surfaceVessel, "cell", "sync", "--hub", url);
        Assert.Contains("received     2 new", back);
        Assert.Contains("hold the hub", Run(surface, surfaceVessel, "cell", "ledger"));
    }

    /// <summary>C18: the hub answers loopback and the mesh range only.</summary>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("100.100.1.3", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("::ffff:100.100.1.5", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("192.0.2.74", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fd7a:115c:a1e0::1", false)]
    public async Task C18_TheHubAnswersLoopbackAndTheMeshOnly(string address, bool allowed)
    {
        Assert.Equal(allowed, HubAccess.IsAllowed(IPAddress.Parse(address)));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        var reached = false;
        await HubAccess.Middleware(context, _ => { reached = true; return Task.CompletedTask; });
        Assert.Equal(allowed, reached);
        if (!allowed) Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
}
