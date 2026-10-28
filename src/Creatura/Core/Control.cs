namespace Creatura;

/// <summary>
/// A defended target state in its own space — Levin's point that goal states, once found, can be rewritten
/// (Lex Fridman Podcast #486, 01:01:45). Progress is 0..1, so stress is just the distance left.
/// </summary>
public sealed class SetPoint
{
    public required string Id { get; init; }

    /// <summary>Which state space this point is defended in ("contract", "obligations", "effect").</summary>
    public required string Space { get; init; }

    public required Func<IWorldView, double> Progress { get; init; }

    public double Tolerance { get; init; } = 0.001;

    public double Stress(IWorldView world) => Math.Clamp(1.0 - Progress(world), 0.0, 1.0);

    public bool Met(IWorldView world) => Stress(world) <= Tolerance;
}

/// <summary>One published reading. The log is the audit trail of who wanted what.</summary>
public sealed record StressReading(int Tick, string Level, double Stress, string SetPointId);

/// <summary>
/// Levin's stress channel (02:46:49): one normalized scalar per level, broadcast, deliberately leaky —
/// one cell's problem becomes its neighbours' problem through the wiring, not through altruism (Lex Fridman Podcast #486, 02:48:35).
/// </summary>
public sealed class StressBus
{
    private readonly Dictionary<string, double> _current = new(StringComparer.Ordinal);
    private readonly List<StressReading> _log = [];

    public int Tick { get; set; }

    public IReadOnlyList<StressReading> Log => _log;

    public void Publish(string level, SetPoint point, IWorldView world)
    {
        var stress = point.Stress(world);
        _current[level] = stress;
        _log.Add(new StressReading(Tick, level, stress, point.Id));
    }

    public double Own(string level) => _current.GetValueOrDefault(level);

    /// <summary>
    /// Effective stress = own stress, raised by a leaking fraction of the worst neighbour.
    /// This is what couples the levels without a manager.
    /// </summary>
    public double Effective(string level, double leak = 0.5)
    {
        var own = Own(level);
        var neighbours = _current.Where(kv => !string.Equals(kv.Key, level, StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .DefaultIfEmpty(0.0)
            .Max();
        return Math.Clamp(Math.Max(own, leak * neighbours), 0.0, 1.0);
    }

    public double Aggregate => _current.Values.DefaultIfEmpty(0.0).Max();
}

public static class Plasticity
{
    /// <summary>
    /// Levin's plasticity coupling (02:48:01): error buys freedom, success freezes it.
    /// Level-headed default: even a perfectly calm agent keeps a little room to improvise.
    /// </summary>
    public static double Freedom(double stress, double floor = 0.2, double gain = 0.8) =>
        Math.Clamp(floor + gain * stress, 0.0, 1.0);
}

/// <summary>
/// How far a level may reach: the largest goal and the longest horizon it may commit to
/// (Levin's cognitive light cone, how large a goal a system can work toward; Lex Fridman Podcast #486, 00:25:59).
/// </summary>
public sealed record LightCone(int MaxTicks, int MaxPlanDepth, string Scope)
{
    public bool Within(int tick) => tick <= MaxTicks;

    /// <summary>
    /// Plasticity widens the *retry budget*: more freedom means more distinct plans before escalating.
    /// Depth is fixed by the cone; error buys attempts, not omniscience.
    /// </summary>
    public int AttemptsFor(double freedom) => Budget.Attempts(freedom);
}
