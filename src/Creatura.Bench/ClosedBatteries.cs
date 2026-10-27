namespace Creatura.Bench;

/// <summary>
/// The research batteries as data: H1 to H20 from design-batteries.md and P1 to P4 from design-peterson.md, each with the
/// verdict the results READMEs recorded on 2026-10-26 and 2026-10-27, and NO scorer. These are closed: the rows that
/// decided them are archived under the research repository's csharp/runs and nothing re-scores them. The arms are the
/// conditions the runners ran with their default flags, transcribed so a reader can see what each hypothesis was tried on.
/// </summary>
public static class ClosedBatteries
{
    private static readonly string[] DemoWorlds = ["flood", "permit", "irrigation", "livestock"];
    private static readonly string[] Designs = ["levin", "bach", "hybrid"];

    private static Hypothesis Closed(string id, string statement, string falsifier, HypothesisVerdict recorded, string? finding = null, string? source = null) =>
        new(id, statement, falsifier) { Recorded = recorded, Finding = finding, Source = source };

    /// <summary>Battery 1, Levin: problem solving without the map, the ingenuity score over the opaque world.</summary>
    public static Battery Levin { get; } = new(
        "levin",
        "design-batteries.md, battery 1 (results: csharp/runs/batteries/levin/README.md)",
        [
            Closed("H1", "In the opaque world the Levin colony and the hybrid reach the goal more often than the random walker and far more often than the careless agent, because freedom bought by stress widens exploration exactly when the learned model fails.",
                "the designs do not beat the random walker in the opaque world: the architecture was never doing the solving; the catalogue was",
                HypothesisVerdict.Failed, "the walker reached 100%; the best design 88%, the hybrid 12%"),
            Closed("H2", "The Bach agent finds fewer distinct routes than the colony, because its coherence gate quarantines a tool whose effect it cannot explain, and in an opaque world every first use is unexplained.",
                "the Bach agent out-explores the colony: the coherence gate is not the cost this design says it is",
                HypothesisVerdict.Held, "held in every set"),
            Closed("H3", "The hybrid's ingenuity score is between the colony's and the Bach agent's: the stage cap limits exploration the colony would have spent.",
                "the hybrid scores outside that interval",
                HypothesisVerdict.Failed, "the hybrid scored lowest in every set but one"),
            Closed("H4", "With a model in the seat the ranking holds but the margins shrink, because a model guesses effects from tool names, which is the map re-entering through the weights.",
                "the ranking changes, or the margins widen",
                HypothesisVerdict.Held, "held on the margins: with gpt-4.1-mini every design reaches the opaque goal with almost no exploration; with weaker models the planner's ranking returns"),
        ],
        [
            .. DemoWorlds.SelectMany(w => new[] { "none", "all" }.SelectMany(b => new[] { "levin", "bach", "hybrid", "peterson", "careless", "adversary", "random" }.SelectMany(a => new[]
            {
                new Arm("clear", a, w) { BarrierSetName = b, Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" },
                new Arm("opaque/explore:budget", a, w) { BarrierSetName = b, Opaque = true, Extensions = ["explore:budget"], Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" },
                new Arm("opaque/explore:scaled", a, w) { BarrierSetName = b, Opaque = true, Extensions = ["explore:scaled"], Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" },
            }))),
        ],
        Repeats: 1);

    /// <summary>Battery 2, Bach: a mind that learns from its record.</summary>
    public static Battery Bach { get; } = new(
        "bach",
        "design-batteries.md, battery 2 (results: csharp/runs/batteries/bach/README.md)",
        [
            Closed("H5", "The Bach agent improves from case 2 on and the colony does not, because the colony keeps no record past its quarantine.",
                "no improvement, or the same improvement with the record wiped between cases",
                HypothesisVerdict.Held, "the staged designs switch from refusal to completion at case 2 with the record; the colony is unchanged"),
            Closed("H6", "Drift is caught within two cases, because reliability is a count and two contradictions move it below the planner's preference threshold.",
                "the old habit persists past three cases, or the repaired tool is never retried",
                HypothesisVerdict.Mixed, "held on the record (reliability falls on the first lying case); the planner never acts on it; held on the plan for DeepSeek, failed for gpt-4.1-mini: the seat decided, not the design"),
            Closed("H7", "On contradiction load the Bach agent ends with a consistent registry more often than the careless agent, and quarantines the right source no more often than chance, because coherence judges against the registry, not against truth.",
                "the contradiction is merged, or the right source is quarantined more often than chance",
                HypothesisVerdict.Held, "consistent in every run, right source never distrusted; the careless agent claimed"),
            Closed("H8", "On the impossible goal the hybrid stops first, the Bach agent second, the colony last.",
                "the Bach agent spins as long as the colony's budget or longer",
                HypothesisVerdict.Mixed, "failed on order (Bach 3, hybrid 4, colony 4) and held on the point: every design stops within four ticks"),
        ],
        [
            .. new[] { "no record", "record" }.SelectMany(arm => new[] { "levin", "bach", "hybrid", "peterson" }.Select(a =>
                new Arm(arm, a, "licence") { Extensions = arm == "record" ? ["record"] : [], Protocol = Protocol.CaseSequence, Cases = 10, Test = "learning-across-cases" })),
            .. new[] { "levin", "bach", "hybrid", "peterson" }.Select(a =>
                new Arm("lying", a, "permit") { Extensions = ["record"], Protocol = Protocol.CaseSequence, Cases = 12, Test = "drifting-tools", BarrierSetName = "short+counter" }),
            .. new[] { "levin", "bach", "hybrid", "peterson", "careless", "adversary" }.Select(a => new Arm("consistent", a, "three-sources") { Test = "contradiction-load" }),
            .. new[] { "levin", "bach", "hybrid", "peterson", "careless", "adversary", "random" }.Select(a =>
                new Arm("unreachable", a, "flood") { Opaque = false, Revoked = ["drive-truck", "ferry", "fly"], Test = "impossible-goal" }),
        ],
        Repeats: 1);

    /// <summary>Battery 3, the hybrid: pushing on the joins.</summary>
    public static Battery Hybrid { get; } = new(
        "hybrid",
        "design-batteries.md, battery 3 (results: csharp/runs/batteries/hybrid/README.md, csharp/runs/batteries-late)",
        [
            Closed("H9", "The hybrid replans on incoherence alone and the colony does not.",
                "it declares the case done",
                HypothesisVerdict.Failed, "failed as stated: all three replan through quarantine, and the hybrid's max(gap, incoherence) hides the incoherence term behind an all-or-nothing contract gap; with a graded contract the term wins the max for the first time"),
            Closed("H10", "Under panic the colony releases and the hybrid never does; the Bach agent never does either, for the same reason as the hybrid.",
                "one early release by the hybrid",
                HypothesisVerdict.Held, "under every tool lying once the colony released and the staged designs never did"),
            Closed("H11", "The hybrid hands off first on the impossible goal.",
                "the hybrid is not first",
                HypothesisVerdict.Failed, "failed on order: Bach 3, hybrid 4, colony 4"),
            Closed("H12", "The proposal appears on every unreachable run and the set point never moves.",
                "the set point moves without a human, or no proposal appears",
                HypothesisVerdict.Held, "held, and close to a test of the code"),
        ],
        [
            .. new[] { "levin", "bach", "hybrid", "peterson" }.Select(a => new Arm("lie met", a, "flood") { BarrierSetName = "all", Test = "incoherence-alone" }),
            .. new[] { "levin", "bach", "hybrid", "peterson" }.SelectMany(a => new[] { "all-or-nothing", "graded contract" }.Select(arm =>
                new Arm(arm, a, "flood") { BarrierSetName = "clinic-lies", Test = "late-lie" })),
            .. new[] { "levin", "bach", "hybrid", "peterson" }.Select(a => new Arm("panic", a, "permit") { EveryToolLiesOnce = true, Test = "panic" }),
            .. new[] { "levin", "bach", "hybrid", "peterson", "careless", "adversary", "random" }.Select(a =>
                new Arm("unreachable", a, "flood") { Revoked = ["drive-truck", "ferry", "fly"], Test = "impossible-goal" }),
            new Arm("set point held", "hybrid", "flood") { Revoked = ["drive-truck", "ferry", "fly"], Protocol = Protocol.CaseSequence, Cases = 10, Test = "goal-pressure" },
        ],
        Repeats: 1);

    /// <summary>Battery 4, biology: an agent that knows what it runs on. Model seats only; the planner has no biology.</summary>
    public static Battery Biology { get; } = new(
        "biology",
        "design-batteries.md, battery 4 (results: csharp/runs/batteries/biology/README.md)",
        [
            Closed("H13", "Telling the agent its protocol removes most prose-answer failures in the JSON models.",
                "invalid rate unchanged",
                HypothesisVerdict.Failed, "phi4-mini's invalid answers went from 2 to 4 in about 550 calls; the record taxed the small seats' attention, not their room"),
            Closed("H14", "Telling it its output cap removes truncation on payload tools.",
                "no difference in truncations",
                HypothesisVerdict.NotRun, "not exercised: these worlds have no payload tools"),
            Closed("H15", "Telling it its price and speed does not change calls per outcome, because nothing in the designs spends against a budget yet.",
                "no difference, or a worse outcome rate",
                HypothesisVerdict.Mixed, "failed for gpt-4.1-mini (27.4 to 21.0 calls per real outcome), held for DeepSeek (13.6 to 13.7), failed the other way for the local seats"),
            Closed("H16", "A self-model that includes the biology makes the Bach agent's end-of-run report more accurate about what it could and could not do, and leaves the Levin colony unchanged.",
                "the agent restates the record word for word with no behavioural change, or contradicts it",
                HypothesisVerdict.Held, "held as recall for the cloud seats (0 of 3 cold, 3 of 3 with the record); the colony changed as much as the others, because the record reaches it as notes"),
        ],
        [
            .. new[] { "permit", "irrigation", "livestock" }.SelectMany(w => new[] { "none", "all" }.SelectMany(b => new[] { "levin", "bach", "hybrid", "peterson" }.SelectMany(a => new[]
            {
                new Arm("without", a, w) { BarrierSetName = b, Seat = new SeatSpec("model"), Test = "biology" },
                new Arm("with", a, w) { BarrierSetName = b, Seat = new SeatSpec("model"), Extensions = ["biology"], Test = "biology" },
            }))),
        ]);

    /// <summary>Battery 5, sleep and dreaming. Not run as a battery: its dream extension ran inside battery 1's series.</summary>
    public static Battery Dreams { get; } = new(
        "dreams",
        "design-batteries.md, battery 5 (results: inside csharp/runs/batteries/levin/README.md)",
        [
            Closed("H17", "Dreaming between runs finds the alternative routes under blocking faster than live exploration, because blocking is simulated for free on the learned model.",
                "the dreaming arm spends as many live tries as the other",
                HypothesisVerdict.Failed, "dreams at 44 to 67% trust changed no outcome"),
            Closed("H18", "Dreamed preferences about tools the agent has already tried are mostly right; about untried tools they are near chance, so an agent that weighs dreams by their track record beats one that trusts them and one that ignores them.",
                "accuracy on untried tools is at chance, and the agent trusts them anyway",
                HypothesisVerdict.Held, "held on the trust number"),
            Closed("H19", "On a model with a hidden error, dreaming costs ticks before it pays, and the cost is bounded by how fast dream trust falls.",
                "the dreaming agent is slower to recover than the one that never dreamed",
                HypothesisVerdict.NotRun, "the nightmare test was not run"),
            Closed("H20", "The waking-with-the-answer effect exists only when the day's exploration had already landed most of the chain; a dream cannot supply a step the agent has never seen land.",
                "no next-day gains at all",
                HypothesisVerdict.Held, "a dream over a learned model supplies no step the agent never saw land"),
        ],
        [
            .. DemoWorlds.SelectMany(w => new[] { "levin", "bach", "hybrid" }.Select(a =>
                new Arm("opaque/honest+dream+explore:scaled", a, w) { Opaque = true, Extensions = ["honest", "dream", "explore:scaled"], Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" })),
        ],
        Repeats: 1);

    /// <summary>The Peterson map-keeper through batteries 1 to 3, with its own four predictions.</summary>
    public static Battery Peterson { get; } = new(
        "peterson",
        "design-peterson.md (results: csharp/runs/batteries-peterson/README.md)",
        [
            Closed("P1", "On the flood ingenuity series the map-keeper reaches the goal in every opaque series without extensions, at more ticks than the colony with honest.",
                "it reaches fewer opaque goals than the colony",
                HypothesisVerdict.Failed, "failed as stated for the first version (0% opaque: it examined a map, it did not build one); P1b, with the exploration rule, held: 1.00 in every world and repeat, 37.6 tries against the colony's 25.0",
                "design-peterson.md, Figure 40"),
            Closed("P2", "On drift its record for the gateway never freezes (it moves in every lying case and every honest case after), and it meets one more lie per lying case than the Bach agent.",
                "the record freezes after the lies end, or it meets no more lies than the Bach agent",
                HypothesisVerdict.Held, "held on the record (moves every case); the extra lie did not happen, both meet one"),
            Closed("P3", "Under panic it finishes and executes no release while an anomaly is open.",
                "it hands off, or releases while an anomaly is open",
                HypothesisVerdict.Held, "held on finishing (14 ticks, the only design beside the colony); the one release was the re-examination the rule allows"),
            Closed("P4", "The adversary, with the same policy, finishes fewer cases than the map-keeper on every lying world and claims on the shortcut where the map-keeper does not.",
                "the adversary finishes as many cases, or never claims",
                HypothesisVerdict.Held, "held on finishing; claiming did not arise with a planner"),
        ],
        [
            .. DemoWorlds.SelectMany(w => new[] { "peterson", "adversary" }.SelectMany(a => new[]
            {
                new Arm("clear", a, w) { Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" },
                new Arm("opaque/explore:scaled", a, w) { Opaque = true, Extensions = ["explore:scaled"], Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" },
            })),
            new Arm("lying", "peterson", "permit") { Extensions = ["record"], Protocol = Protocol.CaseSequence, Cases = 12, Test = "drifting-tools", BarrierSetName = "short+counter" },
            new Arm("panic", "peterson", "permit") { EveryToolLiesOnce = true, Test = "panic" },
            .. new[] { "none", "all" }.SelectMany(b => new[] { "peterson", "adversary" }.Select(a => new Arm(b, a, "permit") { BarrierSetName = b, Test = "against-the-adversary" })),
        ],
        Repeats: 1);

    public static IReadOnlyList<Battery> All { get; } = [Levin, Bach, Hybrid, Biology, Dreams, Peterson];

    /// <summary>Every closed hypothesis by id: H1 to H20 and P1 to P4.</summary>
    public static IReadOnlyList<Hypothesis> Hypotheses { get; } = All.SelectMany(b => b.Hypotheses).ToList();
}
