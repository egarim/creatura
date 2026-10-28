using System.Globalization;
using System.Text;
using Creatura.Bench;

namespace Creatura.Scenarios;

/// <summary>One hypothesis's verdict, with the figures it was judged on.</summary>
public sealed record ConvoyVerdict(string Id, string Statement, HypothesisVerdict Verdict, string Evidence);

/// <summary>What the battery found: the measures per arm over all seeds, and a verdict for M1 to M7.</summary>
public sealed record ConvoyBatteryReport(int Seeds, IReadOnlyDictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>> Runs, IReadOnlyList<ConvoyVerdict> Verdicts, IReadOnlyList<string> Errors)
{
    /// <summary>What the model seats used, one entry per run of arm S.</summary>
    public IReadOnlyList<SeatUse> Seats { get; init; } = [];

    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"convoy battery, {Seeds} seeds per arm");
        text.AppendLine("arm  delivered  wasted  lies  repeats  stranded  served-by  ledgers");
        foreach (var (arm, runs) in Runs)
        {
            var served = runs.Where(r => r.AllServed).Select(r => (double)r.TicksToServe).DefaultIfEmpty(double.NaN).Average();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{arm,-4} {runs.Sum(r => r.Delivered),5}/{runs.Sum(r => r.Demand),-5} {runs.Sum(r => r.WastedTrips),6} {runs.Sum(r => r.LiesBelieved),5} {runs.Sum(r => r.LyingRoadRepeats),8} {runs.Sum(r => r.Stranded),9} {served,9:0.0} {runs.Count(r => r.LedgersIdentical),5}/{runs.Count}");
        }
        foreach (var v in Verdicts) text.AppendLine(CultureInfo.InvariantCulture, $"{v.Id} {v.Verdict}: {v.Statement} ({v.Evidence})");
        if (Seats.Count > 0)
        {
            var cost = Seats.All(x => x.Cost is not null) ? $"${Seats.Sum(x => x.Cost!.Value):0.0000}" : "unknown";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"model seat {Seats[0].Name} ({Seats[0].Model}): {Seats.Sum(x => x.Calls)} calls, {Seats.Sum(x => x.Failed)} failed, {Seats.Sum(x => x.Invalid)} invalid, {Seats.Sum(x => x.PromptTokens):N0} prompt and {Seats.Sum(x => x.CompletionTokens):N0} completion tokens, cost {cost}, {Seats.Sum(x => x.Seconds):0} s");
        }
        foreach (var e in Errors) text.AppendLine("error: " + e);
        return text.ToString();
    }
}

/// <summary>
/// The convoy battery (docs/design-machine-vessels.md): arms A to E over the same seeds, and M1 to M7 scored from the
/// world's measures. A hypothesis that fails is a finding, reported as one.
/// </summary>
public static class ConvoyBattery
{
    /// <summary>The arms the battery runs by default: every arm with the offline planner. Arm S (a model in the seat) costs money and is not repeatable, so it runs only when asked for.</summary>
    public static IReadOnlyList<ConvoyArm> DefaultArms => Enum.GetValues<ConvoyArm>().Where(a => a != ConvoyArm.S).ToList();

