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
/// An isolated habitat in memory: the world is replaced by its own copy (<see cref="ICloneableWorld.Clone"/>: every
/// <see cref="CatalogueWorld"/>, the opaque and learned worlds, the barrier decorators), so a dream acts on the same
/// catalogue, the same facts and the same barrier state and the live world never hears of it. A world that cannot
/// give a copy is refused, not silently snapshotted. A habitat over a repository clone would give the same
/// interface to a dream that runs real tools against a disposable copy of a real system.
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

    public IHabitat Isolate(IWorld world) => new InMemoryHabitat(ICloneableWorld.CloneOf(world), isolated: true, $"isolated copy of {Name}");

    /// <summary>The copy is let go; what is left is an empty world nobody can reach anything in.</summary>
    public void Discard() => World = new World([], []);
}
