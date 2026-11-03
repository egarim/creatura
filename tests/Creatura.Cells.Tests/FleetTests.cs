using System.Text;
using System.Text.Json.Nodes;
using Creatura.Operator;

namespace Creatura.Cells.Tests;

/// <summary>The console's Fleet (docs/design-console.md, slice U3): U3.1 to U3.3.</summary>
public class FleetTests
{
    private static readonly DateTimeOffset T0 = ActivityStreamTests.T0;

    /// <summary>
    /// Three cells on three vessels: alpha on a claim, its last progress fine; beta on a claim, blocked at stress 0.9;
    /// gamma said hello and nothing else. Returns the ledger, its entries, and the cells.
    /// </summary>
    private static (WorkLedger Ledger, List<SignedEntry> Entries, LocalCell A, LocalCell B, LocalCell C) Working(OwnerKey owner, CellHome home)
    {
        var a = ActivityStreamTests.NewCell(home, "raw-vessel-a", "alpha", "laptop");
        var b = ActivityStreamTests.NewCell(home, "raw-vessel-b", "beta", "second");
        var c = ActivityStreamTests.NewCell(home, "raw-vessel-c", "gamma", "third");
        var ledger = new WorkLedger(owner.PublicKey);
        var t = T0;
        SignedEntry Add<T>(LocalCell cell, T payload) where T : IEntryPayload => ledger.Append(cell, payload, t = t.AddMinutes(1));
        Add(a, new HelloPayload(a.Identity));
        Add(b, new HelloPayload(b.Identity));
        Add(c, new HelloPayload(c.Identity));
        var claimA = Add(a, new ClaimPayload("migration step 16") { Tool = "claude-code" });
        var claimB = Add(b, new ClaimPayload("rotate the logs") { Tool = "codex" });
        Add(b, new ProgressPayload(claimB.Id, "disk full") { Stress = 0.9, Blocked = true });
        Add(a, new ProgressPayload(claimA.Id, "golden diff empty") { Stress = 0.2 });
        Add(b, new HeartbeatPayload(0.42));
        return (ledger, ledger.Entries.ToList(), a, b, c);
    }

    /// <summary>The creatura command in this process, on one home: what the console runs as a process elsewhere.</summary>
    private sealed class InProcess(string home, IVesselSource source) : ICellCommand
    {
        public List<string[]> Calls { get; } = [];

        public (int Code, string Output, string Error) Run(IReadOnlyList<string> args)
        {
            Calls.Add([.. args]);
            var output = new StringWriter();
            var error = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", home, "--no-mesh"], output, error, source);
            return (code, output.ToString(), error.ToString());
        }
    }

    /// <summary>U3.1: two homes with the same entries show the same Fleet, byte for byte, as the stream does (C23's test, reused).</summary>
    [Fact]
    public void U3_1_TheSameEntriesShowTheSameFleetByteForByte()
    {
        using var dir = new TempHome();
        var (home, owner) = ActivityStreamTests.NewHome(dir);
        var (ledger, entries, a, b, c) = Working(owner, home);
        var now = T0.AddMinutes(30);
        var expected = FleetView.Document(ledger, now);

        foreach (var seed in new[] { 1, 2, 3, 4, 5 })
        {
            var random = new Random(seed);
            var shuffled = entries.OrderBy(_ => random.Next()).ToList();
            var other = new WorkLedger(owner.PublicKey);
            other.Merge(shuffled.Take(shuffled.Count / 2));
            other.Merge(shuffled.Skip(shuffled.Count / 2));
            Assert.Equal(expected, FleetView.Document(other, now));
        }

        var synced = new WorkLedger(owner.PublicKey);
        synced.Merge(Enumerable.Reverse(entries));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(FleetView.Document(synced, now)));

        // C23's own fleet, whose claims are all closed: the same holds there, and no row holds a claim.
        var (streamLedger, streamEntries, _) = ActivityStreamTests.Fleet(owner, a, b);
        var mirrored = new WorkLedger(owner.PublicKey);
        mirrored.Merge(Enumerable.Reverse(streamEntries));
        Assert.Equal(FleetView.Document(streamLedger, now), FleetView.Document(mirrored, now));
        Assert.All(FleetView.Rows(streamLedger, now), r => Assert.Null(r.Claim));

        // What the Fleet shows: the most recently seen first, the claim and its last progress, blocked and stress.
        var rows = FleetView.Rows(ledger, now);
        Assert.Equal([b.Id, a.Id, c.Id], rows.Select(r => r.Cell));
        Assert.Equal(("rotate the logs", "disk full", 0.9, true, 0.42), (rows[0].Claim, rows[0].Progress, rows[0].Stress!.Value, rows[0].Blocked, rows[0].Load!.Value));
        Assert.Equal(("codex on second", b.Identity.Creatura.Value), (rows[0].Name, rows[0].Creatura));   // the ledger knows a creatura by its id
        Assert.Equal(("migration step 16", "golden diff empty", false), (rows[1].Claim, rows[1].Progress, rows[1].Blocked));
        Assert.Null(rows[2].Claim);

