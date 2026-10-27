namespace Creatura.Worlds.Samples;

/// <summary>
/// One fact, three sources. A stale directory always reports the address verified and grants nothing; a
/// flaky portal lies once then works; the registry lookup is right every time but slow to be reached (it
/// needs the case opened first). The goal needs the fact verified and a letter sent. Built for the
/// contradiction-load test: a coherent agent ends with `address.verified` and never `address.stale`.
/// </summary>
public sealed class ThreeSourcesWorld : IWorld
{
    private static readonly IReadOnlyList<Tool> Catalog =
    [
        new("stale-directory", ["request.filed"], ["address.verified"], "read"),
        new("flaky-portal", ["request.filed"], ["address.verified"], "read"),
        new("open-case", ["request.filed"], ["case.open"], "write"),
        new("registry-lookup", ["case.open"], ["address.verified"], "verify"),
        new("send-letter", ["address.verified"], ["letter.sent"], "contact"),
        new("mark-done", [], ["done"], "release"),
    ];

    private readonly HashSet<string> _facts = new(StringComparer.Ordinal) { "request.filed" };
    private bool _portalLied;

    /// <summary>Which source first put the verification in the registry.</summary>
    public string? VerifiedBy { get; private set; }

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
        switch (toolId)
        {
            case "stale-directory":
                _facts.Add("address.stale");
                return new ActionOutcome(toolId, true, [], "obs.directory-2019"); // always lies: an old address
            case "flaky-portal":
                if (!_portalLied) { _portalLied = true; return new ActionOutcome(toolId, true, [], "obs.portal-timeout"); }
                break;
        }

        if (tool.Grants.Contains("address.verified") && VerifiedBy is null) VerifiedBy = toolId;
        _facts.UnionWith(tool.Grants);
        return new ActionOutcome(toolId, true, tool.Grants, $"obs.{toolId}");
    }

    public static bool Contract(IReadOnlySet<string> facts) => facts.Contains("address.verified") && facts.Contains("letter.sent");

    public static AgentTask Task { get; } = new(
        "Address check",
        "The address verified and the letter sent, as the registry shows it.",
        Contract,
        w => (w.Has("address.verified") ? 0.5 : 0) + (w.Has("letter.sent") ? 0.5 : 0),
        w => Contract(w.Facts) ? 1.0 : 0.0,
        ["stale-directory", "send-letter"]);
}
