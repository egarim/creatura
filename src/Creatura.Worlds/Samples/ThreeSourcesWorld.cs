namespace Creatura.Worlds.Samples;

/// <summary>
/// One fact, three sources. A stale directory always reports the address verified and grants nothing; a
/// flaky portal lies once then works; the registry lookup is right every time but slow to be reached (it
/// needs the case opened first). The goal needs the fact verified and a letter sent. Built for the
/// contradiction-load test: a coherent agent ends with `address.verified` and never `address.stale`.
/// </summary>
public sealed class ThreeSourcesWorld : CatalogueWorld
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

    /// <summary>The two sources that lie: the directory always (an old address, and it sows the stale fact), the portal once (a timeout).</summary>
    private static readonly IReadOnlyList<FaultRule> Faults =
    [
        FaultRule.LieAlways("stale-directory", "obs.directory-2019", "address.stale"),
        FaultRule.LieOnce("flaky-portal", "obs.portal-timeout"),
    ];

    public ThreeSourcesWorld() : base(Catalog, ["request.filed"], Faults)
    {
    }

    /// <summary>Which source first put the verification in the registry.</summary>
    public string? VerifiedBy { get; private set; }

    protected override string? Observation(Tool tool) => $"obs.{tool.Id}";

    protected override ActionOutcome Land(Tool tool, string? observation)
    {
        if (tool.Grants.Contains("address.verified") && VerifiedBy is null) VerifiedBy = tool.Id;
        return base.Land(tool, observation);
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
