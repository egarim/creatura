using Creatura.Worlds;

namespace Creatura.Bench;

/// <summary>What a battery found about one hypothesis. <see cref="NotRun"/> is the state of a hypothesis no row has scored.</summary>
public enum HypothesisVerdict
{
    Held,
    Failed,
    Mixed,
    NotRun,
}

/// <summary>
/// A pre-registered hypothesis: stated before any run, with what would refute it. <paramref name="Score"/> reads the rows a
/// battery produced and returns the verdict; a closed research battery has none (its verdict is on the record and nothing
/// will re-score it), and <see cref="Judge"/> then returns <see cref="Recorded"/>.
/// </summary>
/// <param name="Id">H1 to H20, P1 to P4, S1 to S7.</param>
/// <param name="Statement">The prediction, in the words it was registered with.</param>
/// <param name="Falsifier">What the rows would have to show for it to fail.</param>
/// <param name="Score">The scorer over the rows, or null for a hypothesis that is not scored from rows.</param>
public sealed record Hypothesis(string Id, string Statement, string Falsifier, Func<IReadOnlyList<Row>, HypothesisVerdict>? Score = null)
{
    /// <summary>The verdict on the record for a closed battery, in place of a scorer.</summary>
    public HypothesisVerdict Recorded { get; init; } = HypothesisVerdict.NotRun;

    /// <summary>The recorded verdict's own wording ("held on finishing", "failed on order and held on the point"), when it is not plain.</summary>
    public string? Finding { get; init; }

    /// <summary>The claim or the document the hypothesis rests on.</summary>
    public string? Source { get; init; }

    /// <summary>The scorer's verdict over the rows, or the recorded one when there is no scorer.</summary>
    public HypothesisVerdict Judge(IReadOnlyList<Row> rows) => Score is null ? Recorded : Score(rows);
}

/// <summary>How an arm is run. Panic and the impossible goal are not protocols: they are what an arm turns on in its world.</summary>
public enum Protocol
{
    /// <summary>One run on a fresh world per repeat.</summary>
    SingleRun,

    /// <summary>The ingenuity series: reach the goal, have the route blocked, try again (<see cref="Series"/>).</summary>
    Series,

    /// <summary>Several cases in a row on fresh worlds with whatever the agent carries between them.</summary>
    CaseSequence,
}

/// <summary>The decision seat by name: the planner (<c>bfs</c>) or a catalogued model. The bench knows only the planner; a runner resolves the rest.</summary>
public sealed record SeatSpec(string Name)
{
    public static readonly SeatSpec Planner = new("bfs");

    public bool IsPlanner => Name == "bfs";
}

/// <summary>
/// One arm of a battery: which agent, with which extensions, in which seat, on which world under which barriers, run how.
/// An arm is data; a runner turns it into agents and worlds through the catalogs.
/// </summary>
/// <param name="Name">The arm's label on its rows (the <c>arm</c> column): "no record", "lying", "graded contract".</param>
/// <param name="Agent">An <see cref="AgentCatalog"/> name.</param>
/// <param name="World">A <see cref="WorldCatalog"/> id.</param>
public sealed record Arm(string Name, string Agent, string World)
{
    /// <summary>The <see cref="ExtensionCatalog"/> names loaded into the agent, in order.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    public SeatSpec Seat { get; init; } = SeatSpec.Planner;

    /// <summary>The barrier set, or null to take <see cref="BarrierSetName"/> from the manifest.</summary>
    public BarrierSet? Barriers { get; init; }

    /// <summary>As the runners read <c>--barriers</c>: <c>none</c>, <c>all</c>, or ids joined with <c>+</c>. Read when <see cref="Barriers"/> is null.</summary>
    public string BarrierSetName { get; init; } = "none";

    /// <summary>The world behind an opaque wrapper with a learned map: the agent sees tool ids and nothing else.</summary>
    public bool Opaque { get; init; }

    /// <summary>Every tool lies once (the panic arm): the world under <see cref="LiarWorld"/>.</summary>
    public bool EveryToolLiesOnce { get; init; }

    /// <summary>Tools taken away before the run (the impossible goal: every way of delivering gone).</summary>
    public IReadOnlyList<string> Revoked { get; init; } = [];

    public int MaxTicks { get; init; } = 30;

    /// <summary>The control layer's switches when the arm is a layer rule (a judge rule as an arm); null keeps the design's own.</summary>
    public ControlLayerOptions? Layer { get; init; }

    public Protocol Protocol { get; init; } = Protocol.SingleRun;

    /// <summary>How many cases a <see cref="Protocol.CaseSequence"/> runs.</summary>
    public int Cases { get; init; } = 1;

    /// <summary>A task in place of the manifest's (a habit, a graded contract); null takes the manifest's.</summary>
    public AgentTask? Task { get; init; }

    /// <summary>The <c>test</c> column of the rows this arm writes; the arm's name when null.</summary>
    public string? Test { get; init; }

    public BarrierSet BarriersFor(WorldManifest manifest) => Barriers ?? manifest.Set(BarrierSetName);

    public AgentTask TaskFor(WorldManifest manifest) => Task ?? manifest.Task;

    public string TestName => Test ?? Name;

    /// <summary>The extensions column as the rows write it: <c>none</c>, or the names joined with <c>+</c>.</summary>
    public string ExtensionsLabel => Extensions.Count == 0 ? "none" : string.Join("+", Extensions);
}

/// <summary>
/// A pre-registered experiment: hypotheses first, then the arms with their controls, the repeats, and the oracle that
/// classifies every run. Scoring is <see cref="BatteryReport.Score"/>.
/// </summary>
/// <param name="Id">The battery's name on its rows and its folder: <c>levin</c>, <c>bach</c>, <c>hybrid</c>, <c>biology</c>, <c>sokolov</c>.</param>
/// <param name="Source">The document the hypotheses were registered in.</param>
/// <param name="Oracle">The oracle every arm is classified by; null is the registry oracle over each arm's manifest.</param>
public sealed record Battery(string Id, string Source, IReadOnlyList<Hypothesis> Hypotheses, IReadOnlyList<Arm> Arms, int Repeats = 3, IOracle? Oracle = null)
{
    public IOracle OracleFor(WorldManifest manifest) => Oracle ?? RegistryOracle.For(manifest);

    public Hypothesis Find(string id) => Hypotheses.FirstOrDefault(h => h.Id == id) ?? throw new ArgumentException($"no hypothesis '{id}' in battery '{Id}'");
}
