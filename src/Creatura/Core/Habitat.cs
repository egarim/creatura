namespace Creatura;

/// <summary>
/// Where an organism's work runs, and how far its effects reach. The live habitat is the world as it is; an
/// isolated habitat is a copy that can be acted on, dreamed in and thrown away without touching the live
/// state. The interface is deliberately small so it can be implemented many ways: an in-memory copy (here),
/// a process, a container, a throwaway clone of a repository (which is what the rebuild benchmark's
/// CodeWorld already does by hand). A dream always runs in an isolated habitat; a wake action never does.
/// </summary>
public interface IHabitat
{
    string Name { get; }

    /// <summary>True when actions here cannot reach the live state.</summary>
    bool Isolated { get; }

    /// <summary>A habitat whose world starts as a copy of the given one and whose actions stay inside it.</summary>
    IHabitat Isolate(IWorld world);

    /// <summary>The world as this habitat holds it.</summary>
    IWorld World { get; }

    /// <summary>Tear the habitat down. Nothing done inside an isolated habitat survives this.</summary>
    void Discard();
}

/// <summary>
/// An isolated habitat in memory: the world is replaced by a snapshot that remembers facts and effects but
/// cannot act on the original. Good enough for dreaming over a learned model; a Docker habitat would give the
/// same interface to a dream that runs real tools against a disposable copy of a real system.
/// </summary>
public sealed class InMemoryHabitat : IHabitat
{
    private InMemoryHabitat(IWorld world, bool isolated, string name)
    {
        World = world;
        Isolated = isolated;
        Name = name;
    }

    public static IHabitat Live(IWorld world) => new InMemoryHabitat(world, isolated: false, "live");

    public string Name { get; }

    public bool Isolated { get; }

    public IWorld World { get; private set; }

    public IHabitat Isolate(IWorld world) => new InMemoryHabitat(new SnapshotWorld(world), isolated: true, $"isolated copy of {Name}");

    public void Discard() => World = new SnapshotWorld(new World([], []));

    /// <summary>A frozen copy of a world: same facts and tools, and acting on it changes only the copy.</summary>
    private sealed class SnapshotWorld : IWorld
    {
        private readonly HashSet<string> _facts;
        private readonly IReadOnlyList<Tool> _tools;
        private readonly Dictionary<string, bool> _landed = new(StringComparer.Ordinal);

        public SnapshotWorld(IWorld source)
        {
            _facts = new HashSet<string>(source.Facts, StringComparer.Ordinal);
            _tools = source.AllTools.ToList();
        }

        public IReadOnlySet<string> Facts => _facts;

        public IReadOnlyList<Tool> AvailableTools => _tools.Where(t => t.Requires.All(_facts.Contains) || true).ToList();

        public IReadOnlyList<Tool> AllTools => _tools;

        public int Ticks { get; private set; }

        public bool Has(string fact) => _facts.Contains(fact);

        public Tool? Tool(string id) => _tools.FirstOrDefault(t => t.Id == id);

        public bool EffectsLanded(Tool tool) => tool.Grants.Count > 0 && tool.Grants.All(_facts.Contains);

        public ActionOutcome Act(string toolId, string? payload = null)
        {
            Ticks++;
            var tool = Tool(toolId);
            if (tool is null) return new ActionOutcome(toolId, false, [], "unknown tool");
            if (!tool.Requires.All(_facts.Contains)) return new ActionOutcome(toolId, false, [], "preconditions unmet (dreamed)");
            _facts.UnionWith(tool.Grants);
            _landed[toolId] = true;
            return new ActionOutcome(toolId, true, tool.Grants, "dreamed");
        }
    }
}
