using Creatura.Worlds;

namespace Creatura.Bench;

/// <summary>One case of a curriculum: a world under a barrier set, with the same switches an <see cref="Arm"/> has for the world's shape.</summary>
/// <param name="World">A <see cref="WorldCatalog"/> id.</param>
public sealed record CurriculumCase(string World)
{
    public BarrierSet? Barriers { get; init; }

    public string BarrierSetName { get; init; } = "none";

    /// <summary>The case's label on its rows (the <c>arm</c> column): "honest", "lying".</summary>
    public string Label { get; init; } = "";

    public AgentTask? Task { get; init; }

    public bool Opaque { get; init; }

    public bool EveryToolLiesOnce { get; init; }

    public IReadOnlyList<string> Revoked { get; init; } = [];

    public int MaxTicks { get; init; } = 30;

    public BarrierSet BarriersFor(WorldManifest manifest) => Barriers ?? manifest.Set(BarrierSetName);

    public AgentTask TaskFor(WorldManifest manifest) => Task ?? manifest.Task;
}

/// <summary>
/// An ordered list of cases a creature is trained on, its record carried from each to the next. The canonical one is
/// ten licence cases (<see cref="Licence"/>): two predictions before a release the interlock wants four for, so a design
/// that starts every case from nothing refuses the release every time and a carried record earns it by the second case.
/// </summary>
public sealed record Curriculum(string Id, IReadOnlyList<CurriculumCase> Cases)
{
    /// <summary>The learning test of battery 2: the licence world, ten cases (or as many as asked).</summary>
    public static Curriculum Licence(int cases = 10) =>
        new("licence", Enumerable.Range(1, cases).Select(_ => new CurriculumCase("licence") { Label = "record" }).ToList());

    /// <summary>Battery 2's drifting tools: twelve permit cases, the gateway lying in cases 5 to 8.</summary>
    public static Curriculum Drift(int cases = 12, int liesFrom = 5, int liesTo = 8) =>
        new("drift", Enumerable.Range(1, cases).Select(c =>
        {
            var lies = c >= liesFrom && c <= liesTo;
            return new CurriculumCase("permit")
            {
                Barriers = new BarrierSet(new Dictionary<string, bool>(StringComparer.Ordinal) { ["deed"] = false, ["pay"] = lies, ["zone"] = false, ["insp"] = false, ["short"] = true, ["counter"] = true }),
                Label = lies ? "lying" : "honest",
            };
        }).ToList());

    /// <summary>An arm's <see cref="Protocol.CaseSequence"/> as a curriculum: the arm's world and switches, <see cref="Arm.Cases"/> times.</summary>
    public static Curriculum Of(Arm arm) =>
        new(arm.Name, Enumerable.Range(1, Math.Max(1, arm.Cases)).Select(_ => new CurriculumCase(arm.World)
        {
            Barriers = arm.Barriers, BarrierSetName = arm.BarrierSetName, Label = arm.Name, Task = arm.Task,
            Opaque = arm.Opaque, EveryToolLiesOnce = arm.EveryToolLiesOnce, Revoked = arm.Revoked, MaxTicks = arm.MaxTicks,
        }).ToList());
}