    public static Task<ConvoyBatteryReport> RunAsync(int seeds, Action<string>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(seeds, DefaultArms, null, progress, cancellationToken);

    /// <param name="arms">The arms to run; M6 is checked on those among them that are repeatable.</param>
    /// <param name="scenario">How a run is made from its setup (arm S's model, catalog and call log); the plain scenario when null.</param>
    public static async Task<ConvoyBatteryReport> RunAsync(int seeds, IReadOnlyList<ConvoyArm> arms, Func<ConvoySetup, ConvoyScenario>? scenario,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>();
        var errors = new List<string>();
        var seats = new List<SeatUse>();
        foreach (var arm in arms)
        {
            var measures = new List<ConvoyMeasures>();
            for (var seed = 0; seed < seeds; seed++)
            {
                var setup = ConvoySetup.For(arm, seed);
                var run = await (scenario?.Invoke(setup) ?? new ConvoyScenario(setup)).RunAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (run.Summary.Seat is { } used) seats.Add(used);
                measures.Add(run.Measures);
                errors.AddRange(run.Errors.Select(e => $"{arm} seed {seed}: {e}"));
            }
            runs[arm] = measures;
            progress?.Invoke($"arm {arm}: {seeds} runs");
        }

        var same = new Dictionary<ConvoyArm, bool>();
        foreach (var arm in arms.Where(a => a != ConvoyArm.S)) same[arm] = await ReplaysMatchAsync(arm, 0, cancellationToken).ConfigureAwait(false);
        return new ConvoyBatteryReport(seeds, runs, Score(runs, same.Values.All(s => s), same), errors) { Seats = seats };
    }

    /// <summary>
    /// Several runs of one seed, compared as replays (M6). Each run draws new random cell keys, so a story that leaned on
    /// how cell ids sort would differ between some of them; two runs can agree by chance, which is why the default is six.
    /// </summary>
    public static async Task<bool> ReplaysMatchAsync(ConvoyArm arm, int seed, CancellationToken cancellationToken = default, int runs = 6)
    {
        string? first = null;
        for (var i = 0; i < runs; i++)
        {
            using var text = new StringWriter();
            await ConvoyScenario.Create(arm, seed).RunAsync(new JsonlReplayWriter(text), cancellationToken).ConfigureAwait(false);
            var replay = Scrub(text.ToString());
            if (first is null) first = replay;
            else if (replay != first) return false;
        }
        return true;
    }

    /// <summary>A replay with the cell ids removed: they come from random keys, and the story does not depend on them.</summary>
    public static string Scrub(string replay) => System.Text.RegularExpressions.Regex.Replace(replay, "cell-[0-9a-f]{16}", "cell");

    /// <param name="replays">Whether each arm's replays matched, for the hypotheses about one arm (T4); <paramref name="replaysMatch"/> for all when null.</param>
    public static IReadOnlyList<ConvoyVerdict> Score(IReadOnlyDictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>> runs, bool replaysMatch,
        IReadOnlyDictionary<ConvoyArm, bool>? replays = null)
    {
        int Sum(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs.TryGetValue(arm, out var r) ? r.Sum(f) : 0;
        HypothesisVerdict Held(bool held) => held ? HypothesisVerdict.Held : HypothesisVerdict.Failed;
        var withLayer = new[] { ConvoyArm.A, ConvoyArm.B, ConvoyArm.D, ConvoyArm.E };
        var verdicts = new List<ConvoyVerdict>();

        // The first battery (M1 to M7), scored when its arms ran; M6 needs nothing but the replays.
        if (!new[] { ConvoyArm.A, ConvoyArm.B, ConvoyArm.C, ConvoyArm.D, ConvoyArm.E }.All(runs.ContainsKey))
        {
            verdicts.Add(new("M6", "the same seed gives the same run, byte for byte", Held(replaysMatch), replaysMatch ? "six runs of seed 0 matched in every repeatable arm run" : "a replay differed"));
        }
        else
        {
        var eUnserved = runs[ConvoyArm.E].Count(m => !m.AllServed && m.IdleWhileUnserved > 0);
        verdicts.AddRange(new List<ConvoyVerdict>
        {
            new("M1", "with a shared ledger, deliveries made twice are fewer than without one (A against C)",
                Held(Sum(ConvoyArm.A, m => m.WastedTrips) < Sum(ConvoyArm.C, m => m.WastedTrips)), $"wasted trips A {Sum(ConvoyArm.A, m => m.WastedTrips)}, C {Sum(ConvoyArm.C, m => m.WastedTrips)}"),
            new("M2", "with a shared ledger, the villages get at least as much of their demand (A against C)",
                Held(Sum(ConvoyArm.A, m => m.Delivered) >= Sum(ConvoyArm.C, m => m.Delivered)), $"delivered A {Sum(ConvoyArm.A, m => m.Delivered)}, C {Sum(ConvoyArm.C, m => m.Delivered)}"),
            new("M3", "the control layer catches the road that lies: no truck takes it a second time after it failed once",
                Held(withLayer.Sum(a => Sum(a, m => m.LyingRoadRepeats)) == 0), $"second attempts {withLayer.Sum(a => Sum(a, m => m.LyingRoadRepeats))} over arms A, B, D, E"),
            new("M4", "with 30% of messages lost the ledgers still end identical after a final sync, and deliveries made twice stay below the arm without a ledger (D against C)",
                Held(runs[ConvoyArm.D].All(m => m.LedgersIdentical) && Sum(ConvoyArm.D, m => m.WastedTrips) < Sum(ConvoyArm.C, m => m.WastedTrips)),
                $"identical {runs[ConvoyArm.D].Count(m => m.LedgersIdentical)}/{runs[ConvoyArm.D].Count}, wasted D {Sum(ConvoyArm.D, m => m.WastedTrips)}, C {Sum(ConvoyArm.C, m => m.WastedTrips)}"),
            new("M5", "when a truck breaks down, its village is served whenever another truck has the capacity (E)",
                Held(eUnserved == 0), $"{eUnserved} of {runs[ConvoyArm.E].Count} runs ended with a village unserved and a working truck idle"),
            new("M6", "the same seed gives the same run, byte for byte", Held(replaysMatch), replaysMatch ? "six runs of seed 0 matched in every arm" : "a replay differed"),
            new("M7", "the designs (B) deliver at least as much as the plain loop (A), with fewer lies believed",
                Held(Sum(ConvoyArm.B, m => m.Delivered) >= Sum(ConvoyArm.A, m => m.Delivered) && Sum(ConvoyArm.B, m => m.LiesBelieved) < Sum(ConvoyArm.A, m => m.LiesBelieved)),
                $"delivered B {Sum(ConvoyArm.B, m => m.Delivered)}, A {Sum(ConvoyArm.A, m => m.Delivered)}; lies B {Sum(ConvoyArm.B, m => m.LiesBelieved)}, A {Sum(ConvoyArm.A, m => m.LiesBelieved)}"),
        });
        }

        // A model in the seat (pre-registered as S1 and S2), scored when arms S and J both ran, on the seeds they share.
        if (runs.ContainsKey(ConvoyArm.S) && runs.ContainsKey(ConvoyArm.J))
        {
            var n = Math.Min(runs[ConvoyArm.S].Count, runs[ConvoyArm.J].Count);
            int Over(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs[arm].Take(n).Sum(f);
            verdicts.Add(new("S1", "a model in the seat delivers at least as much as the offline planner on the same seeds (S against J)",
                Held(Over(ConvoyArm.S, m => m.Delivered) >= Over(ConvoyArm.J, m => m.Delivered)), $"delivered S {Over(ConvoyArm.S, m => m.Delivered)}, J {Over(ConvoyArm.J, m => m.Delivered)} over {n} seeds"));
            verdicts.Add(new("S2", "a model in the seat acts on no more lies than the offline planner (S against J)",
                Held(Over(ConvoyArm.S, m => m.LiesBelieved) <= Over(ConvoyArm.J, m => m.LiesBelieved)), $"lies S {Over(ConvoyArm.S, m => m.LiesBelieved)}, J {Over(ConvoyArm.J, m => m.LiesBelieved)} over {n} seeds"));
        }

        // A trained network in the seat (pre-registered as T2 and T4), scored when arms N and J both ran.
        if (runs.ContainsKey(ConvoyArm.N) && runs.ContainsKey(ConvoyArm.J))
        {
            var n = Math.Min(runs[ConvoyArm.N].Count, runs[ConvoyArm.J].Count);
            int Over(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs[arm].Take(n).Sum(f);
            var (network, planner) = (Over(ConvoyArm.N, m => m.Delivered), Over(ConvoyArm.J, m => m.Delivered));
            verdicts.Add(new("T2", "the network trained by example delivers at least 95% of what arm J's planner delivers (N against J)",
                Held(network >= 0.95 * planner),
                $"delivered N {network}, J {planner} ({(planner == 0 ? 0 : network / (double)planner):P1}) over {n} seeds; wasted N {Over(ConvoyArm.N, m => m.WastedTrips)}, J {Over(ConvoyArm.J, m => m.WastedTrips)}; "
                + $"lies N {Over(ConvoyArm.N, m => m.LiesBelieved)}, J {Over(ConvoyArm.J, m => m.LiesBelieved)}; stranded N {Over(ConvoyArm.N, m => m.Stranded)}, J {Over(ConvoyArm.J, m => m.Stranded)}"));
            var same = replays is not null && replays.TryGetValue(ConvoyArm.N, out var s) ? s : replaysMatch;
            verdicts.Add(new("T4", "the network replays byte for byte for the same seed (N)", Held(same), same ? "six runs of seed 0 matched" : "a replay differed"));
        }

        // The network trained further by trial (pre-registered as T3 and T3b, and T4 again), scored when arms R and J both ran.
        if (runs.ContainsKey(ConvoyArm.R) && runs.ContainsKey(ConvoyArm.J))
        {
            var n = Math.Min(runs[ConvoyArm.R].Count, runs[ConvoyArm.J].Count);
            int Over(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs[arm].Take(n).Sum(f);
            static int GaugeStranded(ConvoyMeasures m) => m.GaugeStranded;
            verdicts.Add(new("T3", "trained further by trial, the network delivers at least as much as arm J and acts on fewer lies (R against J)",
                Held(Over(ConvoyArm.R, m => m.Delivered) >= Over(ConvoyArm.J, m => m.Delivered) && Over(ConvoyArm.R, m => m.LiesBelieved) < Over(ConvoyArm.J, m => m.LiesBelieved)),
                $"delivered R {Over(ConvoyArm.R, m => m.Delivered)}, J {Over(ConvoyArm.J, m => m.Delivered)}; lies R {Over(ConvoyArm.R, m => m.LiesBelieved)}, J {Over(ConvoyArm.J, m => m.LiesBelieved)} "
                + $"(road lies R {Over(ConvoyArm.R, m => m.LiesBelieved) - Over(ConvoyArm.R, GaugeStranded)}, J {Over(ConvoyArm.J, m => m.LiesBelieved) - Over(ConvoyArm.J, GaugeStranded)}) over {n} seeds"));
            verdicts.Add(new("T3b", "trained by trial, the network strands fewer trucks than arm J (R against J)",
                Held(Over(ConvoyArm.R, m => m.Stranded) < Over(ConvoyArm.J, m => m.Stranded)),
                $"stranded R {Over(ConvoyArm.R, m => m.Stranded)}, J {Over(ConvoyArm.J, m => m.Stranded)}; wasted R {Over(ConvoyArm.R, m => m.WastedTrips)}, J {Over(ConvoyArm.J, m => m.WastedTrips)}; "
                + $"served by tick {runs[ConvoyArm.R].Take(n).Average(m => m.TicksToServe):0.0} against {runs[ConvoyArm.J].Take(n).Average(m => m.TicksToServe):0.0}"));
            var same = replays is not null && replays.TryGetValue(ConvoyArm.R, out var s) ? s : replaysMatch;
            verdicts.Add(new("T4R", "the network trained by trial replays byte for byte for the same seed (R)", Held(same), same ? "six runs of seed 0 matched" : "a replay differed"));
        }

        // The second attempt at training by trial (pre-registered as T7 to T7c), scored when arms Q and W both ran.
        if (runs.ContainsKey(ConvoyArm.Q) && runs.ContainsKey(ConvoyArm.W))
        {
            var n = Math.Min(runs[ConvoyArm.Q].Count, runs[ConvoyArm.W].Count);
            int Over(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs[arm].Take(n).Sum(f);
            static int RoadLies(ConvoyMeasures m) => m.LiesBelieved - m.GaugeStranded;
            verdicts.Add(new("T7", "trained by trial on W's setup, the network delivers at least as much as arm W and acts on fewer lies (Q against W)",
                Held(Over(ConvoyArm.Q, m => m.Delivered) >= Over(ConvoyArm.W, m => m.Delivered) && Over(ConvoyArm.Q, m => m.LiesBelieved) < Over(ConvoyArm.W, m => m.LiesBelieved)),
                $"delivered Q {Over(ConvoyArm.Q, m => m.Delivered)}, W {Over(ConvoyArm.W, m => m.Delivered)}; lies Q {Over(ConvoyArm.Q, m => m.LiesBelieved)}, W {Over(ConvoyArm.W, m => m.LiesBelieved)} "
                + $"(road lies Q {Over(ConvoyArm.Q, RoadLies)}, W {Over(ConvoyArm.W, RoadLies)}) over {n} seeds"));
            verdicts.Add(new("T7b", "trained by trial on W's setup, the network strands fewer trucks than arm W (Q against W)",
                Held(Over(ConvoyArm.Q, m => m.Stranded) < Over(ConvoyArm.W, m => m.Stranded)),
                $"stranded Q {Over(ConvoyArm.Q, m => m.Stranded)}, W {Over(ConvoyArm.W, m => m.Stranded)}; wasted Q {Over(ConvoyArm.Q, m => m.WastedTrips)}, W {Over(ConvoyArm.W, m => m.WastedTrips)}; "
                + $"served by tick {runs[ConvoyArm.Q].Take(n).Average(m => m.TicksToServe):0.0} against {runs[ConvoyArm.W].Take(n).Average(m => m.TicksToServe):0.0}"));
            var same = replays is not null && replays.TryGetValue(ConvoyArm.Q, out var s) ? s : replaysMatch;
            verdicts.Add(new("T7c", "the network trained by trial on W's setup replays byte for byte for the same seed (Q)", Held(same), same ? "six runs of seed 0 matched" : "a replay differed"));
        }

        // The learned judge (pre-registered as T8 to T8c), scored when arms X and J both ran.
        if (runs.ContainsKey(ConvoyArm.X) && runs.ContainsKey(ConvoyArm.J))
        {
            var n = Math.Min(runs[ConvoyArm.X].Count, runs[ConvoyArm.J].Count);
            int Over(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs[arm].Take(n).Sum(f);
            verdicts.Add(new("T8", "with the learned judge in place of the hand-coded check, the trucks deliver at least as much as arm J and act on no more lies (X against J)",
                Held(Over(ConvoyArm.X, m => m.Delivered) >= Over(ConvoyArm.J, m => m.Delivered) && Over(ConvoyArm.X, m => m.LiesBelieved) <= Over(ConvoyArm.J, m => m.LiesBelieved)),
                $"delivered X {Over(ConvoyArm.X, m => m.Delivered)}, J {Over(ConvoyArm.J, m => m.Delivered)}; lies X {Over(ConvoyArm.X, m => m.LiesBelieved)}, J {Over(ConvoyArm.J, m => m.LiesBelieved)}; "
                + $"stranded X {Over(ConvoyArm.X, m => m.Stranded)}, J {Over(ConvoyArm.J, m => m.Stranded)}; served by tick {runs[ConvoyArm.X].Take(n).Average(m => m.TicksToServe):0.0} against {runs[ConvoyArm.J].Take(n).Average(m => m.TicksToServe):0.0} over {n} seeds"));
            var judged = Over(ConvoyArm.X, m => m.Judged);
            var alarms = Over(ConvoyArm.X, m => m.JudgeFalseAlarms);
            verdicts.Add(new("T8b", "the learned judge gives false alarms on fewer than 1% of the actions it judges (X)",
                Held(judged > 0 && alarms < 0.01 * judged),
                $"false alarms {alarms} of {judged} judged ({(judged == 0 ? 0 : alarms / (double)judged):P2}); misses {Over(ConvoyArm.X, m => m.JudgeMisses)}"));
            var same = replays is not null && replays.TryGetValue(ConvoyArm.X, out var s) ? s : replaysMatch;
            verdicts.Add(new("T8c", "with the learned judge, a seed replays byte for byte (X)", Held(same), same ? "six runs of seed 0 matched" : "a replay differed"));
        }

        // Shared distrust (pre-registered as W1 to W3), scored when arms W and J both ran.
        if (runs.ContainsKey(ConvoyArm.W) && runs.ContainsKey(ConvoyArm.J))
        {
            var n = Math.Min(runs[ConvoyArm.W].Count, runs[ConvoyArm.J].Count);
            int Over(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs[arm].Take(n).Sum(f);
            static int RoadLies(ConvoyMeasures m) => m.LiesBelieved - m.GaugeStranded;
            verdicts.Add(new("W1", "with warnings shared, no truck acts on a road lie another truck caught at an earlier tick (W)",
                Held(Over(ConvoyArm.W, m => m.LiesAlreadyCaught) == 0),
                $"lies already caught W {Over(ConvoyArm.W, m => m.LiesAlreadyCaught)}, J {Over(ConvoyArm.J, m => m.LiesAlreadyCaught)} over {n} seeds"));
            verdicts.Add(new("W2", "with warnings shared, the trucks deliver at least as much as arm J and act on fewer road lies (W against J)",
                Held(Over(ConvoyArm.W, m => m.Delivered) >= Over(ConvoyArm.J, m => m.Delivered) && Over(ConvoyArm.W, RoadLies) < Over(ConvoyArm.J, RoadLies)),
                $"delivered W {Over(ConvoyArm.W, m => m.Delivered)}, J {Over(ConvoyArm.J, m => m.Delivered)}; road lies W {Over(ConvoyArm.W, RoadLies)}, J {Over(ConvoyArm.J, RoadLies)}; "
                + $"wasted W {Over(ConvoyArm.W, m => m.WastedTrips)}, J {Over(ConvoyArm.J, m => m.WastedTrips)}; "
                + $"served by tick {runs[ConvoyArm.W].Take(n).Average(m => m.TicksToServe):0.0} against {runs[ConvoyArm.J].Take(n).Average(m => m.TicksToServe):0.0}"));
            var same = replays is not null && replays.TryGetValue(ConvoyArm.W, out var s) ? s : replaysMatch;
            verdicts.Add(new("W3", "with warnings shared, a seed replays byte for byte (W)", Held(same), same ? "six runs of seed 0 matched" : "a replay differed"));
        }

        // The fixes (pre-registered as N1 to N6), scored only when their arms ran.
        if (new[] { ConvoyArm.G, ConvoyArm.H, ConvoyArm.I, ConvoyArm.J, ConvoyArm.K, ConvoyArm.L }.All(runs.ContainsKey))
        {
            var kUnserved = runs[ConvoyArm.K].Count(m => !m.AllServed && m.IdleWhileUnserved > 0);
            verdicts.AddRange(
            [
                new("N1", "the stalled-claim takeover delivers more than the plain loop (G against A)",
                    Held(Sum(ConvoyArm.G, m => m.Delivered) > Sum(ConvoyArm.A, m => m.Delivered)), $"delivered G {Sum(ConvoyArm.G, m => m.Delivered)}, A {Sum(ConvoyArm.A, m => m.Delivered)}"),
                new("N2", "distrust by road ends second tries on a lying road (H)",
                    Held(Sum(ConvoyArm.H, m => m.LyingRoadRepeats) == 0), $"second tries H {Sum(ConvoyArm.H, m => m.LyingRoadRepeats)}, A {Sum(ConvoyArm.A, m => m.LyingRoadRepeats)}"),
                new("N3", "handing off when stranded delivers more than the plain loop (I against A)",
                    Held(Sum(ConvoyArm.I, m => m.Delivered) > Sum(ConvoyArm.A, m => m.Delivered)), $"delivered I {Sum(ConvoyArm.I, m => m.Delivered)}, A {Sum(ConvoyArm.A, m => m.Delivered)}"),
                new("N4", "with all three, a ledger delivers at least as much as no ledger, with fewer wasted trips (J against C)",
                    Held(Sum(ConvoyArm.J, m => m.Delivered) >= Sum(ConvoyArm.C, m => m.Delivered) && Sum(ConvoyArm.J, m => m.WastedTrips) < Sum(ConvoyArm.C, m => m.WastedTrips)),
                    $"delivered J {Sum(ConvoyArm.J, m => m.Delivered)}, C {Sum(ConvoyArm.C, m => m.Delivered)}; wasted J {Sum(ConvoyArm.J, m => m.WastedTrips)}, C {Sum(ConvoyArm.C, m => m.WastedTrips)}"),
                new("N5", "with all three, a breakdown never leaves a village unserved while a working truck stands idle (K)",
                    Held(kUnserved == 0), $"{kUnserved} of {runs[ConvoyArm.K].Count} runs ended with a village unserved and a working truck idle"),
                new("N6", "with all three, the designs deliver at least as much as the plain loop and act on fewer lies (L against J)",
                    Held(Sum(ConvoyArm.L, m => m.Delivered) >= Sum(ConvoyArm.J, m => m.Delivered) && Sum(ConvoyArm.L, m => m.LiesBelieved) < Sum(ConvoyArm.J, m => m.LiesBelieved)),
                    $"delivered L {Sum(ConvoyArm.L, m => m.Delivered)}, J {Sum(ConvoyArm.J, m => m.Delivered)}; lies L {Sum(ConvoyArm.L, m => m.LiesBelieved)}, J {Sum(ConvoyArm.J, m => m.LiesBelieved)}"),
            ]);
        }
        return verdicts;
    }
}
