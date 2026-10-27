namespace Creatura;

/// <summary>
/// A quarantine with decay. A tool whose effect did not land is distrusted — but distrust is a hypothesis,
/// not a life sentence: it lapses after a cooldown, so a one-off lie (a flaky CI runner) does not
/// permanently remove the only oracle the agent has.
///
/// Levin's version is habituation and its decay (00:36:18); Bach's is a belief that may be revised when
/// holding it stops being useful (01:21:27). Both point at the same operational rule, which is why it lives
/// here rather than in either agent.
/// </summary>
public sealed class Quarantine
{
    private readonly Dictionary<string, int> _since = new(StringComparer.Ordinal);

    public Quarantine(int cooldownTicks = 3) => CooldownTicks = cooldownTicks;

    public int CooldownTicks { get; }

    public IReadOnlyList<string> Quarantined(int tick) =>
        _since.Where(kv => tick - kv.Value < CooldownTicks).Select(kv => kv.Key).ToList();

    public void Add(string toolId, int tick) => _since[toolId] = tick;

    public void Clear() => _since.Clear();

    public bool IsQuarantined(string toolId, int tick) =>
        _since.TryGetValue(toolId, out var since) && tick - since < CooldownTicks;

    /// <summary>The tools the policy may see this tick.</summary>
    public IReadOnlyList<Tool> Visible(IWorldView world, int tick) =>
        world.AvailableTools.Where(t => !IsQuarantined(t.Id, tick)).ToList();
}
