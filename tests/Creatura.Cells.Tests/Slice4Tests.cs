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
        new(ledger, cell.Id, LedgerHub.Http(hub.Url, cell), state);

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
        var client = LedgerHub.Http(hub.Url, a);
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
        var first = await LedgerHub.SyncAsync(ledgerA, a, hub.Url, statePath, l => saved = [.. l.Entries], CancellationToken.None);
        Assert.Equal((4, 4), (first.Sent, first.Pulled.Added));
        Assert.True(OwnerOnly.IsOwnerOnly(statePath));

        var rerun = new WorkLedger(owner.PublicKey);
        rerun.MergeVerified(saved);
        var second = await LedgerHub.SyncAsync(rerun, a, hub.Url, statePath, _ => { }, CancellationToken.None);
        Assert.Equal((0, 0, 0), (second.Sent, second.Pulled.Added, second.Pulled.Duplicates));

        rerun.Append(a, new HeartbeatPayload(0.9), T0.AddMinutes(30));
        var third = await LedgerHub.SyncAsync(rerun, a, hub.Url, statePath, _ => { }, CancellationToken.None);
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

        // Another owner's cell is turned away at the door (C20): its request is not signed by a cell the hub's owner
        // endorsed, so nothing it sends reaches the store and nothing is served to it.
        Assert.NotEqual(0, Fails(stranger, new FakeVessel("raw-stranger"), "cell", "sync", "--hub", url));
        var synced = Run(air, airVessel, "cell", "sync", "--hub", url);
        Assert.Contains("received     2 new", synced);

        var ledger = Run(air, airVessel, "cell", "ledger");
        Assert.Contains("from the surface", ledger);
        Assert.DoesNotContain("from a stranger", ledger);
        Assert.Equal(new HubCounts(Stored: 4, Duplicates: 0, Rejected: 0), hub.Store.Counts);   // two hellos and two claims

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

    /// <summary>
    /// C19: a hub given only a file of owner public keys trusts exactly those owners and needs no private key; a file
    /// with anything else in it is refused.
    /// </summary>
    [Fact]
    public async Task C19_AHubTrustsAPublicKeyFileAndNeedsNoPrivateKey()
    {
        using var ownerDir = new TempHome();
        using var strangerDir = new TempHome();
        using var server = new TempHome();
        var (home, _) = NewHome(ownerDir);
        var (strangerHome, _) = NewHome(strangerDir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var stranger = NewCell(strangerHome, "raw-vessel-s", "mallory");

        // The owner's vessel writes the file; the server has nothing but that file.
        var trust = Path.Combine(server.Path, "owners.txt");
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "owner-key", "--home", ownerDir.Path, "--out", trust], output, output, new FakeVessel("raw-vessel-a")));
        Assert.DoesNotContain("PRIVATE", File.ReadAllText(trust), StringComparison.Ordinal);
        var emptyHome = Path.Combine(server.Path, "no-home");
        var launch = HubCommandLine.Parse(["--listen", "127.0.0.1:0", "--trust", trust, "--home", emptyHome]);
        Assert.Equal([a.Identity.Endorsement!.OwnerPublicKey], launch.Options.TrustedOwners);
        Assert.Equal(Path.Combine(emptyHome, "hub", "hub.db"), launch.Options.DatabasePath);
        Assert.False(Directory.Exists(Path.Combine(emptyHome, "owner")), "the hub made or read an owner key");

        var data = HubCommandLine.Parse(["--listen", "127.0.0.1:0", "--trust", trust, "--data", Path.Combine(server.Path, "hub")]);
        Assert.Equal(Path.Combine(server.Path, "hub", "hub.db"), data.Options.DatabasePath);
        Directory.CreateDirectory(Path.Combine(server.Path, "hub"));
        await using (var hub = await RunningHub.StartAsync(data.Options.DatabasePath, [.. data.Options.TrustedOwners]))
        {
            var client = LedgerHub.Http(hub.Url, a);
            var mine = new WorkLedger(a.Identity.Endorsement!.OwnerPublicKey).Append(a, new HelloPayload(a.Identity), T0);
            var theirs = new WorkLedger(stranger.Identity.Endorsement!.OwnerPublicKey).Append(stranger, new HelloPayload(stranger.Identity), T0);
            Assert.True((await client.PushAsync([LedgerDelta.From(mine), LedgerDelta.From(theirs)], CancellationToken.None)).Success);
            Assert.Equal(new HubCounts(1, 0, 1), hub.Store.Counts);
        }

        // Refused: no --data and no --home with --trust, a private key, something that is not a key, an empty file.
        Assert.Throws<ArgumentException>(() => HubCommandLine.Parse(["--listen", "127.0.0.1:0", "--trust", trust]));
        var privateKey = Path.Combine(server.Path, "private.txt");
        File.WriteAllText(privateKey, File.ReadAllText(home.OwnerKeyPath));
        var refused = Assert.Throws<ArgumentException>(() => HubCommandLine.ReadTrustFile(privateKey));
        Assert.DoesNotContain(File.ReadAllLines(home.OwnerKeyPath)[1], refused.Message, StringComparison.Ordinal);
        var junk = Path.Combine(server.Path, "junk.txt");
        File.WriteAllText(junk, a.Identity.Endorsement!.OwnerPublicKey + "\nhello\n");
        Assert.Throws<ArgumentException>(() => HubCommandLine.ReadTrustFile(junk));
        var empty = Path.Combine(server.Path, "empty.txt");
        File.WriteAllText(empty, "# nobody\n\n");
        Assert.Throws<ArgumentException>(() => HubCommandLine.ReadTrustFile(empty));
    }

    /// <summary>Changes a request after it was signed: the body, the query, or the endorsement header.</summary>
    private sealed class Tamper(Func<HttpRequestMessage, Task> change) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await change(request);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// C20: the hub answers only requests signed by an endorsed cell of an owner it trusts, within five minutes, over
    /// that exact request.
    /// </summary>
    [Fact]
    public async Task C20_TheHubAnswersOnlyRequestsSignedByAnEndorsedCell()
    {
        using var dir = new TempHome();
        using var strangerDir = new TempHome();
        var (home, owner) = NewHome(dir);
        var (strangerHome, _) = NewHome(strangerDir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        using var stranger = NewCell(strangerHome, "raw-vessel-s", "mallory");
        var ledger = new WorkLedger(owner.PublicKey);
        Work(ledger, a, "something private: a client's repository", 0);

        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        var fetch = new Uri(hub.Url, "/sync/Fetch?startIndex=-1&identity=someone");

        // The control: a's cell pushes and b's cell reads it back.
        Assert.True((await Node(hub, ledger, a).SyncAsync(CancellationToken.None)).Pushed);
        Assert.Equal(4, (await LedgerHub.Http(hub.Url, b).FetchAsync("-1", b.Id, CancellationToken.None)).Deltas.Count);

        // Unsigned: a plain request, from loopback, which the mesh check lets through.
        using (var plain = new HttpClient())
        {
            var answer = await plain.GetAsync(fetch);
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
            Assert.DoesNotContain("private", await answer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, (await plain.PostAsync(new Uri(hub.Url, "/sync/Push"), new StringContent("[]"))).StatusCode);
        }

        async Task<HttpStatusCode> Status(LocalCell signer, TimeProvider? clock = null, Func<HttpRequestMessage, Task>? change = null, HttpMethod? method = null)
        {
            using var client = new HttpClient(new HubSigningHandler(signer, clock) { InnerHandler = new Tamper(change ?? (_ => Task.CompletedTask)) });
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, fetch);
            if (request.Method == HttpMethod.Post) request.Content = new StringContent("[]", Encoding.UTF8, "application/json");
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Status(b));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(stranger));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(b, new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-10))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(b, new FixedClock(DateTimeOffset.UtcNow.AddMinutes(10))));
        Assert.Equal(HttpStatusCode.OK, await Status(b, new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-4))));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(b, change: r =>
        {
            r.RequestUri = new Uri(r.RequestUri!.ToString().Replace("identity=someone", "identity=someone-else", StringComparison.Ordinal));
            return Task.CompletedTask;
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(b, method: HttpMethod.Post, change: r =>
        {
            r.Content = new StringContent("[ ]", Encoding.UTF8, "application/json");
            return Task.CompletedTask;
        }));

        // Signed by b's key under a's endorsement: the endorsement does not cover the key that signed.
        var aEndorsement = HubRequest.Sign(a, "GET", "/", DateTimeOffset.UtcNow, [])[0].Value;
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(b, change: r =>
        {
            r.Headers.Remove(HubRequest.EndorsementHeader);
            r.Headers.TryAddWithoutValidation(HubRequest.EndorsementHeader, aEndorsement);
            return Task.CompletedTask;
        }));

        Assert.Equal(new HubCounts(4, 0, 0), hub.Store.Counts);
    }

    /// <summary>Remembers the last request it passed on, headers and body, so a test can send it again as it was.</summary>
    private sealed class Recorder() : DelegatingHandler(new HttpClientHandler())
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public byte[] Body { get; private set; } = [];
        public List<KeyValuePair<string, string>> Headers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Headers.Clear();
            foreach (var name in new[] { HubRequest.EndorsementHeader, HubRequest.TimeHeader, HubRequest.SignatureHeader })
                Headers.Add(new(name, request.Headers.GetValues(name).Single()));
            return await base.SendAsync(request, cancellationToken);
        }

        public async Task<HttpStatusCode> ReplayAsync(Uri? to = null)
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(Method!, to is null ? Uri : new Uri(to, Uri!.PathAndQuery));
            if (Body.Length > 0) request.Content = new ByteArrayContent(Body);
            foreach (var (name, value) in Headers) request.Headers.TryAddWithoutValidation(name, value);
            return (await client.SendAsync(request)).StatusCode;
        }
    }

    /// <summary>C21: a signed request sent twice is answered once, also across a hub restart.</summary>
    [Fact]
    public async Task C21_ASignedRequestIsAnsweredOnce()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        var database = Path.Combine(dir.Path, "hub.db");
        var recorder = new Recorder();

        await using (var first = await RunningHub.StartAsync(database, owner.PublicKey))
        {
            var client = LedgerHub.Http(first.Url, a, inner: recorder);
            Assert.True((await client.FetchAsync("-1", a.Id, CancellationToken.None)).Success);
            Assert.Equal(HttpStatusCode.Unauthorized, await recorder.ReplayAsync());

            // A push replayed: the same body and headers, refused the second time.
            Assert.True((await client.PushAsync([LedgerDelta.From(new WorkLedger(owner.PublicKey).Append(a, new HelloPayload(a.Identity), T0))], CancellationToken.None)).Success);
            Assert.Equal(HttpStatusCode.Unauthorized, await recorder.ReplayAsync());
        }

        await using var second = await RunningHub.StartAsync(database, owner.PublicKey);
        Assert.Equal(HttpStatusCode.Unauthorized, await recorder.ReplayAsync(second.Url));
        Assert.True((await LedgerHub.Http(second.Url, a).FetchAsync("-1", a.Id, CancellationToken.None)).Success);
        Assert.Equal(new HubCounts(0, 0, 0), second.Store.Counts);
    }

    /// <summary>
    /// C22: after a revocation the hub refuses the cell's requests and its entries past N, ledgers drop and refuse them
    /// whatever the order of arrival, the revocation reaches other homes through sync, and one signed by an owner a
    /// home does not trust changes nothing.
    /// </summary>
    [Fact]
    public async Task C22_ARevokedCellIsRefusedEverywhere()
    {
        using var dir = new TempHome();
        using var strangerDir = new TempHome();
        var (home, owner) = NewHome(dir);
        var (_, strangerOwner) = NewHome(strangerDir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");
        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        Work(ledgerA, a, "the survivor's", 0);
        Work(ledgerB, b, "before the vessel was lost", 1);
        Work(ledgerB, b, "after: the thief's", 5);
        Assert.Equal(7, ledgerB.Count);

        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        await Node(hub, ledgerB, b).SyncAsync(CancellationToken.None);
        await Node(hub, ledgerA, a).SyncAsync(CancellationToken.None);
        Assert.Equal(11, ledgerA.Count);
        var everything = ledgerA.Entries.ToList();

        // The owner revokes b after its fourth entry and hands the revocation to the hub as a.
        var revocation = owner.Revoke(b.Identity.PublicKey, 4, "the vessel was lost", T0.AddHours(1));
        var held = await LedgerHub.ExchangeRevocationsAsync(hub.Url, a, [revocation], CancellationToken.None);
        Assert.Equal(revocation, Assert.Single(held));
        Assert.DoesNotContain((await hub.Store.GetDeltasAsync("-1")).Select(LedgerDelta.Entry), e => e.Cell == b.Id && e.Sequence > 4);

        // The hub answers b no more, and refuses b's entries past 4 from anyone.
        await Assert.ThrowsAsync<HttpRequestException>(() => LedgerHub.ExchangeRevocationsAsync(hub.Url, b, [], CancellationToken.None));
        Assert.False((await LedgerHub.Http(hub.Url, b).PushAsync([LedgerDelta.From(everything.Last(e => e.Cell == b.Id))], CancellationToken.None)).Success);
        var rejectedBefore = hub.Store.Counts.Rejected;
        Assert.True((await LedgerHub.Http(hub.Url, a).PushAsync([LedgerDelta.From(everything.Last(e => e.Cell == b.Id))], CancellationToken.None)).Success);
        Assert.Equal(rejectedBefore + 1, hub.Store.Counts.Rejected);

        // a's next sync takes the revocation and drops what it held past 4.
        var report = await LedgerHub.SyncAsync(ledgerA, a, hub.Url, Path.Combine(dir.Path, "a.json"), _ => { }, CancellationToken.None);
        Assert.Equal(1, report.RevocationsTaken);
        Assert.Equal(8, ledgerA.Count);
        Assert.DoesNotContain(ledgerA.Entries, e => e.Cell == b.Id && e.Sequence > 4);

        // Whatever the order: revocation first or entries first, the same ledger.
        var revokedFirst = new WorkLedger(owner.PublicKey);
        Assert.True(revokedFirst.Revoke(revocation));
        var refused = revokedFirst.Merge(everything);
        Assert.Equal(3, refused.Rejected.Count);
        var entriesFirst = new WorkLedger(owner.PublicKey);
        entriesFirst.Merge(everything);
        Assert.True(entriesFirst.Revoke(revocation));
        Assert.False(entriesFirst.Revoke(revocation));
        Assert.Equal(revokedFirst.Digest(), entriesFirst.Digest());
        Assert.Equal(ledgerA.Digest(), entriesFirst.Digest());

        // Of two revocations of one cell, every ledger keeps the stronger one.
        var later = owner.Revoke(b.Identity.PublicKey, 2, "it went earlier than we thought", T0.AddHours(2));
        Assert.True(entriesFirst.Revoke(later));
        Assert.False(entriesFirst.Revoke(revocation));
        Assert.Equal(later, Assert.Single(Revocation.Merge([revocation, later])));

        // Another owner's revocation of a's cell changes nothing, at a ledger or at the hub.
        var forged = strangerOwner.Revoke(a.Identity.PublicKey, 0, "not yours to revoke", T0.AddHours(1));
        Assert.False(ledgerA.Revoke(forged));
        Assert.Single(await LedgerHub.ExchangeRevocationsAsync(hub.Url, a, [forged], CancellationToken.None));
        Assert.False(hub.Store.IsRevoked(a.Id));
        Assert.True((await LedgerHub.Http(hub.Url, a).FetchAsync("-1", a.Id, CancellationToken.None)).Success);
    }

    /// <summary>The revoke command: entries past the point dropped from this home's ledger and the revocation kept owner-only.</summary>
    [Fact]
    public void TheRevokeCommandDropsTheCellsLaterEntriesHere()
    {
        using var dir = new TempHome();
        var vessel = new FakeVessel("raw-owner");
        string Run(params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", dir.Path, "--no-mesh"], output, output, vessel);
            Assert.True(code == 0, $"{string.Join(' ', args)} exited {code}: {output}");
            return output.ToString();
        }

        Run("cell", "init");
        Run("cell", "init", "--creatura", "beta");
        Run("cell", "claim", "--goal", "beta's claim", "--creatura", "beta");
        var beta = Run("cell", "whoami", "--creatura", "beta").Split('\n').Single(l => l.StartsWith("cell ", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];

        var revoked = Run("cell", "revoke", "--cell", beta, "--up-to", "1", "--reason", "test");
        Assert.Contains("1 entry past it dropped", revoked);
        Assert.True(OwnerOnly.IsOwnerOnly(new CellHome(dir.Path).RevocationsPath));
        var ledger = Run("cell", "ledger");
        Assert.DoesNotContain("beta's claim", ledger);
        Assert.Contains("2 entries from 2 cells", ledger);

        // A cell that never wrote here is revoked by the endorsement the owner gave it.
        using var lost = new TempHome();
        var lostVessel = new FakeVessel("raw-lost");
        var request = Path.Combine(lost.Path, "lost.request.json");
        var endorsement = Path.Combine(lost.Path, "lost.endorsement.json");
        Assert.Equal(0, CellCommand.Run(["cell", "request", "--home", lost.Path, "--no-mesh", "--out", request], new StringWriter(), new StringWriter(), lostVessel));
        Run("cell", "endorse", "--request", request, "--out", endorsement);
        Assert.Contains("after sequence 0", Run("cell", "revoke", "--endorsement", endorsement, "--reason", "lost before it ever synced"));
        Assert.Equal(2, new CellHome(dir.Path).LoadRevocations().Count);
    }

    /// <summary>The two launchd agents: generated plists that parse, absolute paths, the right schedule, and install and uninstall that touch only these two labels.</summary>
    [Fact]
    public void TheLaunchdAgentsAreGeneratedInstalledAndRemoved()
    {
        using var dir = new TempHome();
        using var agents = new TempHome();
        var vessel = new FakeVessel("raw-owner");
        (int Code, string Text) Run(params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", dir.Path, "--no-mesh", "--creatura", "claude-code", "--agents", agents.Path, "--no-load"], output, output, vessel);
            return (code, output.ToString());
        }

        var options = new[] { "--hub", "http://100.100.1.5:5181", "--listen", "100.100.1.5:5181" };
        var config = Run(["cell", "launchd-config", .. options]);
        Assert.Equal(0, config.Code);
        foreach (var label in new[] { LaunchdSetup.SyncLabel, LaunchdSetup.HubLabel }) Assert.Contains(label + ".plist", config.Text);

        var sync = System.Xml.Linq.XDocument.Parse(LaunchdSetup.SyncPlist("/h/bin/creatura", "/h", "claude-code", "http://100.100.1.5:5181", 60, "/h/sync/sync.log"));
        var syncArguments = sync.Descendants("array").Single().Elements("string").Select(e => e.Value).ToList();
        Assert.Equal(["/h/bin/creatura", "cell", "sync", "--home", "/h", "--creatura", "claude-code", "--hub", "http://100.100.1.5:5181", "--quiet"], syncArguments);
        Assert.Contains("<key>StartInterval</key>", sync.ToString());
        var hub = System.Xml.Linq.XDocument.Parse(LaunchdSetup.HubPlist("/usr/local/share/dotnet/dotnet", "/h/hub/app/Creatura.Cells.Hub.dll", "100.100.1.5:5181", "/h/hub/owners.txt", "/h/hub", "/h/hub/hub.log"));
        Assert.Equal(["/usr/local/share/dotnet/dotnet", "/h/hub/app/Creatura.Cells.Hub.dll", "--listen", "100.100.1.5:5181", "--trust", "/h/hub/owners.txt", "--data", "/h/hub"],
            hub.Descendants("array").Single().Elements("string").Select(e => e.Value).ToList());
        Assert.Contains("<key>KeepAlive</key>", hub.ToString());
        Assert.All(config.Text.Split('\n').Where(l => l.Contains("<string>/", StringComparison.Ordinal)), l => Assert.DoesNotContain("~", l, StringComparison.Ordinal));

        // Nothing is installed while a piece is missing, and the message names each one.
        var missing = Run(["cell", "install-launchd", .. options]);
        Assert.Equal(2, missing.Code);
        Assert.Contains("publish-bin", missing.Text);
        Assert.Contains("publish-hub", missing.Text);
        Assert.Contains("owner-key", missing.Text);
        Assert.Empty(Directory.GetFiles(agents.Path));

        foreach (var piece in new[] { Path.Combine(dir.Path, "bin", "creatura"), Path.Combine(dir.Path, "hub", "app", "Creatura.Cells.Hub.dll"), Path.Combine(dir.Path, "hub", "owners.txt") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(piece)!);
            File.WriteAllText(piece, "");
        }
        var other = Path.Combine(agents.Path, "com.someone.else.plist");
        File.WriteAllText(other, "not ours");

        Assert.Equal(0, Run(["cell", "install-launchd", "--dry-run", .. options]).Code);
        Assert.Single(Directory.GetFiles(agents.Path));
        var installed = Run(["cell", "install-launchd", .. options]);
        Assert.Equal(0, installed.Code);
        Assert.Equal(3, Directory.GetFiles(agents.Path).Length);
        Assert.Contains("unchanged", Run(["cell", "install-launchd", .. options]).Text);
        var changed = Run(["cell", "install-launchd", "--hub", "http://100.100.1.5:5181", "--listen", "100.100.1.5:5181", "--interval", "120"]);
        Assert.Contains("backed up", changed.Text);

        Assert.Equal(0, Run("cell", "uninstall-launchd").Code);
        Assert.True(File.Exists(other));
        Assert.Empty(Directory.GetFiles(agents.Path).Where(f => f.EndsWith(".plist", StringComparison.Ordinal) && Path.GetFileName(f).StartsWith("com.creatura.", StringComparison.Ordinal)));
    }

    /// <summary>sync --quiet: a run with nothing to move prints nothing, so a sync every minute does not fill its log.</summary>
    [Fact]
    public async Task AQuietSyncPrintsOnlyWhenSomethingMoved()
    {
        using var dir = new TempHome();
        var vessel = new FakeVessel("raw-owner");
        string Run(params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", dir.Path, "--no-mesh"], output, output, vessel);
            Assert.True(code == 0, $"{string.Join(' ', args)} exited {code}: {output}");
            return output.ToString();
        }

        Run("cell", "init");
        using var owner = new CellHome(dir.Path).LoadOwner()!;
        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        Assert.Contains("sent         1 entry", Run("cell", "sync", "--hub", hub.Url.ToString(), "--quiet"));
        Assert.Equal("", Run("cell", "sync", "--hub", hub.Url.ToString(), "--quiet"));
        Run("cell", "heartbeat");
        Assert.Contains("sent         1 entry", Run("cell", "sync", "--hub", hub.Url.ToString(), "--quiet"));
    }
}
