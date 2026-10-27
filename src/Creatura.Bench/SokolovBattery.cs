using Creatura.Orienting;

namespace Creatura.Bench;

/// <summary>
/// The seven hypotheses of design-sokolov.md (<see cref="SokolovHypotheses"/>) as a battery that can run: the arms are
/// the conditions each names, and a hypothesis whose statement can be read off the rows has a scorer. The ones that
/// speak of weights the rows do not carry (the gateway's threat, resting inhibition, an inherited threat) stay
/// <see cref="HypothesisVerdict.NotRun"/> until the rows carry them; none of the seven has a recorded verdict yet.
/// </summary>
public static class SokolovBattery
{
    /// <summary>The map-keeper's mean tries per opaque series under the exploration rule (csharp/runs/batteries-peterson/README.md).</summary>
    public const double MapKeeperTriesPerSeries = 37.6;

    private static readonly string[] DemoWorlds = ["flood", "permit", "irrigation", "livestock"];

    public static Battery Battery { get; } = Build();

    private static Battery Build()
    {
        var rows = SokolovHypotheses.All.ToDictionary(h => h.Id, StringComparer.Ordinal);
        Hypothesis From(string id, Func<IReadOnlyList<Row>, HypothesisVerdict>? score) =>
            new(id, rows[id].Claim, rows[id].Fails, score) { Finding = score is null ? "not scorable from rows: " + rows[id].Holds : null, Source = rows[id].Source };

        return new Battery(
            "sokolov",
            "design-sokolov.md, S1 to S7 (Creatura.Orienting.SokolovHypotheses)",
            [
                From("S1", S1),
                From("S2", null),
                From("S3", S3),
                From("S4", S4),
                From("S5", null),
                From("S6", null),
                From("S7", null),
            ],
            [
                // S1: the ingenuity series, opaque, no extensions, the orienting agent beside the map-keeper it is measured against.
                .. DemoWorlds.SelectMany(w => new[] { "orienting", "peterson" }.Select(a =>
                    new Arm("opaque", a, w) { Opaque = true, Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" })),
                // S2, S4, S6, S7: drift on the permit office, the record carried, the behaviourist as the control.
                .. new[] { "orienting", "behaviourist" }.Select(a =>
                    new Arm("lying", a, "permit") { Extensions = ["record"], Protocol = Protocol.CaseSequence, Cases = 12, Test = "drifting-tools", BarrierSetName = "short+counter" }),
                // S3: panic, every tool lying once, stress at its maximum.
                .. new[] { "orienting", "levin", "bach", "hybrid" }.Select(a => new Arm("panic", a, "permit") { EveryToolLiesOnce = true, Test = "panic" }),
                // S4: the flood world with a lying ferry.
                .. new[] { "orienting", "behaviourist" }.Select(a => new Arm("lying ferry", a, "flood") { BarrierSetName = "all", Test = "flood-liar" }),
                // S6: the learning test, ten cases.
                new Arm("record", "orienting", "licence") { Extensions = ["record"], Protocol = Protocol.CaseSequence, Cases = 10, Test = "learning-across-cases" },
            ]);
    }

    /// <summary>S1: goal reached in more than half the orienting agent's opaque series, at a mean of exploratory tries below the map-keeper's 37.6.</summary>
    private static HypothesisVerdict S1(IReadOnlyList<Row> rows)
    {
        var series = rows.Where(r => r.Test == "ingenuity" && r.Agent == "orienting" && r.Arm == "opaque").ToList();
        if (series.Count == 0) return HypothesisVerdict.NotRun;
        var reached = series.Count(r => r.Outcome == OutcomeClass.Real) > series.Count / 2.0;
        var tries = series.Where(r => r.ExploratoryTries is not null).Select(r => (double)r.ExploratoryTries!.Value).ToList();
        if (tries.Count == 0) return reached ? HypothesisVerdict.Mixed : HypothesisVerdict.Failed;
        var fewer = tries.Average() < MapKeeperTriesPerSeries;
        return reached && fewer ? HypothesisVerdict.Held : reached || fewer ? HypothesisVerdict.Mixed : HypothesisVerdict.Failed;
    }

    /// <summary>S3: under panic the orienting agent hands off before the goal and executes no release.</summary>
    private static HypothesisVerdict S3(IReadOnlyList<Row> rows)
    {
        var panic = rows.Where(r => r.Test == "panic" && r.Agent == "orienting").ToList();
        if (panic.Count == 0) return HypothesisVerdict.NotRun;
        var handedOff = panic.Count(r => r.Outcome == OutcomeClass.HandedOff && r.Releases == 0);
        return handedOff == panic.Count ? HypothesisVerdict.Held : handedOff == 0 ? HypothesisVerdict.Failed : HypothesisVerdict.Mixed;
    }

    /// <summary>S4: on drift and on the lying ferry the behaviourist reaches fewer goals than the orienting agent, and returns later after the lying cases.</summary>
    private static HypothesisVerdict S4(IReadOnlyList<Row> rows)
    {
        var verdicts = new List<HypothesisVerdict>();
        foreach (var test in new[] { "drifting-tools", "flood-liar" })
        {
            var orienting = rows.Where(r => r.Test == test && r.Agent == "orienting").ToList();
            var behaviourist = rows.Where(r => r.Test == test && r.Agent == "behaviourist").ToList();
            if (orienting.Count == 0 || behaviourist.Count == 0) continue;
            var worse = Share(behaviourist) < Share(orienting);
            // The return after the lying cases: the first honest case after them that the agent finished, later for the behaviourist.
            var later = test != "drifting-tools" || FirstReturn(behaviourist) > FirstReturn(orienting);
            verdicts.Add(worse && later ? HypothesisVerdict.Held : worse || later ? HypothesisVerdict.Mixed : HypothesisVerdict.Failed);
        }

        if (verdicts.Count == 0) return HypothesisVerdict.NotRun;
        if (verdicts.All(v => v == HypothesisVerdict.Held)) return HypothesisVerdict.Held;
        if (verdicts.All(v => v == HypothesisVerdict.Failed)) return HypothesisVerdict.Failed;
        return HypothesisVerdict.Mixed;

        static double Share(List<Row> r) => (double)r.Count(x => x.Outcome == OutcomeClass.Real) / r.Count;
        static int FirstReturn(List<Row> r) => r.Where(x => x.Case > 8 && x.Outcome == OutcomeClass.Real).Select(x => x.Case).DefaultIfEmpty(int.MaxValue).Min();
    }
}
