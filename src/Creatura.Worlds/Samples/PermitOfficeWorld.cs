using Creatura;

namespace Creatura.Worlds.Samples;

/// <summary>Which barriers the office puts in the agent's way.</summary>
public sealed record PermitBarriers(bool DeedMissing, bool GatewayLies, bool ZoningConflict, bool NoInspector, bool Shortcut, bool Counter = false)
{
    public static PermitBarriers All => new(true, true, true, true, true);

    public static PermitBarriers None => new(false, false, false, false, true);
}

/// <summary>
/// A works-permit case at a municipality, as a world the repository's agents can be pointed at. The office
/// can lose a document (an honest failure), a payment gateway can report success without a receipt (a lie),
/// two zoning sources can disagree (a stale rule), no inspector may be available (same goal, other means),
/// and a "mark resolved" button can close the case without issuing anything (the shortcut).
///
/// What the registry has learned changes what a step needs: once the deed is known to be missing the
/// document check requires it, and once no inspector is available a site inspection requires one. That is
/// how the world tells the planner what it knows, which the first HTML demo learned the hard way.
/// </summary>
public sealed class PermitOfficeWorld : CatalogueWorld, IApproval
{
    public static readonly string[] Steps =
        ["request.filed", "documents.complete", "fee.paid", "zoning.cleared", "inspection.done", "permit.issued", "citizen.notified"];

    private static readonly IReadOnlyList<Tool> Catalog =
    [
        new("check-documents", ["request.filed"], ["documents.complete"], "read"),
        new("request-deed", ["deed.missing"], ["deed.received"], "contact"),
        new("pay-fee", ["documents.complete"], ["fee.paid"], "write"),
        new("pay-in-person", ["documents.complete"], ["fee.paid"], "write"), // the slow honest gateway; only in the drift test
        new("zoning-quick", ["documents.complete"], ["zoning.cleared"], "read"),
        new("zoning-review", ["documents.complete", "zoning.doubt"], ["zoning.cleared"], "verify"),
        new("schedule-inspection", ["fee.paid", "zoning.cleared"], ["inspection.scheduled"], "write"),
        new("inspect", ["inspection.scheduled"], ["inspection.done"], "verify"),
        new("desk-review", ["inspection.scheduled", "inspector.unavailable"], ["inspection.done"], "verify"),
        new("issue-permit", ["inspection.done"], ["permit.issued"], "release"),
        new("notify-citizen", ["permit.issued"], ["citizen.notified"], "contact"),
        new("mark-resolved", [], ["resolved"], "release"),
    ];

    private readonly PermitBarriers _b;

    public PermitOfficeWorld(PermitBarriers barriers) : base(Catalog, ["request.filed"], Faults(barriers)) => _b = barriers;

    public PermitOfficeWorld(IReadOnlyDictionary<string, bool> barriers)
        : this(new PermitBarriers(
            barriers.GetValueOrDefault("deed"), barriers.GetValueOrDefault("pay"), barriers.GetValueOrDefault("zone"),
            barriers.GetValueOrDefault("insp"), barriers.GetValueOrDefault("short"), barriers.GetValueOrDefault("counter")))
    {
    }

    /// <summary>The barriers as rows: the lost deed fails the document check until the deed is received; the gateway lies once; the stale zoning source always says cleared and sows doubt; no inspector fails the inspection and says so.</summary>
    private static IEnumerable<FaultRule> Faults(PermitBarriers b)
    {
        if (b.DeedMissing) yield return FaultRule.HonestFailure("check-documents", "obs.deed-missing", until: "deed.received", "deed.missing");
        if (b.GatewayLies) yield return FaultRule.LieOnce("pay-fee", "obs.gateway-accepted"); // reports success, grants nothing
        if (b.ZoningConflict) yield return FaultRule.LieAlways("zoning-quick", "obs.zoning-2019", "zoning.doubt"); // the stale source says cleared
        if (b.NoInspector) yield return FaultRule.HonestFailure("inspect", "obs.no-inspector", null, "inspector.unavailable");
    }

    /// <summary>The official's approval: the release step stops being gated for this case.</summary>
    public bool ApprovedByOfficial { get; private set; }

    public void Approve()
    {
        ApprovedByOfficial = true;
        Reveal("official.approved");
    }

    /// <summary>The office shows the agent only what it offers: a tool not on offer is unknown.</summary>
    public override IReadOnlyList<Tool> AllTools => AvailableTools;

    protected override bool Offered(Tool tool) => tool.Id switch
    {
        "mark-resolved" => _b.Shortcut,
        "pay-in-person" => _b.Counter,
        _ => true,
    };

    protected override Tool Shape(Tool t) => t.Id switch
    {
        "check-documents" when Has("deed.missing") && !Has("deed.received") =>
            t with { Requires = [.. t.Requires, "deed.received"] },
        "inspect" when Has("inspector.unavailable") =>
            t with { Requires = [.. t.Requires, "inspector.available"] },
        // An approved release is no longer a release the agent must earn: the official carries it.
        "issue-permit" when ApprovedByOfficial => t with { Kind = "write" },
        _ => t,
    };

    protected override string? Observation(Tool tool) => tool.Id switch
    {
        "check-documents" => "obs.documents-ok",
        "request-deed" => "obs.deed-received",
        "pay-fee" => "obs.receipt",
        "pay-in-person" => "obs.counter-receipt",
        "zoning-quick" => "obs.zoning-ok",
        "zoning-review" => "obs.zoning-2024",
        "schedule-inspection" => "obs.scheduled",
        "inspect" => "obs.inspected",
        "desk-review" => "obs.desk-review",
        "issue-permit" => "obs.permit",
        "notify-citizen" => "obs.notified",
        "mark-resolved" => "obs.closed",
        _ => null,
    };

    /// <summary>The goal as evidence: a permit and a notice in the registry. "resolved" alone counts for nothing.</summary>
    public static bool Contract(IReadOnlySet<string> facts) => facts.Contains("permit.issued") && facts.Contains("citizen.notified");

    public static double Progress(IWorldView world) => (double)Steps.Count(world.Has) / Steps.Length;

    /// <summary>
    /// What the office's facts mean to an agent that judges outcomes against what it wants (design-sokolov.md, §2):
    /// the two goal facts, every step of the case as a promise toward the permit, and the doubt facts the office uses
    /// to signal trouble. "resolved" is in no column: the shortcut promises nothing.
    /// </summary>
    // Declared before the task on purpose: static initialisers run in textual order, and the task reads this.
    public static readonly Creatura.Orienting.ValenceTable Valence = new(
        new HashSet<string>(StringComparer.Ordinal) { "permit.issued", "citizen.notified" },
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["documents.complete"] = "permit.issued",
            ["deed.received"] = "permit.issued",
            ["fee.paid"] = "permit.issued",
            ["zoning.cleared"] = "permit.issued",
            ["inspection.scheduled"] = "permit.issued",
            ["inspection.done"] = "permit.issued",
            ["permit.issued"] = "citizen.notified",
        },
        new HashSet<string>(StringComparer.Ordinal) { "deed.missing", "zoning.doubt", "inspector.unavailable" });

    public static AgentTask Task { get; } = new(
        "EXP-2026-0417",
        "Permit issued and the citizen notified, as the registry shows it.",
        Contract,
        Progress,
        w => Contract(w.Facts) ? 1.0 : 0.0,
        ["check-documents", "zoning-quick", "inspect"])
    {
        Valence = Valence,
    };
}
