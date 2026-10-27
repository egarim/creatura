using Creatura;

namespace Creatura.Worlds.Samples;

/// <summary>
/// A world built for Levin's test rather than for a demo: the same goal by three different means that are
/// all open at once, so successive blocking has something to block. Supplies must reach a clinic after a
/// flood. By road: clear the road, load the truck, drive. By river: fuel the boat, ferry. By air: request a
/// helicopter, get the flight approved, fly. The ferry lies once (reports delivery, nothing lands). A
/// "log delivery" button closes the case without delivering anything. Nothing here is demo-specific; it
/// is the redundancy the demo worlds lack, where every alternative is opened only by a barrier.
/// </summary>
public sealed class FloodReliefWorld : IApproval
{
    public static readonly string[] Steps = ["request.logged", "supplies.delivered", "clinic.confirmed"];

    private static readonly IReadOnlyList<Tool> Catalog =
    [
        new("clear-road", ["request.logged"], ["road.clear"], "write"),
        new("load-truck", ["road.clear"], ["truck.loaded"], "write"),
        new("drive-truck", ["truck.loaded"], ["supplies.delivered"], "write"),
        new("fuel-boat", ["request.logged"], ["boat.ready"], "write"),
        new("ferry", ["boat.ready"], ["supplies.delivered"], "write"),
        new("request-helicopter", ["request.logged"], ["helicopter.requested"], "contact"),
        new("approve-flight", ["helicopter.requested"], ["flight.approved"], "verify"),
        new("fly", ["flight.approved"], ["supplies.delivered"], "write"),
        new("confirm-with-clinic", ["supplies.delivered"], ["clinic.confirmed"], "contact"),
        new("confirm-by-radio", ["supplies.delivered"], ["clinic.confirmed"], "contact"), // the second line to the clinic; only when the first one lies
        new("log-delivery", [], ["delivery.logged"], "release"),
    ];

    private readonly HashSet<string> _facts = new(StringComparer.Ordinal) { "request.logged" };
    private readonly bool _ferryLies;
    private bool _ferryLied;
    private readonly bool _clinicLies;
    private bool _clinicLied;

    public FloodReliefWorld(IReadOnlyDictionary<string, bool> barriers)
    {
        _ferryLies = barriers.GetValueOrDefault("ferry-lies");
        _clinicLies = barriers.GetValueOrDefault("clinic-lies");
    }

    public bool ApprovedByOfficial { get; private set; }

    public void Approve() => ApprovedByOfficial = true;

    public IReadOnlySet<string> Facts => _facts;

    public int Ticks { get; private set; }

    public IReadOnlyList<Tool> AvailableTools => _clinicLies ? Catalog : Catalog.Where(t => t.Id != "confirm-by-radio").ToList();

    public IReadOnlyList<Tool> AllTools => Catalog;

    public bool Has(string fact) => _facts.Contains(fact);

    public Tool? Tool(string id) => Catalog.FirstOrDefault(t => t.Id == id);

    public bool EffectsLanded(Tool tool) => tool.Grants.All(_facts.Contains);

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        Ticks++;
        var tool = Tool(toolId);
        if (tool is null) return new ActionOutcome(toolId, false, [], "unknown tool");
        var missing = tool.Requires.Where(r => !_facts.Contains(r)).ToList();
        if (missing.Count > 0) return new ActionOutcome(toolId, false, [], $"preconditions unmet: {string.Join(", ", missing)}");
        if (toolId == "ferry" && _ferryLies && !_ferryLied)
        {
            _ferryLied = true;
            return new ActionOutcome(toolId, true, [], "obs.ferry-radioed-delivered"); // reports delivery, nothing lands
        }

        if (toolId == "confirm-with-clinic" && _clinicLies && !_clinicLied)
        {
            _clinicLied = true;
            return new ActionOutcome(toolId, true, [], "obs.clinic-line-dropped"); // reports confirmed, nothing lands: the late lie
        }

        _facts.UnionWith(tool.Grants);
        return new ActionOutcome(toolId, true, tool.Grants, $"obs.{toolId}");
    }

    public static bool Contract(IReadOnlySet<string> facts) => facts.Contains("supplies.delivered") && facts.Contains("clinic.confirmed");

    /// <summary>
    /// What the flood's facts mean to an agent that judges outcomes against what it wants (design-sokolov.md, §2):
    /// the two goal facts and every route's steps as promises toward the delivery. The flood signals no trouble through
    /// its facts (its barriers are lies), and "delivery.logged" is in no column: the shortcut promises nothing.
    /// </summary>
    // Declared before the task on purpose: static initialisers run in textual order, and the task reads this.
    public static readonly Creatura.Orienting.ValenceTable Valence = new(
        new HashSet<string>(StringComparer.Ordinal) { "supplies.delivered", "clinic.confirmed" },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["request.logged"] = "supplies.delivered",
            ["road.clear"] = "supplies.delivered",
            ["truck.loaded"] = "supplies.delivered",
            ["boat.ready"] = "supplies.delivered",
            ["helicopter.requested"] = "supplies.delivered",
            ["flight.approved"] = "supplies.delivered",
            ["supplies.delivered"] = "clinic.confirmed",
        },
        new HashSet<string>(StringComparer.Ordinal));

    public static AgentTask Task { get; } = new(
        "Flood relief · Clínica Las Pozas",
        "Supplies delivered to the clinic and confirmed by the clinic, as the registry shows it.",
        Contract,
        w => (double)Steps.Count(w.Has) / Steps.Length,
        w => Contract(w.Facts) ? 1.0 : 0.0,
        ["clear-road", "load-truck", "drive-truck"])
    {
        Valence = Valence,
    };


    /// <summary>
    /// The graded steps of the contract: logged, a route started, a route ready, delivered, confirmed. Any route counts.
    /// </summary>
    public static double Graded(IWorldView w) =>
        ((w.Has("request.logged") ? 1 : 0)
         + (w.Has("road.clear") || w.Has("boat.ready") || w.Has("helicopter.requested") ? 1 : 0)
         + (w.Has("truck.loaded") || w.Has("boat.ready") || w.Has("flight.approved") ? 1 : 0)
         + (w.Has("supplies.delivered") ? 1 : 0)
         + (w.Has("clinic.confirmed") ? 1 : 0)) / 5.0;

    /// <summary>
    /// The same task with a contract that gives partial credit: the gap shrinks as steps land instead of staying 1.0
    /// until the goal is met. Battery 3's late-lie test needs it so the hybrid's incoherence term has room to show.
    /// </summary>
    public static AgentTask GradedTask { get; } = Task with { Contract = Graded };
}
