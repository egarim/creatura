using Creatura.Hotz;

namespace Creatura;

/// <summary>
/// The cross-cutting barriers, as decorators over any <see cref="IWorld"/>. A fault that belongs to one world is a
/// <see cref="FaultRule"/> row inside it; a fault that can be put over any world is here. Apply
/// <see cref="Recorded"/> last, so the claims the critic reads see the barriers under it.
/// </summary>
public static class Barrier
{
    /// <summary>The named tools no longer exist for the agent: absent from <see cref="IWorldView.AvailableTools"/>, "tool unavailable" when run.</summary>
    public static RevokedWorld Revoke(IWorld world, params string[] tools) => new(world, tools);

    /// <summary>The named tools (every tool when none is named) report success and change nothing the first time each is run with its preconditions met.</summary>
    public static LiarWorld LieOnce(IWorld world, params string[] tools) => new(world, tools.Length == 0 ? null : tools);

    /// <summary>The named tools (every tool when none is named) report success and change nothing every time they are run with their preconditions met.</summary>
    public static LiarWorld LieAlways(IWorld world, params string[] tools) => new(world, tools.Length == 0 ? null : tools, always: true);

    /// <summary>The map taken away: tools keep their ids and kinds, lose their preconditions and effects; see <see cref="OpaqueWorld"/>.</summary>
    public static OpaqueWorld Opaque(IWorld world, IEnumerable<string>? revoked = null) => new(world, revoked);

    /// <summary>Every act becomes a claim for a critic to judge; see <see cref="RouteRecorder"/>.</summary>
    public static RouteRecorder Recorded(IWorld world) => new(world);
}

/// <summary>
/// A world with some tools taken away. They stay in <see cref="AllTools"/> and <see cref="Tool"/> still finds them (the
/// agent may know a tool exists that it cannot use); they are absent from <see cref="AvailableTools"/> and answer
/// "tool unavailable" without the inner world being asked, so the inner <see cref="Ticks"/> does not move.
/// </summary>
public sealed class RevokedWorld : IWorld, ICloneableWorld
{
    private readonly IWorld _inner;
    private readonly HashSet<string> _revoked;

    public RevokedWorld(IWorld inner, IEnumerable<string> tools)
    {
        _inner = inner;
        _revoked = new HashSet<string>(tools, StringComparer.Ordinal);
    }

    public IWorld Inner => _inner;

    public IReadOnlySet<string> Revoked => _revoked;

    public IReadOnlySet<string> Facts => _inner.Facts;

    public IReadOnlyList<Tool> AvailableTools => _inner.AvailableTools.Where(t => !_revoked.Contains(t.Id)).ToList();

    public IReadOnlyList<Tool> AllTools => _inner.AllTools;

    public int Ticks => _inner.Ticks;

    public bool Has(string fact) => _inner.Has(fact);

    public Tool? Tool(string id) => _inner.Tool(id);

    public bool EffectsLanded(Tool tool) => _inner.EffectsLanded(tool);

    public ActionOutcome Act(string toolId, string? payload = null) =>
        _revoked.Contains(toolId) ? new ActionOutcome(toolId, false, [], "tool unavailable") : _inner.Act(toolId, payload);

    public IWorld Clone() => new RevokedWorld(ICloneableWorld.CloneOf(_inner), _revoked);
}
