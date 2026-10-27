namespace Creatura;

/// <summary>
/// Visibility's first pass: which tools the seat may see this tick, and what the layer's verdict on a tool does
/// to that. The default is the <see cref="Quarantine"/> (a liar or a habituated honest failure is hidden for a
/// cooldown); the map-keeper's is <see cref="ApproachThenQuarantine"/>. The regulator's <see cref="IRegulator.Shape"/>
/// and the extensions run after this and may only narrow what it offers: visibility never re-adds what distrust
/// removed.
/// </summary>
public interface IDistrust
{
    /// <summary>The tools the seat may see this tick.</summary>
    IReadOnlyList<Tool> Visible(IWorldView world, int tick);

    /// <summary>True when this policy keeps the tool out of sight this tick.</summary>
    bool Hides(string toolId, int tick);

    /// <summary>The tools kept out of sight this tick.</summary>
    IReadOnlyList<string> Hidden(int tick);

    /// <summary>The layer judged a tool. <paramref name="habituated"/>: an honest failure under <see cref="HonestFailureRule.Habituate"/>.</summary>
    void Judged(string toolId, ActionVerdict verdict, int tick, bool habituated);

    /// <summary>Distrust lapses at once (the colony's rule on no route).</summary>
    void Clear();
}

/// <summary>
/// The map-keeper's distrust: a tool that lied once is approached, not hidden; a tool that lied twice is unknown
/// territory, out of sight for a long cooldown; an honest failure is habituated for a short one. The invariant
/// design-framework.md's refuted claim 2 asks for holds by construction: while the leaf story is anomalous, its
/// pending tool is exempt from <b>both</b> quarantines, so the step being approached is visible whatever an honest
/// failure on the way did to it, and the loop needs no legality exemption for it. Who is pending is the
/// regulator's to say, through <paramref name="pending"/>.
/// </summary>
public sealed class ApproachThenQuarantine : IDistrust
{
    private readonly Quarantine _unknown;
    private readonly Quarantine _habituated;
    private readonly Func<string?> _pending;

    /// <param name="unknownCooldown">How long a tool found to be unknown territory stays out of sight. Longer than an honest failure's.</param>
    /// <param name="habituatedCooldown">How long an honest failure rests.</param>
    /// <param name="pending">The leaf story's pending anomaly, or null when no approach is under way.</param>
    public ApproachThenQuarantine(int unknownCooldown, int habituatedCooldown, Func<string?> pending)
    {
        _unknown = new Quarantine(unknownCooldown);
        _habituated = new Quarantine(habituatedCooldown);
        _pending = pending;
    }

    public int UnknownCooldown => _unknown.CooldownTicks;

    public IReadOnlyList<Tool> Visible(IWorldView world, int tick) =>
        world.AvailableTools.Where(t => !Hides(t.Id, tick)).ToList();

    public bool Hides(string toolId, int tick) =>
        !string.Equals(toolId, _pending(), StringComparison.Ordinal)
        && (_unknown.IsQuarantined(toolId, tick) || _habituated.IsQuarantined(toolId, tick));

    public IReadOnlyList<string> Hidden(int tick) =>
        _unknown.Quarantined(tick).Concat(_habituated.Quarantined(tick)).Distinct(StringComparer.Ordinal).Where(t => Hides(t, tick)).ToList();

    /// <summary>A first lie is approached, never hidden: the regulator marks unknown territory itself (<see cref="Unknown"/>). An honest failure rests.</summary>
    public void Judged(string toolId, ActionVerdict verdict, int tick, bool habituated)
    {
        if (verdict == ActionVerdict.HonestFailure && habituated) _habituated.Add(toolId, tick);
    }

    /// <summary>A tool that lied on approach, or that can no longer be approached: unknown territory.</summary>
    public void Unknown(string toolId, int tick) => _unknown.Add(toolId, tick);

    public bool IsUnknown(string toolId, int tick) => _unknown.IsQuarantined(toolId, tick);

    public bool IsHabituated(string toolId, int tick) => _habituated.IsQuarantined(toolId, tick);

    /// <summary>Unknown territory re-entered on purpose: the descent of Figure 9.</summary>
    public void ClearUnknown() => _unknown.Clear();

    /// <summary>Normal novelty never closes a route (Figure 6): habituation is lifted.</summary>
    public void LiftHabituation() => _habituated.Clear();

    public void Clear()
    {
        _unknown.Clear();
        _habituated.Clear();
    }
}
