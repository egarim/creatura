using System.Globalization;
using System.Text;
using Creatura.Bench;

namespace Creatura.Scenarios;

/// <summary>One hypothesis's verdict, with the figures it was judged on.</summary>
public sealed record ConvoyVerdict(string Id, string Statement, HypothesisVerdict Verdict, string Evidence);

/// <summary>What the battery found: the measures per arm over all seeds, and a verdict for M1 to M7.</summary>
public sealed record ConvoyBatteryReport(int Seeds, IReadOnlyDictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>> Runs, IReadOnlyList<ConvoyVerdict> Verdicts, IReadOnlyList<string> Errors)
{
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
    public static async Task<ConvoyBatteryReport> RunAsync(int seeds, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>();
        var errors = new List<string>();
        foreach (var arm in Enum.GetValues<ConvoyArm>())
        {
            var measures = new List<ConvoyMeasures>();
            for (var seed = 0; seed < seeds; seed++)
            {
                var run = await ConvoyScenario.Create(arm, seed).RunAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                measures.Add(run.Measures);
                errors.AddRange(run.Errors.Select(e => $"{arm} seed {seed}: {e}"));
            }
            runs[arm] = measures;
            progress?.Invoke($"arm {arm}: {seeds} runs");
        }

        var same = new List<bool>();
        foreach (var arm in Enum.GetValues<ConvoyArm>()) same.Add(await ReplaysMatchAsync(arm, 0, cancellationToken).ConfigureAwait(false));
        return new ConvoyBatteryReport(seeds, runs, Score(runs, same.All(s => s)), errors);
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

    public static IReadOnlyList<ConvoyVerdict> Score(IReadOnlyDictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>> runs, bool replaysMatch)
    {
        int Sum(ConvoyArm arm, Func<ConvoyMeasures, int> f) => runs[arm].Sum(f);
        HypothesisVerdict Held(bool held) => held ? HypothesisVerdict.Held : HypothesisVerdict.Failed;
        var withLayer = new[] { ConvoyArm.A, ConvoyArm.B, ConvoyArm.D, ConvoyArm.E };
        var eUnserved = runs[ConvoyArm.E].Count(m => !m.AllServed && m.IdleWhileUnserved > 0);
        var verdicts = new List<ConvoyVerdict>
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
        };

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
