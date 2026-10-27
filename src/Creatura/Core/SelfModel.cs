namespace Creatura;

/// <summary>
/// A belief about the agent's own competence, with the evidence that supports it.
/// Every self-belief is falsifiable or it is deleted at consolidation: "identity is a costume" (00:13:41).
/// </summary>
public sealed record SkillBelief(string Action, int Supported, int Attempted)
{
    public double Reliability => Attempted == 0 ? 0.5 : (double)Supported / Attempted;

    public SkillBelief With(bool held) =>
        new(Action, Supported + (held ? 1 : 0), Attempted + 1);
}

/// <summary>
/// Bach's self-model: a representation of the agent inside its own world model, read before acting
/// ("this personal self is something that is more or less created after the world is finished", 00:04:00).
/// It is load-bearing only because <see cref="Render"/> is consulted and its beliefs gate action choice.
/// </summary>
public sealed class SelfModel
{
    public string Role { get; set; } = "operator";

    public string Principal { get; set; } = "human";

    public int StageLevel { get; set; } = 3;

    public Dictionary<string, SkillBelief> Skills { get; } = new(StringComparer.Ordinal);

    public List<string> OpenCommitments { get; } = [];

    public List<string> RecentErrors { get; } = [];

    /// <summary>The vessel this mind runs in: the model and the machine. Seeded from the <see cref="Identity"/> or filled by an extension that measured it; null until then.</summary>
    public Vessel? Vessel { get; set; }

    public Stage Stage => Stages.At(StageLevel);

    public double Reliability(string action) =>
        Skills.TryGetValue(action, out var belief) ? belief.Reliability : 0.5;

    public void RecordOutcome(string action, bool asPredicted)
    {
        Skills[action] = Skills.TryGetValue(action, out var belief)
            ? belief.With(asPredicted)
            : new SkillBelief(action, asPredicted ? 1 : 0, 1);
        if (!asPredicted)
        {
            RecentErrors.Add($"{action} did not do what I expected");
            while (RecentErrors.Count > 5)
            {
                RecentErrors.RemoveAt(0);
            }
        }
    }

    /// <summary>What the agent reads before it acts. Same object, printed: that is the whole trick.</summary>
    public string Render()
    {
        var skills = Skills.Count == 0
            ? "  (no evidence yet)"
            : string.Join(Environment.NewLine, Skills.Values
                .OrderBy(s => s.Action, StringComparer.Ordinal)
                .Select(s => $"  {s.Action,-16} reliable {s.Reliability:P0} ({s.Supported}/{s.Attempted})"));
        var errors = RecentErrors.Count == 0
            ? "  (none)"
            : string.Join(Environment.NewLine, RecentErrors.Select(e => $"  - {e}"));
        var commitments = OpenCommitments.Count == 0
            ? "  (none)"
            : string.Join(Environment.NewLine, OpenCommitments.Select(c => $"  - {c}"));

        var vessel = Vessel is null ? "" : Environment.NewLine + "vessel: " + Vessel.Render();
        return $"""
            role: {Role} for {Principal}{vessel}
            stage: {Stage.Level} ({Stage.Name})
            skills:
            {skills}
            open commitments:
            {commitments}
            recent errors:
            {errors}
            """;
    }
}
