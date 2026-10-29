using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Creatura.Cells.Tests;

/// <summary>
/// The pre-registered tests of docs/design-activity-stream.md for the read-only stream (C23 to C26), written before the
/// code. Each test's name is its id; its summary is the claim, and the assertions are the falsifier as the note states it.
/// </summary>
public class ActivityStreamTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static (CellHome Home, OwnerKey Owner) NewHome(TempHome dir)
    {
        var home = new CellHome(dir.Path);
        var owner = home.EnsureOwner(out _);
        return (home, owner);
    }

    private static LocalCell NewCell(CellHome home, string raw, string creatura, string host) =>
        home.Endorse(home.OpenCell(new FakeVessel(raw, host), Position.InProcess(creatura), creatura));

    /// <summary>
    /// Two cells on two vessels with one entry of every kind: hellos, a claim each, progress (one blocked, with stress),
    /// a heartbeat with a load, a warning, a shared file, a hand-off and a release. Returns the ledger and its entries.
    /// </summary>
    private static (WorkLedger Ledger, List<SignedEntry> Entries, SealedFile File) Fleet(OwnerKey owner, LocalCell a, LocalCell b)
    {
        var ledger = new WorkLedger(owner.PublicKey);
        var t = T0;
        SignedEntry Add<T>(LocalCell cell, T payload) where T : IEntryPayload => ledger.Append(cell, payload, t = t.AddMinutes(1));
        Add(a, new HelloPayload(a.Identity));
        Add(b, new HelloPayload(b.Identity));
        var claimA = Add(a, new ClaimPayload("migration step 16") { Repo = "egarim/creatura", Branch = "main", Tool = "claude-code", Session = "s-1" });
        var claimB = Add(b, new ClaimPayload("rotate the logs") { Folder = "/srv/logs", Tool = "codex" });
        Add(a, new ProgressPayload(claimA.Id, "golden diff empty") { Stress = 0.2 });
        Add(b, new ProgressPayload(claimB.Id, "disk full") { Stress = 0.9, Blocked = true });
        Add(b, new HeartbeatPayload(0.42));
        Add(a, new WarningPayload("road:r12", "sensor says open, nothing arrived"));
        var file = SharedFiles.Seal(Encoding.UTF8.GetBytes("secret plan"), "secret-plan.txt", "text/plain", SharedFiles.Recipients(ledger, a, [b.Id]));
        Add(a, file.Payload with { Claim = claimA.Id });
        Add(b, new HandOffPayload(claimB.Id, "waiting for a bigger disk"));
        Add(a, new ReleasePayload(claimA.Id, "737 tests green; commit 7b7421f"));
        return (ledger, ledger.Entries.ToList(), file);
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Every string in a JSON document, decoded: a search of the raw text misses what the writer escaped (base64's '+' is written \u002B).</summary>
    private static string AllText(string json)
    {
        var strings = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o: foreach (var (key, value) in o) { strings.Add(key); Walk(value); } break;
                case JsonArray a: foreach (var value in a) Walk(value); break;
                case JsonValue v: strings.Add(v.ToString()); break;
            }
        }

        Walk(JsonNode.Parse(json));
        return string.Join("\n", strings);
    }

    private static List<JsonObject> Items(JsonObject collection) => collection["orderedItems"]!.AsArray().Select(n => n!.AsObject()).ToList();

    /// <summary>
    /// C23: the stream is a pure function of the verified ledger and the trusted revocations: the same entries, in any
    /// order of arrival, give the same bytes, also on two homes after a sync. Falsifier: any byte that differs.
    /// </summary>
    [Fact]
    public void C23_TheSameEntriesGiveTheSameStreamWhateverTheOrderOfArrival()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha", "laptop");
        using var b = NewCell(home, "raw-vessel-b", "beta", "second");
        var (ledger, entries, _) = Fleet(owner, a, b);
        var expected = ActivityStream.Document(ledger);

        foreach (var seed in new[] { 1, 2, 3, 4, 5 })
        {
            var random = new Random(seed);
            var shuffled = entries.OrderBy(_ => random.Next()).ToList();
            var other = new WorkLedger(owner.PublicKey);
            // Arrival in two batches, as two syncs would bring them.
            other.Merge(shuffled.Take(shuffled.Count / 2));
            other.Merge(shuffled.Skip(shuffled.Count / 2));
            Assert.Equal(expected, ActivityStream.Document(other));
        }

        // A second home that synced the same entries from this one.
        var synced = new WorkLedger(owner.PublicKey);
        synced.Merge(Enumerable.Reverse(entries));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(ActivityStream.Document(synced)));
        // And building twice changes nothing.
        Assert.Equal(expected, ActivityStream.Document(ledger));
    }

    /// <summary>
    /// C24: each entry is exactly one activity, except heartbeats, which only update their actor; each activity names
    /// its entry, its actor and its time, and a claim's progress, hand-off and release are in its thread; the collection
    /// has the shape Activity Streams 2.0 requires. Falsifier: an entry with no activity or two, a heartbeat in the
    /// timeline, an activity whose id is not its entry's, a step outside its claim's thread, or a shape a reader rejects.
    /// </summary>
    [Fact]
    public void C24_EachEntryIsOneActivityInItsClaimsThread()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha", "laptop");
        using var b = NewCell(home, "raw-vessel-b", "beta", "second");
        var (ledger, entries, _) = Fleet(owner, a, b);

        var document = Parse(ActivityStream.Document(ledger));
        Assert.Contains("https://www.w3.org/ns/activitystreams", document["@context"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("OrderedCollection", (string?)document["type"]);
        var items = Items(document);
        var timeline = entries.Where(e => e.Kind != EntryKind.Heartbeat).ToList();
        Assert.Equal(timeline.Count, (int)document["totalItems"]!);
        Assert.Equal(timeline.Count, items.Count);

        // Newest first, one activity per entry, each naming its entry, its actor and its time.
        Assert.Equal(timeline.Select(e => $"urn:creatura:entry:{e.Id}").Reverse(), items.Select(i => (string?)i["id"]));
        foreach (var (item, entry) in items.Zip(Enumerable.Reverse(timeline)))
        {
            Assert.Equal($"urn:creatura:cell:{entry.Cell}", (string?)item["actor"]);
            Assert.Equal(entry.Timestamp, DateTimeOffset.Parse((string)item["published"]!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(string.IsNullOrEmpty((string?)item["summary"]));
            Assert.Equal(entry.Signature, (string?)item["creatura:entry"]!["signature"]);
        }

        string TypeOf(EntryKind kind) => (string)items.Single(i => (string?)i["creatura:entry"]!["kind"] == kind.ToString())["type"]!;
        Assert.All(items.Where(i => (string?)i["creatura:entry"]!["kind"] == "Hello"), i => Assert.Equal("Join", (string?)i["type"]));
        Assert.Equal("Flag", TypeOf(EntryKind.Warning));
        Assert.Equal("Leave", TypeOf(EntryKind.HandOff));
        Assert.Equal("creatura:Release", TypeOf(EntryKind.Release));
        Assert.Equal("Create", TypeOf(EntryKind.Artifact));
        Assert.All(items.Where(i => (string?)i["creatura:entry"]!["kind"] == "Claim"), i => Assert.Equal("Create", (string?)i["type"]));
        Assert.All(items.Where(i => (string?)i["creatura:entry"]!["kind"] == "Progress"), i => Assert.Equal("Update", (string?)i["type"]));

        // Threads: every step of a claim carries the claim's thread as its context.
        foreach (var claim in entries.Where(e => e.Kind == EntryKind.Claim))
        {
            var thread = $"urn:creatura:claim:{claim.Id}";
            var steps = ledger.PathOf(claim.Id).Where(e => e.Kind is EntryKind.Progress or EntryKind.HandOff or EntryKind.Release or EntryKind.Artifact).ToList();
            Assert.NotEmpty(steps);
            foreach (var step in steps) Assert.Equal(thread, (string?)items.Single(i => (string?)i["id"] == $"urn:creatura:entry:{step.Id}")["context"]);
            Assert.Equal(thread, (string?)items.Single(i => (string?)i["id"] == $"urn:creatura:entry:{claim.Id}")["object"]!["id"]);
        }

        // The blocked step says so; the heartbeat is no activity, only the actor's last word.
        var blocked = items.Single(i => (string?)i["content"] == "disk full");
        Assert.True((bool)blocked["creatura:blocked"]!);
        var actors = document["creatura:actors"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal(2, actors.Count);
        var beta = actors.Single(x => (string?)x["id"] == $"urn:creatura:cell:{b.Id}");
        Assert.Equal("Service", (string?)beta["type"]);
        Assert.Equal(0.42, (double)beta["creatura:load"]!);
        Assert.Equal(entries.Last(e => e.Cell == b.Id).Timestamp, DateTimeOffset.Parse((string)beta["creatura:lastSeen"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("second", (string)beta["name"]!, StringComparison.Ordinal);
        Assert.StartsWith("#", (string)beta["creatura:ring"]!, StringComparison.Ordinal);

        // Pages: page 1 is the newest, and the pages together are the whole timeline.
        var page = Parse(ActivityStream.Page(ledger, 1, pageSize: 4));
        Assert.Equal("OrderedCollectionPage", (string?)page["type"]);
        Assert.Equal(items.Take(4).Select(i => (string?)i["id"]), Items(page).Select(i => (string?)i["id"]));
        var all = Enumerable.Range(1, 3).SelectMany(n => Items(Parse(ActivityStream.Page(ledger, n, pageSize: 4)))).Select(i => (string?)i["id"]);
        Assert.Equal(items.Select(i => (string?)i["id"]), all);
    }

    /// <summary>
    /// C25: nothing the ledger seals or withholds reaches the stream: no ciphertext, sealed name or wrapped key, no raw
    /// hardware identifier, no entry past a revocation's N. Falsifier: any of them found in the stream or the page.
    /// </summary>
    [Fact]
    public void C25_NothingSealedOrRevokedReachesTheStream()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-secret-a", "alpha", "laptop");
        using var b = NewCell(home, "raw-vessel-secret-b", "beta", "second");
        var (ledger, entries, file) = Fleet(owner, a, b);
        var json = ActivityStream.Document(ledger);
        var document = json + "\n" + AllText(json);

        Assert.DoesNotContain("raw-vessel-secret", document, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-plan", document, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Payload.Label, document, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Payload.Ciphertext, document, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Payload.Plain, document, StringComparison.Ordinal);
        foreach (var key in file.Payload.Keys)
        {
            Assert.DoesNotContain(key.Sealed, document, StringComparison.Ordinal);
            Assert.DoesNotContain(key.Ephemeral, document, StringComparison.Ordinal);
        }

        // Size is shown; the file's name is not.
        var artifact = Items(Parse(json)).Single(i => (string?)i["creatura:entry"]!["kind"] == "Artifact");
        Assert.Equal(file.Payload.Size, (long)artifact["object"]!["creatura:size"]!);

        // Revoked: b trusted up to its second entry; its later entries are in neither the ledger nor the stream.
        var revocation = owner.Revoke(b.Identity.PublicKey, 2, "the vessel was lost", T0.AddHours(1));
        Assert.True(ledger.Revoke(revocation));
        var after = ActivityStream.Document(ledger);
        foreach (var entry in entries.Where(e => e.Cell == b.Id && e.Sequence > 2))
        {
            Assert.DoesNotContain($"urn:creatura:entry:{entry.Id}\"", after, StringComparison.Ordinal);
        }

        Assert.Contains($"urn:creatura:entry:{b.Id}#2\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-vessel-secret", StreamPage.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// C26: <c>creatura cell stream</c> answers on the loopback address only, and only GET. Falsifier: an answer on
    /// another address, or to another method.
    /// </summary>
    [Fact]
    public async Task C26_TheStreamAnswersOnLoopbackOnlyAndOnlyGet()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha", "laptop");
        using var b = NewCell(home, "raw-vessel-b", "beta", "second");
        var (ledger, _, _) = Fleet(owner, a, b);

        await using var server = StreamServer.Start(() => ledger);
        Assert.Equal(IPAddress.Loopback.ToString(), server.Address.Host);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var page = await http.GetAsync(server.Address);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var feed = await http.GetAsync(new Uri(server.Address, "activity"));
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal(ActivityStream.MediaType, feed.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ActivityStream.Collection(ledger), await feed.Content.ReadAsStringAsync());

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            var response = await http.SendAsync(new HttpRequestMessage(method, new Uri(server.Address, "activity")) { Content = new StringContent("{}") });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        // Every other address of this machine: the stream does not answer there. The falsifier is the stream's answer,
        // not a TCP connect: a VPN in tunnel mode accepts every connection to its own address, on any port, whether
        // anything listens or not (seen here on a utun interface), so only the response says who answered.
        var others = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .ToList();
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var ip in others)
        {
            string? answer = null;
            try
            {
                var response = await probe.GetAsync(new Uri($"http://{ip}:{server.Address.Port}/activity"));
                answer = await response.Content.ReadAsStringAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
            }

            Assert.False(answer is not null && answer.Contains("urn:creatura", StringComparison.Ordinal), $"the stream answered on {ip}");
        }
    }
}
