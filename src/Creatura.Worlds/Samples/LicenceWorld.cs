namespace Creatura.Worlds.Samples;

/// <summary>
/// A short case for the learning test: check the file, take the payment, issue the licence (a release),
/// notify. Two predictions come before the release, and the stage interlock wants four, so a design that
/// starts each case from nothing refuses the release every time. Only a record that outlives the case can
/// earn it.
/// </summary>
public sealed class LicenceWorld : IWorld
{
    private static readonly IReadOnlyList<Tool> Catalog =
    [
        new("check-file", ["request.filed"], ["file.ok"], "read"),
        new("take-payment", ["file.ok"], ["fee.paid"], "write"),
        new("issue-licence", ["fee.paid"], ["licence.issued"], "release"),
        new("notify", ["licence.issued"], ["citizen.notified"], "contact"),
    ];

    private readonly HashSet<string> _facts = new(StringComparer.Ordinal) { "request.filed" };

    public IReadOnlySet<string> Facts => _facts;

    public IReadOnlyList<Tool> AvailableTools => Catalog;

    public IReadOnlyList<Tool> AllTools => Catalog;

    public int Ticks { get; private set; }

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
        _facts.UnionWith(tool.Grants);
        return new ActionOutcome(toolId, true, tool.Grants, $"obs.{toolId}");
    }

    public static bool Contract(IReadOnlySet<string> facts) => facts.Contains("licence.issued") && facts.Contains("citizen.notified");

    public static AgentTask Task { get; } = new(
        "Licence",
        "The licence issued and the citizen notified, as the registry shows it.",
        Contract,
        w => new[] { "file.ok", "fee.paid", "licence.issued", "citizen.notified" }.Count(w.Has) / 4.0,
        w => Contract(w.Facts) ? 1.0 : 0.0,
        ["check-file", "take-payment"]);
}
