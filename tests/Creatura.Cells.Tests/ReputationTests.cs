using System.Text.Json.Nodes;

namespace Creatura.Cells.Tests;

/// <summary>Cell reputation (docs/design-ratings.md, slice R2): G6 to G8, written before the code.</summary>
public sealed class ReputationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private const string Goal = "build and publish the shared shopping list";

    /// <summary>Cells a and b on one goal in two homes, and the owner's own cell o, which holds no claim; all three hellos everywhere.</summary>
    private sealed class Trio : IDisposable
    {
        public Trio()
        {
            var home = new CellHome(Dir.Path);
            Owner = home.EnsureOwner(out _);
            A = home.Endorse(home.OpenCell(new FakeVessel("raw-a", "creature-a"), Position.InProcess("a"), "a"));
            B = home.Endorse(home.OpenCell(new FakeVessel("raw-b", "creature-b"), Position.InProcess("b"), "b"));
            O = home.Endorse(home.OpenCell(new FakeVessel("raw-o", "owner-box"), Position.InProcess("o"), "owner"));
            La = new WorkLedger(Owner.PublicKey);
            Lb = new WorkLedger(Owner.PublicKey);
            La.Append(A, new HelloPayload(A.Identity), T0);
            Lb.Append(B, new HelloPayload(B.Identity), T0);
            La.Append(O, new HelloPayload(O.Identity), T0.AddSeconds(1));
            ClaimA = La.Append(A, new ClaimPayload(Goal), T0.AddSeconds(2)).Id;
            ClaimB = Lb.Append(B, new ClaimPayload(Goal), T0.AddSeconds(3)).Id;
            Sync();
        }

        public TempHome Dir { get; } = new();

        public OwnerKey Owner { get; }

        public LocalCell A { get; }

        public LocalCell B { get; }

        public LocalCell O { get; }

        public WorkLedger La { get; }

        public WorkLedger Lb { get; }

        public string ClaimA { get; }

        public string ClaimB { get; }

        public void Sync()
        {
            Assert.Empty(La.Merge(Lb.Entries).Rejected);
            Assert.Empty(Lb.Merge(La.Entries).Rejected);
        }

        public (Move Move, string Rule, TurnResult Turn) Both(LocalCell cell, DateTimeOffset now)
        {
            var inA = La.Turn(cell.Id, now);
            var inB = Lb.Turn(cell.Id, now);
            Assert.Equal(inA.ToJson(), inB.ToJson());
            return (inA.Move, inA.Rule, inA);
        }

        public void Dispose()
        {
            Owner.Dispose();
            A.Dispose();
            B.Dispose();
            O.Dispose();
            Dir.Dispose();
        }
    }

    private static SignedEntry SignUntil(Func<SignedEntry> sign, Func<SignedEntry, bool> condition)
    {
        for (var i = 0; i < 1000; i++)
        {
            var entry = sign();
            if (condition(entry)) return entry;
        }

        throw new InvalidOperationException("no signature met the condition in 1000 tries");
    }

    private static bool Lower(SignedEntry a, SignedEntry b) => string.CompareOrdinal(a.Hash(), b.Hash()) < 0;

    /// <summary>
    /// G6: after a shared job the owner's cell writes one reputation entry per cell that ran it, signed by the owner key;
    /// a cell cannot write one for itself or its peer, and a ledger refuses one not signed by an owner it trusts.
    /// Falsifier: a self-rating accepted, or an unsigned one.
    /// </summary>
    [Fact]
    public void G6_TheOwnersCellRatesTheWorkersAndNobodyElseMay()
    {
        using var t = new Trio();
        var ra = t.Owner.Rate(t.A.Id, "run-1", 27.6, 8.1, 1, shipped: true, falseClaims: 0, contractKept: true, T0.AddMinutes(10));
        var rb = t.Owner.Rate(t.B.Id, "run-1", 22.4, 8.1, 1, shipped: true, falseClaims: 2, contractKept: true, T0.AddMinutes(10));
        Assert.True(ra.Verify());
        t.La.Append(t.O, new ReputationPayload(ra), T0.AddMinutes(10));
        t.La.Append(t.O, new ReputationPayload(rb), T0.AddMinutes(10));
        t.Sync();
        Assert.Equal(27.6, t.Lb.ReputationOf(t.A.Id)!.Mu);
        Assert.Equal(["a", "b"], t.Lb.Reputations().Select(r => r.Cell == t.A.Id ? "a" : "b"));

        // A worker rating itself, or its peer: refused by every home, however well the owner signature verifies.
        var self = SignedEntry.Sign(t.A, t.La.NextSequence(t.A.Id), new ReputationPayload(t.Owner.Rate(t.A.Id, "run-1", 40, 1, 2, true, 0, true, T0.AddMinutes(11))), T0.AddMinutes(11));
        Assert.Contains("rating itself", t.La.Merge([self]).Rejected.Single().Reason);
        var peer = SignedEntry.Sign(t.A, t.La.NextSequence(t.A.Id), new ReputationPayload(t.Owner.Rate(t.B.Id, "run-1", 1, 1, 2, false, 9, false, T0.AddMinutes(11))), T0.AddMinutes(11));
        Assert.Contains("a worker never rates", t.Lb.Merge([peer]).Rejected.Single().Reason);
        Assert.Equal(22.4, t.Lb.ReputationOf(t.B.Id)!.Mu);

        // Not the owner's signature: refused.
        using var stranger = OwnerKey.Generate();
        var forged = SignedEntry.Sign(t.O, t.La.NextSequence(t.O.Id), new ReputationPayload(stranger.Rate(t.A.Id, "run-1", 40, 1, 2, true, 0, true, T0.AddMinutes(12))), T0.AddMinutes(12));
        Assert.Contains("does not trust", t.La.Merge([forged]).Rejected.Single().Reason);
        var tampered = SignedEntry.Sign(t.O, t.La.NextSequence(t.O.Id), new ReputationPayload(ra with { Mu = 99 }), T0.AddMinutes(12));
        Assert.Contains("does not verify", t.La.Merge([tampered]).Rejected.Single().Reason);

        // The stream shows the rating as the owner's act on the cell.
        var items = JsonNode.Parse(ActivityStream.Document(t.La))!["orderedItems"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var rated = items.Where(i => (string?)i["type"] == "creatura:Rate").ToList();
        Assert.Equal(2, rated.Count);
        Assert.Contains(rated, i => ((string?)i["summary"])!.Contains("2 false claims", StringComparison.Ordinal));
    }

    /// <summary>
    /// G7: turn with reputation on breaks a competing-proposal tie by the higher reputation when both ledgers hold the
    /// same entries, and by the hash as before when they do not. Falsifier: a tie broken differently in the two homes.
    /// </summary>
    [Fact]
    public void G7_TurnBreaksATieByReputationWhenBothHomesHoldItAndByHashOtherwise()
    {
        using var t = new Trio();
        // The owner rated a above b before either proposes; both homes hold it.
        t.La.Append(t.O, new ReputationPayload(t.Owner.Rate(t.A.Id, "run-1", 30, 5, 3, true, 0, true, T0.AddMinutes(1))), T0.AddMinutes(1));
        t.La.Append(t.O, new ReputationPayload(t.Owner.Rate(t.B.Id, "run-1", 20, 5, 3, true, 2, true, T0.AddMinutes(1))), T0.AddMinutes(1));
        t.Sync();

        // Competing proposals, b's with the lower hash: by hash b would win; by reputation a wins, in both homes alike.
        var pa = SignedEntry.Sign(t.A, t.La.NextSequence(t.A.Id), t.La.Propose(t.ClaimA, "plan", "a: the server in repo-a"), T0.AddMinutes(5));
        var pb = SignUntil(() => SignedEntry.Sign(t.B, t.Lb.NextSequence(t.B.Id), t.Lb.Propose(t.ClaimB, "plan", "b: the server in repo-b"), T0.AddMinutes(6)), e => Lower(e, pa));
        Assert.Empty(t.La.Merge([pa]).Rejected);
        Assert.Empty(t.Lb.Merge([pb]).Rejected);
        t.Sync();
        var now = T0.AddMinutes(7);
        var a = t.Both(t.A, now);
        var b = t.Both(t.B, now);
        Assert.Equal((Move.Wait, "T6"), (a.Move, a.Rule));
        Assert.Equal((Move.Act, "T6"), (b.Move, b.Rule));
        Assert.Contains("higher reputation", b.Turn.Reason);
        var topic = Assert.Single(a.Turn.Topics);
        Assert.Equal((pa.Id, "reputation"), (topic.Live.Id, topic.LiveBy));
        Assert.Contains("\"liveBy\":\"reputation\"", a.Turn.ToJson());

        // Rated only after the proposals (or in one home only): the hash decides, the same in both homes.
        using var u = new Trio();
        var qa = SignedEntry.Sign(u.A, u.La.NextSequence(u.A.Id), u.La.Propose(u.ClaimA, "plan", "a's plan"), T0.AddMinutes(5));
        var qb = SignUntil(() => SignedEntry.Sign(u.B, u.Lb.NextSequence(u.B.Id), u.Lb.Propose(u.ClaimB, "plan", "b's plan"), T0.AddMinutes(6)), e => Lower(e, qa));
        Assert.Empty(u.La.Merge([qa]).Rejected);
        Assert.Empty(u.Lb.Merge([qb]).Rejected);
        u.Sync();
        u.La.Append(u.O, new ReputationPayload(u.Owner.Rate(u.A.Id, "run-1", 30, 5, 3, true, 0, true, T0.AddMinutes(8))), T0.AddMinutes(8));
        u.La.Append(u.O, new ReputationPayload(u.Owner.Rate(u.B.Id, "run-1", 20, 5, 3, true, 2, true, T0.AddMinutes(8))), T0.AddMinutes(8));
        // a's home holds the late ratings, b's does not yet: both still pick b's proposal, by hash.
        foreach (var ledger in new[] { u.La, u.Lb })
        {
            var turn = ledger.Turn(u.A.Id, T0.AddMinutes(9));
            var tp = Assert.Single(turn.Topics);
            Assert.Equal((qb.Id, "hash"), (tp.Live.Id, tp.LiveBy));
        }
    }

    /// <summary>
    /// G8: a reputation rises with shipped runs and no false claims and falls with false claims, by the same update as
    /// R1's, through <c>creatura cell rate</c> on the owner's vessel. Falsifier: anything else.
    /// </summary>
    [Fact]
    public void G8_CellRateMovesReputationsByTheWengLinUpdate()
    {
        using var home = new TempHome();
        var vessel = new FakeVessel("11111111-2222-3333-4444-555555555555", "air");
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "init", "--home", home.Path, "--no-mesh"], output, output, vessel));
        var results = Path.Combine(home.Path, "results.json");
        File.WriteAllText(results, """[{"cell":"cell-aaaa","shipped":true,"falseClaims":0,"contractKept":true},{"cell":"cell-bbbb","shipped":true,"falseClaims":2,"contractKept":true}]""");
        output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "rate", "--home", home.Path, "--no-mesh", "--job", "run-1", "--results", results], output, output, vessel));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, lines.Count);
        var a1 = (double)lines.Single(l => (string?)l["cell"] == "cell-aaaa")["mu"]!;
        var b1 = (double)lines.Single(l => (string?)l["cell"] == "cell-bbbb")["mu"]!;
        Assert.True(a1 > 25 && b1 < 25);
        Assert.Equal(25 - b1, a1 - 25, 6);
        Assert.Equal(1, (int)lines[0]["games"]!);

        // The same job again with the roles reversed: b wins back, both have played 2; the ledger holds the latest per cell.
        File.WriteAllText(results, """[{"cell":"cell-aaaa","shipped":false,"falseClaims":1,"contractKept":false},{"cell":"cell-bbbb","shipped":true,"falseClaims":0,"contractKept":true}]""");
        output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "rate", "--home", home.Path, "--no-mesh", "--job", "run-2", "--results", results], output, output, vessel));
        lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        var a2 = (double)lines.Single(l => (string?)l["cell"] == "cell-aaaa")["mu"]!;
        var b2 = (double)lines.Single(l => (string?)l["cell"] == "cell-bbbb")["mu"]!;
        Assert.True(a2 < a1 && b2 > b1);
        Assert.All(lines, l => Assert.Equal(2, (int)l["games"]!));

        output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "reputations", "--home", home.Path, "--no-mesh"], output, output, vessel));
        Assert.Contains("2 rated cells", output.ToString());
        Assert.Contains("run-2", output.ToString());

        // Fewer than two cells is no game; a worker's cell may not rate.
        File.WriteAllText(results, """[{"cell":"cell-aaaa","shipped":true,"falseClaims":0,"contractKept":true}]""");
        Assert.NotEqual(0, CellCommand.Run(["cell", "rate", "--home", home.Path, "--no-mesh", "--job", "run-3", "--results", results], new StringWriter(), new StringWriter(), vessel));
        Assert.Equal(0, CellCommand.Run(["cell", "claim", "--home", home.Path, "--no-mesh", "--goal", "now a worker"], new StringWriter(), new StringWriter(), vessel));
        File.WriteAllText(results, """[{"cell":"cell-aaaa","shipped":true,"falseClaims":0,"contractKept":true},{"cell":"cell-bbbb","shipped":true,"falseClaims":0,"contractKept":true}]""");
        var error = new StringWriter();
        Assert.NotEqual(0, CellCommand.Run(["cell", "rate", "--home", home.Path, "--no-mesh", "--job", "run-3", "--results", results], new StringWriter(), error, vessel));
        Assert.Contains("a worker never rates", error.ToString());
    }
}