        // The console reads the same document back into the same rows.
        Assert.Equal(rows, FleetModel.Parse(expected).Rows);
        Assert.Equal(now, FleetModel.Parse(expected).At);
    }

    /// <summary>U3.2: an offer made from the console is the same entry <c>cell offer</c> writes, and the stream shows it.</summary>
    [Fact]
    public void U3_2_AnOfferFromTheConsoleIsCellOffersOwnEntry()
    {
        using var dir = new TempHome();
        var cli = new InProcess(dir.Path, new FakeVessel("raw-console", "air"));
        Assert.Equal(0, cli.Run(["cell", "init"]).Code);
        var model = new FleetModel(cli);
        var fleet = model.Read();
        Assert.Null(fleet.Problem);
        var me = Assert.Single(fleet.Rows);

        // From the console, then by hand, the same offer.
        var (ok, said) = model.Offer(me.Cell, "rotate the logs");
        Assert.True(ok, said);
        Assert.StartsWith("offered", said);
        Assert.Equal(["cell", "offer", "--to", me.Cell, "--goal", "rotate the logs"], cli.Calls[^1]);
        Assert.Equal(0, cli.Run(["cell", "offer", "--to", me.Cell, "--goal", "rotate the logs"]).Code);

        // The stream shows both, as the same activity but for its id and time.
        var stream = JsonNode.Parse(cli.Run(["cell", "activity"]).Output)!.AsObject();
        var offers = stream["orderedItems"]!.AsArray().Select(n => n!.AsObject()).Where(o => (string?)o["object"]?["type"] == "creatura:Goal").ToList();
        Assert.Equal(2, offers.Count);
        Assert.Equal((string?)offers[0]["type"], (string?)offers[1]["type"]);
        Assert.Equal((string?)offers[0]["summary"], (string?)offers[1]["summary"]);
        Assert.Equal((string?)offers[0]["actor"], (string?)offers[1]["actor"]);
        Assert.Equal("rotate the logs", (string?)offers[0]["object"]!["name"]);
        Assert.Equal(2, Assert.Single(model.Read().Rows).PendingOffers);

        // A goal left empty is not offered, and nothing is run for it.
        var calls = cli.Calls.Count;
        Assert.False(model.Offer(me.Cell, "  ").Ok);
        Assert.Equal(calls, cli.Calls.Count);
        // A command that fails says why.
        var (refused, why) = model.Offer("cell-nobody", "x");
        Assert.False(refused);
        Assert.NotEqual("", why);

        // The cell's activity opens as a run, every entry it wrote a row.
        var run = model.CellRun(me);
        var mine = stream["orderedItems"]!.AsArray().Count(n => (string?)n!["actor"] == $"urn:creatura:cell:{me.Cell}");
        Assert.Equal(mine, run.Rows.Count);
        Assert.Equal(me.Name, Assert.Single(run.Cells));

        // The program's text view, through the same command.
        var output = new StringWriter();
        Assert.Equal(0, ConsoleProgram.Fleet(["--print", "--as", "creatura"], output, new StringWriter(), redirected: false, cli));
        Assert.Contains(me.Name, output.ToString());
        Assert.Contains("2 offers waiting", output.ToString());
    }

    /// <summary>U3.3: a revoked cell is absent; a quiet vessel is dimmed after the stream's quiet time.</summary>
    [Fact]
    public void U3_3_ARevokedCellIsAbsentAndAQuietOneDimmed()
    {
        using var dir = new TempHome();
        var (home, owner) = ActivityStreamTests.NewHome(dir);
        var (ledger, _, a, b, c) = Working(owner, home);
        var last = ledger.Entries.Max(e => e.Timestamp);

        // Within the quiet time nobody is quiet; past it, a cell last seen before then is.
        var fresh = FleetView.Rows(ledger, last.AddMinutes(10));
        Assert.All(fresh, r => Assert.False(r.Quiet));
        var later = FleetView.Rows(ledger, ledger.Entries.Where(e => e.Cell == c.Id).Max(e => e.Timestamp) + FleetView.QuietTime + TimeSpan.FromSeconds(1));
        Assert.True(later.Single(r => r.Cell == c.Id).Quiet);
        Assert.False(later.Single(r => r.Cell == b.Id).Quiet);
        Assert.Equal(15, FleetView.QuietTime.TotalMinutes);                   // the stream page's QUIET_MINUTES

        // Dimmed when quiet, red when blocked or stressed whatever else, plain otherwise.
        var quiet = later.Single(r => r.Cell == c.Id);
        Assert.Equal(FleetLook.Dim, FleetScreen.LookOf(quiet));
        Assert.Equal(FleetLook.Alarm, FleetScreen.LookOf(later.Single(r => r.Cell == b.Id)));
        Assert.Equal(FleetLook.Alarm, FleetScreen.LookOf(quiet with { Stress = 0.75 }));
        Assert.Equal(FleetLook.Plain, FleetScreen.LookOf(fresh.Single(r => r.Cell == a.Id)));
        Assert.Contains("QUIET, seen", FleetView.Render(later, last + FleetView.QuietTime + TimeSpan.FromMinutes(5)));

        // Revoked: gone from the Fleet, the others as they were.
        Assert.True(ledger.Revoke(owner.Revoke(b.Identity.PublicKey, ledger.NextSequence(b.Id), "the laptop was lost", last)));
        var after = FleetView.Rows(ledger, last.AddMinutes(10));
        Assert.DoesNotContain(after, r => r.Cell == b.Id);
        Assert.Equal(new[] { a.Id, c.Id }.Order(StringComparer.Ordinal), after.Select(r => r.Cell).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(b.Id, FleetView.Document(ledger, last));
    }
}
