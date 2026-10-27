namespace Creatura.Worlds.Samples;

/// <summary>
/// A short case for the learning test: check the file, take the payment, issue the licence (a release),
/// notify. Two predictions come before the release, and the stage interlock wants four, so a design that
/// starts each case from nothing refuses the release every time. Only a record that outlives the case can
/// earn it.
/// </summary>
public sealed class LicenceWorld : CatalogueWorld
{
    private static readonly IReadOnlyList<Tool> Catalog =
    [
        new("check-file", ["request.filed"], ["file.ok"], "read"),
        new("take-payment", ["file.ok"], ["fee.paid"], "write"),
        new("issue-licence", ["fee.paid"], ["licence.issued"], "release"),
        new("notify", ["licence.issued"], ["citizen.notified"], "contact"),
    ];

    public LicenceWorld() : base(Catalog, ["request.filed"])
    {
    }

    protected override string? Observation(Tool tool) => $"obs.{tool.Id}";

    public static bool Contract(IReadOnlySet<string> facts) => facts.Contains("licence.issued") && facts.Contains("citizen.notified");

    public static AgentTask Task { get; } = new(
        "Licence",
        "The licence issued and the citizen notified, as the registry shows it.",
        Contract,
        w => new[] { "file.ok", "fee.paid", "licence.issued", "citizen.notified" }.Count(w.Has) / 4.0,
        w => Contract(w.Facts) ? 1.0 : 0.0,
        ["check-file", "take-payment"]);
}
