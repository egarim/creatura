using System.Text.Json.Nodes;
using Creatura.Cells.Sync;

namespace Creatura.Cells.Tests;

/// <summary>
/// The pre-registered tests of docs/design-turns.md (C30 to C37), written before the code. Each test's name is its
/// id; its summary is the claim, and the assertions are the falsifier as the note states it. Two cells of one owner,
/// each with a home and a ledger of its own, sync by merging each other's entries, as two syncs through the hub would.
/// </summary>
public class TurnTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private const string Goal = "build and publish the shared shopping list";

    /// <summary>Cells a and b on one goal, each holding both hellos and both claims, as after a first sync.</summary>
    private sealed class Pair : IDisposable
    {
        public Pair()
        {
            var home = new CellHome(Dir.Path);
            Owner = home.EnsureOwner(out _);
            A = home.Endorse(home.OpenCell(new FakeVessel("raw-a", "creature-a"), Position.InProcess("a"), "a"));
            B = home.Endorse(home.OpenCell(new FakeVessel("raw-b", "creature-b"), Position.InProcess("b"), "b"));
            La = new WorkLedger(Owner.PublicKey);
            Lb = new WorkLedger(Owner.PublicKey);
            La.Append(A, new HelloPayload(A.Identity), T0);
            Lb.Append(B, new HelloPayload(B.Identity), T0);
            ClaimA = La.Append(A, new ClaimPayload(Goal) { Tool = "claude-code" }, T0.AddSeconds(1)).Id;
            ClaimB = Lb.Append(B, new ClaimPayload(Goal) { Tool = "claude-code" }, T0.AddSeconds(2)).Id;
            Sync();
        }

        public TempHome Dir { get; } = new();

        public OwnerKey Owner { get; }

        public LocalCell A { get; }

        public LocalCell B { get; }

        /// <summary>Cell a's home.</summary>
        public WorkLedger La { get; }

        /// <summary>Cell b's home.</summary>
        public WorkLedger Lb { get; }

        public string ClaimA { get; }

        public string ClaimB { get; }

        public IEnumerable<WorkLedger> Homes => [La, Lb];

        /// <summary>Both homes take what the other holds.</summary>
        public void Sync()
        {
            Assert.Empty(La.Merge(Lb.Entries).Rejected);
            Assert.Empty(Lb.Merge(La.Entries).Rejected);
        }

        /// <summary>The move and rule for a cell in both homes, asserted the same in both.</summary>
        public (Move Move, string Rule, TurnResult Turn) Both(LocalCell cell, DateTimeOffset now, TimeSpan? quiet = null)
        {
            var inA = La.Turn(cell.Id, now, quiet);
            var inB = Lb.Turn(cell.Id, now, quiet);
            Assert.Equal(inA.ToJson(), inB.ToJson());
            return (inA.Move, inA.Rule, inA);
        }

        public void Dispose()
        {
            Owner.Dispose();
            A.Dispose();
            B.Dispose();
            Dir.Dispose();
        }
    }

    /// <summary>Signs again until the entry satisfies the condition: a signature is random, so each try has a new hash.</summary>
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
    /// C30: two cells that each wait on the other get exactly one act between them from <c>turn</c>, the same in both
    /// homes once synced. Falsifier: none or both acting, or the homes disagreeing. And a wait ends at the waiter's
    /// next entry: once the mover moved, the cycle is gone.
    /// </summary>
    [Fact]
    public void C30_APolitePairGetsExactlyOneActBetweenThemTheSameInBothHomes()
    {
        using var p = new Pair();
        p.La.Append(p.A, TurnLedger.Wait(p.ClaimA, p.B.Id, "waiting for your plan"), T0.AddMinutes(1));
        p.Lb.Append(p.B, TurnLedger.Wait(p.ClaimB, p.A.Id, "waiting for your plan"), T0.AddMinutes(2));
        p.Sync();

        var now = T0.AddMinutes(3);
        var a = p.Both(p.A, now);
        var b = p.Both(p.B, now);
        Assert.Equal(1, new[] { a.Move, b.Move }.Count(m => m == Move.Act));
        // The cycle rule: b's wait is the latest, so b moves and a waits; both say why.
        Assert.Equal((Move.Wait, "T3"), (a.Move, a.Rule));
        Assert.Equal((Move.Act, "T3"), (b.Move, b.Rule));
        Assert.Contains("deadlock", b.Turn.Reason);
        Assert.Contains(p.B.Id, a.Turn.Reason);
        Assert.Equal(3, a.Turn.ExitCode);
        Assert.Equal(0, b.Turn.ExitCode);

        // The mover moves: a step ends its wait, so the cycle is gone. b is free, a waits on a peer that is moving.
        p.Lb.Append(p.B, new ProgressPayload(p.ClaimB, "drafting a plan"), T0.AddMinutes(4));
        p.Sync();
        now = T0.AddMinutes(5);
        Assert.Equal((Move.Act, "T7"), p.Both(p.B, now) is var b2 ? (b2.Move, b2.Rule) : default);
        Assert.Equal((Move.Wait, "T5"), p.Both(p.A, now) is var a2 ? (a2.Move, a2.Rule) : default);

        // A fresh wait by a, after b moved, is a plain wait, not a deadlock: b's old wait does not outlive b's step.
        p.La.Append(p.A, TurnLedger.Wait(p.ClaimA, p.B.Id, "still waiting for the plan"), T0.AddMinutes(6));
        p.Sync();
        now = T0.AddMinutes(7);
        Assert.Equal((Move.Wait, "T5"), p.Both(p.A, now) is var a3 ? (a3.Move, a3.Rule) : default);
        Assert.Equal((Move.Act, "T7"), p.Both(p.B, now) is var b3 ? (b3.Move, b3.Rule) : default);
        Assert.Contains("waits on you", b3.Turn.Reason);
    }

    /// <summary>
    /// C31: a cell that hasn't synced the other's wait still gets act (T7), and after syncing the cycle rule picks the
    /// same single mover. Falsifier: a cell stopped by a wait it has not seen, or two movers once both have both waits.
    /// </summary>
    [Fact]
    public void C31_ACellThatHasNotSyncedTheOthersWaitStillActsAndTheCycleRulePicksOneMoverAfter()
    {
        using var p = new Pair();
        // b waits on a; a has not synced it. a's move is its own, before and after the sync: someone waiting on you does not stop you.
        p.Lb.Append(p.B, TurnLedger.Wait(p.ClaimB, p.A.Id, "waiting for the api"), T0.AddMinutes(1));
        var now = T0.AddMinutes(2);
        Assert.Equal((Move.Act, "T7"), p.La.Turn(p.A.Id, now) is var before ? (before.Move, before.Rule) : default);
        Assert.Null(before.Waiting);
        Assert.Equal((Move.Wait, "T5"), p.Lb.Turn(p.B.Id, now) is var bWaits ? (bWaits.Move, bWaits.Rule) : default);
        p.Sync();
        var after = p.La.Turn(p.A.Id, now);
        Assert.Equal((Move.Act, "T7"), (after.Move, after.Rule));
        Assert.Contains($"{p.B.Id} waits on you", after.Reason);

        // Now a waits on b too, and b has not synced that: b still waits on a peer that is moving, while a already sees the cycle.
        p.La.Append(p.A, TurnLedger.Wait(p.ClaimA, p.B.Id, "waiting for the ui"), T0.AddMinutes(3));
        now = T0.AddMinutes(4);
        Assert.Equal((Move.Act, "T3"), p.La.Turn(p.A.Id, now) is var aSees ? (aSees.Move, aSees.Rule) : default);
        Assert.Equal((Move.Wait, "T5"), p.Lb.Turn(p.B.Id, now) is var bStill ? (bStill.Move, bStill.Rule) : default);

        // Once both hold both waits: one mover, the same in both homes. a's wait is the latest, so a moves.
        p.Sync();
        var a = p.Both(p.A, now);
        var b = p.Both(p.B, now);
        Assert.Equal((Move.Act, "T3"), (a.Move, a.Rule));
        Assert.Equal((Move.Wait, "T3"), (b.Move, b.Rule));
        Assert.Equal(1, new[] { a.Move, b.Move }.Count(m => m == Move.Act));
    }

    /// <summary>
    /// C32: two proposals on one topic, neither seeing the other, pick the same winner (lowest hash) in both homes, and
    /// the loser gets act: answer the winner. Falsifier: the homes disagreeing, a winner picked by clock, or the loser
    /// not told to answer.
    /// </summary>
    [Fact]
    public void C32_SimultaneousProposalsPickTheSameWinnerByHashInBothHomesAndTheLoserAnswers()
    {
        using var p = new Pair();
        // Each proposes in its own home before any sync, so neither saw the other's. Signed until the lower hash is the
        // later one by clock: a pick by timestamp would then differ from the pick by hash.
        var pa = SignedEntry.Sign(p.A, p.La.NextSequence(p.A.Id), p.La.Propose(p.ClaimA, "plan", "a: the server in repo-a, the ui in repo-b"), T0.AddMinutes(1));
        var pb = SignUntil(() => SignedEntry.Sign(p.B, p.Lb.NextSequence(p.B.Id), p.Lb.Propose(p.ClaimB, "plan", "b: the ui in repo-a, the server in repo-b"), T0.AddMinutes(2)), e => Lower(e, pa));
        Assert.Empty(p.La.Merge([pa]).Rejected);
        Assert.Empty(p.Lb.Merge([pb]).Rejected);
        Assert.Empty(pa.Body<ProgressPayload>().Seen!);
        Assert.Empty(pb.Body<ProgressPayload>().Seen!);
        p.Sync();

        var now = T0.AddMinutes(3);
        var a = p.Both(p.A, now);
        var b = p.Both(p.B, now);
        // b's proposal has the lower hash and the later clock: it wins by hash. a answers; b waits for the answer.
        Assert.Equal((Move.Act, "T6"), (a.Move, a.Rule));
        Assert.Contains(pb.Id, a.Turn.Reason);
        Assert.Contains("answer", a.Turn.Reason);
        Assert.Equal((Move.Wait, "T6"), (b.Move, b.Rule));
        foreach (var turn in new[] { a.Turn, b.Turn })
        {
            var topic = Assert.Single(turn.Topics);
            Assert.Equal(pb.Id, topic.Live.Id);
            Assert.Equal(new[] { pa.Id, pb.Id }.Order(), topic.Competitors.Select(c => c.Id).Order());
            Assert.False(topic.Settled);
        }

        // The loser accepts: the topic is settled and both move on. The winner is the plan.
        p.La.Append(p.A, TurnLedger.Answer(p.ClaimA, pb.Id, AnswerVerdict.Accept, "fine by me"), T0.AddMinutes(4));
        p.Sync();
        now = T0.AddMinutes(5);
        foreach (var cell in new[] { p.A, p.B })
        {
            var turn = p.Both(cell, now).Turn;
            Assert.Equal((Move.Act, "T7"), (turn.Move, turn.Rule));
            var topic = Assert.Single(turn.Topics);
            Assert.True(topic.Settled);
            Assert.Equal(pb.Id, topic.Plan?.Id);
        }
    }

    /// <summary>
    /// C33: a proposal with an earlier (even backdated) timestamp that arrives after the other cell acted doesn't win by
    /// timestamp; the hash decides, and a proposal that saw the other is a counter, not a competitor. Falsifier: the
    /// backdated proposal winning, or the counter competing.
    /// </summary>
    [Fact]
    public void C33_ALateProposalWithAnEarlierClockDoesNotWinAndOneThatSawTheOtherIsACounter()
    {
        using var p = new Pair();
        // a proposes at +5 and, hearing nothing, starts on it.
        var pa = p.La.Append(p.A, p.La.Propose(p.ClaimA, "plan", "a: the server in repo-a, the ui in repo-b"), T0.AddMinutes(5));
        p.La.Append(p.A, new ProgressPayload(p.ClaimA, "scaffolding the server"), T0.AddMinutes(6));

        // b's proposal, with its clock at +1 and never having seen a's, arrives after that. Signed until its hash is the
        // higher, so a pick by clock would take it and a pick by hash does not.
        var pb = SignUntil(() => SignedEntry.Sign(p.B, p.Lb.NextSequence(p.B.Id), p.Lb.Propose(p.ClaimB, "plan", "b: everything in repo-b"), T0.AddMinutes(1)), e => Lower(pa, e));
        Assert.Empty(p.Lb.Merge([pb]).Rejected);
        p.Sync();
        var now = T0.AddMinutes(7);
        var a = p.Both(p.A, now);
        var b = p.Both(p.B, now);
        Assert.Equal(pa.Id, Assert.Single(a.Turn.Topics).Live.Id);
        Assert.Equal((Move.Wait, "T6"), (a.Move, a.Rule));
        Assert.Equal((Move.Act, "T6"), (b.Move, b.Rule));
        Assert.Contains(pa.Id, b.Turn.Reason);

        // b, having seen both, counters. Whatever its hash, a counter is not a competitor: it is what a must answer now,
        // and a's proposal stays the plan only if it was accepted, which it was not.
        var counter = SignUntil(() => SignedEntry.Sign(p.B, p.Lb.NextSequence(p.B.Id), p.Lb.Propose(p.ClaimB, "plan", "b: a's split, with the api behind the ui"), T0.AddMinutes(8)), e => Lower(pa, e));
        Assert.Equal(new[] { pa.Id, pb.Id }.Order(), counter.Body<ProgressPayload>().Seen!.Order());
        Assert.Empty(p.Lb.Merge([counter]).Rejected);
        p.Sync();
        now = T0.AddMinutes(9);
        a = p.Both(p.A, now);
        b = p.Both(p.B, now);
        var topic = Assert.Single(a.Turn.Topics);
        Assert.Equal(counter.Id, topic.Live.Id);
        Assert.Empty(topic.Competitors);
        Assert.Null(topic.Plan);
        Assert.Equal((Move.Act, "T1"), (a.Move, a.Rule));
        Assert.Contains(counter.Id, a.Turn.Reason);
        Assert.Equal((Move.Wait, "T6"), (b.Move, b.Rule));
    }

    /// <summary>
    /// C34: accept settles; object leaves the proposer with act; silence past the quiet time lets the proposer proceed
    /// and says so. Falsifier: an accepted proposal not settled, an objected proposer told to wait, or a proposer kept
    /// waiting on a party that went quiet, or let go without saying it was by silence.
    /// </summary>
    [Fact]
    public void C34_AcceptSettlesObjectGivesTheProposerTheMoveAndSilencePastTheQuietTimeLetsItProceed()
    {
        using var p = new Pair();
        // Accept settles.
        var plan = p.La.Append(p.A, p.La.Propose(p.ClaimA, "plan", "a: the server in repo-a, the ui in repo-b"), T0.AddMinutes(1));
        p.Sync();
        var now = T0.AddMinutes(2);
        Assert.Equal((Move.Act, "T1"), p.Both(p.B, now) is var bAsked ? (bAsked.Move, bAsked.Rule) : default);
        Assert.Equal((Move.Wait, "T6"), p.Both(p.A, now) is var aWaits ? (aWaits.Move, aWaits.Rule) : default);
        p.Lb.Append(p.B, TurnLedger.Answer(p.ClaimB, plan.Id, AnswerVerdict.Accept, "agreed"), T0.AddMinutes(3));
        p.Sync();
        now = T0.AddMinutes(4);
        foreach (var cell in new[] { p.A, p.B })
        {
            var turn = p.Both(cell, now).Turn;
            Assert.Equal((Move.Act, "T7"), (turn.Move, turn.Rule));
            var settled = Assert.Single(turn.Topics);
            Assert.True(settled.Settled);
            Assert.False(settled.BySilence);
            Assert.Equal(plan.Id, settled.Plan?.Id);
        }

        // Object leaves the proposer with act, and the objector waiting on it: the proposer has the move.
        var api = p.La.Append(p.A, p.La.Propose(p.ClaimA, "api", "REST on port 3000"), T0.AddMinutes(5));
        p.Sync();
        p.Lb.Append(p.B, TurnLedger.Answer(p.ClaimB, api.Id, AnswerVerdict.Object, "8080 inside the network, the ui passes /api through"), T0.AddMinutes(6));
        p.Sync();
        now = T0.AddMinutes(7);
        var a = p.Both(p.A, now);
        var b = p.Both(p.B, now);
        Assert.Equal((Move.Act, "T2"), (a.Move, a.Rule));
        Assert.Contains("objected", a.Turn.Reason);
        Assert.Contains("8080 inside", a.Turn.Reason);
        Assert.Equal((Move.Wait, "T6"), (b.Move, b.Rule));
        Assert.Contains("has the move", b.Turn.Reason);
        var objected = a.Turn.Topics.Single(t => t.Topic == "api");
        Assert.Equal([p.B.Id], objected.Objected);
        Assert.False(objected.Settled);
        Assert.Null(objected.Plan);

        // The proposer revises; b says nothing more. Past the quiet time, a may proceed, and is told it is by silence.
        var revised = p.La.Append(p.A, p.La.Propose(p.ClaimA, "api", "REST on 8080 inside, the ui passes /api through"), T0.AddMinutes(8));
        Assert.Equal([api.Id], revised.Body<ProgressPayload>().Seen);
        p.Sync();
        now = T0.AddMinutes(9);
        Assert.Equal((Move.Wait, "T6"), p.Both(p.A, now) is var soon ? (soon.Move, soon.Rule) : default);
        Assert.Equal((Move.Act, "T1"), p.Both(p.B, now) is var bAgain ? (bAgain.Move, bAgain.Rule) : default);

        now = T0.AddMinutes(19);  // b last wrote at +6: 13 minutes, past the default quiet time of 10
        var late = p.Both(p.A, now);
        Assert.Equal((Move.Act, "T6"), (late.Move, late.Rule));
        Assert.Contains("silence", late.Turn.Reason);
        var bySilence = late.Turn.Topics.Single(t => t.Topic == "api");
        Assert.True(bySilence.BySilence);
        Assert.False(bySilence.Settled);
        Assert.Equal([p.B.Id], bySilence.Silent);
        Assert.Equal(revised.Id, bySilence.Live.Id);
        // With a longer quiet time it still waits; and silence does not free b from answering.
        Assert.Equal((Move.Wait, "T6"), p.Both(p.A, now, TimeSpan.FromMinutes(20)) is var patient ? (patient.Move, patient.Rule) : default);
        Assert.Equal((Move.Act, "T1"), p.Both(p.B, now) is var bLate ? (bLate.Move, bLate.Rule) : default);
    }

    /// <summary>
    /// C35: waiting on a cell that has written nothing past the quiet time gives act. Falsifier: a cell kept waiting on
    /// a quiet peer, or freed before the quiet time (the default, the one the owner set in the offer, or <c>--quiet</c>).
    /// </summary>
    [Fact]
    public void C35_WaitingOnAQuietPeerGivesAct()
    {
        using var p = new Pair();
        // The owner's offer sets a quiet time of 3 minutes; a takes it (a claim on the same goal, citing the offer).
        var offer = p.Owner.Offer(p.A.Identity.Hardware.VesselId, Goal, T0, TimeSpan.FromHours(24), quiet: 3);
        Assert.True(offer.Verify());
        p.La.Append(p.A, new OfferPayload(offer), T0.AddMinutes(1));
        var taken = p.La.Append(p.A, new ClaimPayload(Goal) { Offer = offer.Id }, T0.AddMinutes(2)).Id;
        p.La.Append(p.A, TurnLedger.Wait(taken, p.B.Id, "waiting for the ui"), T0.AddMinutes(3));
        p.Sync();
        Assert.Equal(TimeSpan.FromMinutes(3), p.La.QuietOf(p.A.Id));
        // b last wrote at T0 + 2 s. Four minutes later it is quiet by the offer's time, not by a longer one given by hand.
        var now = T0.AddMinutes(4);
        var a = p.Both(p.A, now);
        Assert.Equal((Move.Act, "T4"), (a.Move, a.Rule));
        Assert.Contains("quiet", a.Turn.Reason);
        Assert.Contains(p.B.Id, a.Turn.Reason);
        Assert.Equal((Move.Wait, "T5"), p.Both(p.A, now, TimeSpan.FromMinutes(10)) is var byHand ? (byHand.Move, byHand.Rule) : default);

        // a moves on (its wait ends); b, on no offer, waits on a: the default quiet time of 10 minutes.
        p.La.Append(p.A, new ProgressPayload(taken, "the ui is up"), T0.AddMinutes(5));
        p.Lb.Append(p.B, TurnLedger.Wait(p.ClaimB, p.A.Id, "waiting for the api"), T0.AddMinutes(6));
        p.Sync();
        now = T0.AddMinutes(7);  // a last wrote at +5
        var soon = p.Both(p.B, now);
        Assert.Equal((Move.Wait, "T5"), (soon.Move, soon.Rule));
        Assert.Contains(p.A.Id, soon.Turn.Reason);
        now = T0.AddMinutes(16);
        Assert.Equal((Move.Act, "T4"), p.Both(p.B, now) is var late ? (late.Move, late.Rule) : default);
        Assert.Equal((Move.Wait, "T5"), p.Both(p.B, now, TimeSpan.FromMinutes(20)) is var patient ? (patient.Move, patient.Rule) : default);
        // Waiting on an entry waits on its writer the same way.
        p.La.Append(p.A, new ProgressPayload(taken, "the api is up"), T0.AddMinutes(17));
        p.Lb.Append(p.B, TurnLedger.Wait(p.ClaimB, p.ClaimA, "waiting for that claim to be released"), T0.AddMinutes(18));
        p.Sync();
        now = T0.AddMinutes(19);
        var onEntry = p.Both(p.B, now).Turn;
        Assert.Equal((Move.Wait, "T5", p.A.Id), (onEntry.Move, onEntry.Rule, onEntry.Waiting?.OnCell));
        Assert.Equal((Move.Act, "T4"), p.Both(p.B, T0.AddMinutes(29)) is var quietWriter ? (quietWriter.Move, quietWriter.Rule) : default);
    }

    /// <summary>
    /// C36: for random interleavings and sync orders of the same entries, <c>turn</c> gives the same result in every
    /// home once all hold the same entries. Falsifier: any home answering differently for any cell.
    /// </summary>
    [Fact]
    public void C36_TurnIsTheSameInEveryHomeWhateverTheOrderTheEntriesArrivedIn()
    {
        using var dir = new TempHome();
        var home = new CellHome(dir.Path);
        using var owner = home.EnsureOwner(out _);
        var cells = new[] { "a", "b", "c" }.Select(n => home.Endorse(home.OpenCell(new FakeVessel("raw-" + n, "creature-" + n), Position.InProcess(n), n))).ToList();
        string[] topics = ["plan", "api"];
        try
        {
            for (var seed = 0; seed < 24; seed++)
            {
                var random = new Random(seed);
                var count = 2 + seed % 2;
                var party = cells.Take(count).ToList();
                var own = party.ToDictionary(c => c.Id, _ => new WorkLedger(owner.PublicKey), StringComparer.Ordinal);
                var claims = new Dictionary<string, string>(StringComparer.Ordinal);
                var minute = 0;
                foreach (var cell in party)
                {
                    own[cell.Id].Append(cell, new HelloPayload(cell.Identity), T0.AddMinutes(minute++));
                    claims[cell.Id] = own[cell.Id].Append(cell, new ClaimPayload(Goal), T0.AddMinutes(minute++)).Id;
                }

                // Each step one cell writes something in its own home, with a clock that mostly runs forward, or syncs from one peer.
                for (var step = 0; step < 14; step++)
                {
                    var cell = party[random.Next(count)];
                    var ledger = own[cell.Id];
                    var other = party.Where(c => c != cell).ElementAt(random.Next(count - 1));
                    var at = T0.AddMinutes(minute++ + (random.Next(5) == 0 ? -random.Next(1, 4) : 0));
                    switch (random.Next(6))
                    {
                        case 0:
                            ledger.Append(cell, TurnLedger.Wait(claims[cell.Id], other.Id, "waiting"), at);
                            break;
                        case 1:
                            ledger.Append(cell, ledger.Propose(claims[cell.Id], topics[random.Next(topics.Length)], $"proposal {step}"), at);
                            break;
                        case 2:
                            var asked = ledger.Turn(cell.Id, at).Topics.FirstOrDefault(t => t.Live.Cell != cell.Id && t.Unanswered.Contains(cell.Id));
                            if (asked is not null) ledger.Append(cell, TurnLedger.Answer(claims[cell.Id], asked.Live.Id, random.Next(3) == 0 ? AnswerVerdict.Object : AnswerVerdict.Accept, "answer"), at);
                            else ledger.Append(cell, new ProgressPayload(claims[cell.Id], $"step {step}"), at);
                            break;
                        case 3:
                            ledger.Append(cell, new ProgressPayload(claims[cell.Id], $"step {step}"), at);
                            break;
                        default:
                            Assert.Empty(ledger.Merge(own[other.Id].Entries).Rejected);
                            break;
                    }
                }

                // Every entry anyone wrote, delivered to fresh homes in shuffled orders and chunks, and to the writers' own homes.
                var all = own.Values.SelectMany(l => l.Entries).Distinct().ToList();
                var now = T0.AddMinutes(minute + 2);
                var homes = new List<WorkLedger>();
                for (var h = 0; h < 4; h++)
                {
                    var shuffled = all.OrderBy(_ => random.Next()).ToList();
                    var ledger = new WorkLedger(owner.PublicKey);
                    for (var i = 0; i < shuffled.Count;)
                    {
                        var chunk = random.Next(1, 5);
                        Assert.Empty(ledger.Merge(shuffled.Skip(i).Take(chunk)).Rejected);
                        i += chunk;
                    }
                    homes.Add(ledger);
                }
                foreach (var ledger in own.Values) Assert.Empty(ledger.Merge(all).Rejected);
                Assert.All(homes.Concat(own.Values), h => Assert.Equal(homes[0].Digest(), h.Digest()));

                foreach (var cell in party)
                {
                    var expected = homes[0].Turn(cell.Id, now).ToJson();
                    foreach (var h in homes.Skip(1).Concat(own.Values)) Assert.Equal(expected, h.Turn(cell.Id, now).ToJson());
                }
            }
        }
        finally
        {
            foreach (var cell in cells) cell.Dispose();
        }
    }

    /// <summary>
    /// C37: nothing else changes: the existing suite passes (run with it), an old ledger verifies and reads as before,
    /// and the hub stores the new entries. Falsifier: a progress entry without the new fields written differently (which
    /// would break every old signature), an old ledger reading as anything but "act", or the hub refusing an entry.
    /// </summary>
    [Fact]
    public async Task C37_AnOldLedgerReadsAsBeforeAndTheHubStoresTheNewEntries()
    {
        // A progress entry without the new fields is written exactly as before: every old signature still verifies.
        Assert.Equal("""{"claim":"cell-x#1","row":"golden diff empty","stress":0.2}""", CellJson.Canonical(CellJson.ToElement(new ProgressPayload("cell-x#1", "golden diff empty") { Stress = 0.2 })));

        // A ledger with none of the new fields: saved and loaded as before, no waits, no topics, every cell free to act.
        using var p = new Pair();
        p.La.Append(p.A, new ProgressPayload(p.ClaimA, "turn 1: Bash x2"), T0.AddMinutes(1));
        p.La.Append(p.A, new HeartbeatPayload(0.3), T0.AddMinutes(2));
        p.Lb.Append(p.B, new ProgressPayload(p.ClaimB, "turn 1: Read x3") { Blocked = true, Stress = 0.4 }, T0.AddMinutes(3));
        p.Sync();
        var store = new JsonLedgerStore(Path.Combine(p.Dir.Path, "old-ledger.json"));
        store.Save(p.La);
        var loaded = new WorkLedger(p.Owner.PublicKey);
        Assert.Empty(store.LoadInto(loaded).Rejected);
        Assert.Equal(p.La.Digest(), loaded.Digest());
        Assert.Empty(loaded.StandingWaits());
        var now = T0.AddMinutes(4);
        foreach (var cell in new[] { p.A, p.B })
        {
            var turn = loaded.Turn(cell.Id, now);
            Assert.Equal((Move.Act, "T7"), (turn.Move, turn.Rule));
            Assert.Empty(turn.Topics);
            Assert.Null(turn.Waiting);
        }
        var stream = ActivityStream.Document(loaded);
        Assert.DoesNotContain("creatura:waitingOn", stream);
        Assert.DoesNotContain("creatura:topic", stream);

        // The new entries, through a running hub, to a third home: stored, nothing refused, and the same turn there.
        p.La.Append(p.A, TurnLedger.Wait(p.ClaimA, p.B.Id, "waiting for a plan"), T0.AddMinutes(5));
        var plan = p.Lb.Append(p.B, p.Lb.Propose(p.ClaimB, "plan", "b: the server in repo-b, the ui in repo-a"), T0.AddMinutes(6));
        p.Sync();
        p.La.Append(p.A, TurnLedger.Answer(p.ClaimA, plan.Id, AnswerVerdict.Accept, "yes"), T0.AddMinutes(7));
        await using var hub = await RunningHub.StartAsync(Path.Combine(p.Dir.Path, "hub.db"), p.Owner.PublicKey);
        var nodeA = new LedgerSyncNode(p.La, p.A.Id, LedgerHub.Http(hub.Url, p.A));
        var nodeB = new LedgerSyncNode(p.Lb, p.B.Id, LedgerHub.Http(hub.Url, p.B));
        foreach (var node in new[] { nodeA, nodeB, nodeA, nodeB })
        {
            var report = await node.SyncAsync(CancellationToken.None);
            Assert.True(report.Pushed, report.Message);
            Assert.Empty(report.Pulled.Rejected);
        }
        Assert.Equal(p.La.Digest(), p.Lb.Digest());
        Assert.Equal(0, hub.Store.Counts.Rejected);
        Assert.Equal(p.La.Count, hub.Store.Counts.Stored);

        var third = new WorkLedger(p.Owner.PublicKey);
        var observer = new LedgerSyncNode(third, "observer", LedgerHub.Http(hub.Url, p.A));
        Assert.Empty((await observer.SyncAsync(CancellationToken.None)).Pulled.Rejected);
        Assert.Equal(p.La.Digest(), third.Digest());
        now = T0.AddMinutes(8);
        foreach (var cell in new[] { p.A, p.B })
        {
            Assert.Equal(p.La.Turn(cell.Id, now).ToJson(), third.Turn(cell.Id, now).ToJson());
        }
        var settled = Assert.Single(third.Turn(p.A.Id, now).Topics);
        Assert.True(settled.Settled);
        Assert.Contains("creatura:topic", ActivityStream.Document(third));
        Assert.Equal(ActivityStream.Document(p.La), ActivityStream.Document(third));
    }

    /// <summary>
    /// The commands of slice T0: <c>wait</c>, <c>propose</c> (with a file), <c>answer</c> and <c>turn</c> with its text,
    /// its JSON and its exit codes, on two cells of one home. Not a pre-registered check; what the fake pair of T1 will call.
    /// </summary>
    [Fact]
    public async Task TheCommandsWaitProposeAnswerAndTurn()
    {
        using var dir = new TempHome();
        var vessel = new FakeVessel("raw-shared", "creature");
        (int Code, string Text) Run(string creatura, params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", dir.Path, "--no-mesh", "--creatura", creatura], output, output, vessel);
            return (code, output.ToString());
        }
        string Ok(string creatura, params string[] args)
        {
            var (code, text) = Run(creatura, args);
            Assert.True(code == 0, $"{string.Join(' ', args)} exited {code}: {text}");
            return text;
        }
        string IdIn(string text) => text.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];

        Ok("a", "cell", "init");
        Ok("b", "cell", "init");
        Ok("a", "cell", "claim", "--goal", Goal);
        Ok("b", "cell", "claim", "--goal", Goal);
        var home = new CellHome(dir.Path);
        using var owner = home.LoadOwner()!;
        WorkLedger Ledger()
        {
            var ledger = new WorkLedger(owner.PublicKey);
            home.Ledger.LoadInto(ledger);
            return ledger;
        }
        var cells = Ledger().Entries.Where(e => e.Kind == EntryKind.Hello).Select(e => e.Cell).Distinct().ToList();
        Assert.Equal(2, cells.Count);
        var (cellA, cellB) = (Ledger().Entries.First(e => e.Kind == EntryKind.Claim).Cell, Ledger().Entries.Last(e => e.Kind == EntryKind.Claim).Cell);

        // Free to act, with the exit code 0.
        Assert.Equal(0, Run("a", "cell", "turn").Code);
        Assert.StartsWith("act", Run("a", "cell", "turn").Text);

        // Both wait: one mover, the other told to wait with exit code 3, and --json says which rule.
        Ok("a", "cell", "wait", "--on", cellB, "--row", "waiting for a plan");
        Assert.Contains("a cell cannot wait on itself", Run("b", "cell", "wait", "--on", cellB, "--row", "oops").Text);
        Ok("b", "cell", "wait", "--on", cellA, "--row", "waiting for a plan");
        var ta = Run("a", "cell", "turn", "--json");
        var tb = Run("b", "cell", "turn", "--json");
        Assert.Equal(new[] { 0, 3 }.Order(), new[] { ta.Code, tb.Code }.Order());
        var mover = ta.Code == 0 ? ta : tb;
        var json = JsonNode.Parse(mover.Text)!.AsObject();
        Assert.Equal(("act", "T3"), ((string?)json["move"], (string?)json["rule"]));
        Assert.Contains("deadlock", (string?)json["reason"]);
        Assert.Equal(cellB == ((string?)json["waiting"]!["cell"]) ? cellA : cellB, mover == ta ? cellA : cellB);

        // The mover proposes with a file, shared through a hub; the other must answer it (exit 0), the proposer waits (exit 3).
        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        var (who, other) = mover == ta ? ("a", "b") : ("b", "a");
        var spec = Path.Combine(dir.Path, "api.md");
        File.WriteAllText(spec, "# The api\nGET /api/items\n");
        var proposed = Ok(who, "cell", "propose", "--topic", "api", "--file", spec, "--hub", hub.Url.ToString());
        var proposal = IdIn(proposed);
        Assert.Contains("api.md", proposed);
        Assert.Contains("sealed for 2 cells", proposed);
        var entry = Ledger().Entries.Single(e => e.Id == proposal).Body<ProgressPayload>();
        Assert.Equal("api", entry.Topic);
        Assert.NotNull(entry.Artifact);
        Assert.Contains("api.md", Ok(other, "cell", "files"));
        Assert.Equal((3, 0), (Run(who, "cell", "turn").Code, Run(other, "cell", "turn").Code));
        Assert.Contains($"answer {proposal}", Run(other, "cell", "turn").Text);
        Assert.Contains("[proposal on api, saw 0, file ", Ok(other, "cell", "ledger"));

        // Answering: an objection needs a reason, a proposer cannot answer itself; an acceptance settles the topic.
        Assert.Contains("--object takes --row", Run(other, "cell", "answer", "--proposal", proposal, "--object").Text);
        Assert.Contains("not by its proposer", Run(who, "cell", "answer", "--proposal", proposal, "--accept").Text);
        Assert.Contains("is not a proposal", Run(other, "cell", "answer", "--proposal", Ledger().Entries.First(e => e.Kind == EntryKind.Claim).Id, "--accept").Text);
        Ok(other, "cell", "answer", "--proposal", proposal, "--accept");
        var afterwards = Run(who, "cell", "turn");
        Assert.Equal(0, afterwards.Code);
        Assert.Contains("api: " + proposal, afterwards.Text);
        Assert.Contains("settled", afterwards.Text);
        Assert.Contains("[accept " + proposal + "]", Ok(other, "cell", "ledger"));

        // --quiet is minutes for turn and a flag for sync, as before.
        Assert.Equal(0, Run(who, "cell", "turn", "--quiet", "30").Code);
        Assert.Contains("--quiet takes", Run(who, "cell", "turn", "--quiet", "0").Text);
        Assert.Contains("--quiet", CellCommand.Usage);
    }
}
