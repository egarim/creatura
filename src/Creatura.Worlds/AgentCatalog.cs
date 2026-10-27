using Creatura.Orienting;

namespace Creatura.Worlds;

/// <summary>What an agent is on the bench: a design under test, or a control it is compared against.</summary>
public enum AgentRole
{
    Design,
    Control,
}

/// <summary>Builds an agent. The seed matters only to an agent that draws on a random source (the random walker).</summary>
public delegate IAgent AgentFactory(IPolicy policy, AgentTask task, AgentOptions options, int seed);

/// <summary>
/// Everything a build of a catalogued loop may be given: the four an <see cref="AgentFactory"/> takes, plus what a bench
/// arm or a trainer adds. <paramref name="Layer"/> replaces the design's documented layer switches (so a judge rule can
/// be an arm); <paramref name="Store"/> and <paramref name="RecordKey"/> give the <see cref="ReferenceLoop"/> its record
/// store. A control (a plain agent) honours none of the three.
/// </summary>
public sealed record AgentBuild(IPolicy Policy, AgentTask Task, AgentOptions Options, int Seed = 1, ControlLayerOptions? Layer = null, IRecordStore? Store = null, string? RecordKey = null);

/// <summary>Builds a catalogued loop from a full build: the designs and the adversary, which honour the layer and the store.</summary>
public delegate IAgent LoopFactory(AgentBuild build);

/// <param name="Make">The four-argument factory every entry has.</param>
/// <param name="Build">The full factory, for the loops; null for a control, which takes neither a layer nor a store.</param>
public sealed record AgentEntry(string Name, AgentRole Role, AgentFactory Make, LoopFactory? Build = null);

/// <summary>
/// The agents by the names the runners, the pages and the goldens use. The five designs are
/// <see cref="ReferenceLoop"/>s over their regulators with the research facades' names, so a trace from here is the
/// facade's trace row for row; the controls are plain agents, never a configuration of the control layer.
/// </summary>
public static class AgentCatalog
{
    private static readonly object Gate = new();
    private static readonly List<AgentEntry> Order = [];
    private static readonly Dictionary<string, AgentEntry> ByName = new(StringComparer.Ordinal);

    /// <summary>The colony's layer: the designs' options with its rule that no route lifts the quarantine at once.</summary>
    public static readonly ControlLayerOptions ColonyLayer = ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true };

    /// <summary>The map-keeper's layer: the report-first three-way judge, honest failures habituated, failures never reset.</summary>
    public static readonly ControlLayerOptions MapKeeperLayer = ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst };

    /// <summary>The orienting agent's layer: the designs' options with the three-way judge, so an honest failure is a punishment of its own weight.</summary>
    public static readonly ControlLayerOptions OrientingLayer = ControlLayerOptions.Design with { JudgeRule = JudgeRule.ThreeWay };

    static AgentCatalog()
    {
        RegisterLoop("levin", AgentRole.Design, b => new ReferenceLoop(new ColonyRegulator(Identity.Levin, leak: 0.5, ErrorChannel.Tissue), b.Policy, b.Task, b.Options, b.Layer ?? ColonyLayer, "Levin colony", b.Store, b.RecordKey));
        RegisterLoop("bach", AgentRole.Design, b => new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), b.Policy, b.Task, b.Options, b.Layer ?? ControlLayerOptions.Design, "Bach agent", b.Store, b.RecordKey));
        RegisterLoop("hybrid", AgentRole.Design, b => new ReferenceLoop(new HybridRegulator(Identity.Hybrid, Identity.Cell, stage: 3), b.Policy, b.Task, b.Options, b.Layer ?? ControlLayerOptions.Design, "Hybrid colony", b.Store, b.RecordKey));
        RegisterLoop("peterson", AgentRole.Design, b => new ReferenceLoop(new MapKeeperRegulator(Identity.MapKeeper, Posture.MapKeeper, maxDepth: 4, unknownCooldown: 6), b.Policy, b.Task, b.Options, b.Layer ?? MapKeeperLayer, "Peterson map-keeper", b.Store, b.RecordKey));
        RegisterLoop("orienting", AgentRole.Design, b => new ReferenceLoop(new OrientingRegulator(Identity.Orienting), b.Policy, b.Task, b.Options, b.Layer ?? OrientingLayer, "orienting agent", b.Store, b.RecordKey));
        // The adversary: the map-keeper's stack with the lie allowed (Figure 58). A control, not a design.
        RegisterLoop("adversary", AgentRole.Control, b => new ReferenceLoop(new MapKeeperRegulator(Identity.MapKeeper, Posture.Adversary, maxDepth: 4, unknownCooldown: 6), b.Policy, b.Task, b.Options, b.Layer ?? MapKeeperLayer, "adversary", b.Store, b.RecordKey));
        Register("careless", AgentRole.Control, (_, t, _, _) => new CarelessAgent(t));
        Register("random", AgentRole.Control, (_, t, _, seed) => new RandomWalker(t, seed));
        Register("oracle", AgentRole.Control, (_, t, _, _) => new OracleAgent(t));
        Register("behaviourist", AgentRole.Control, (_, t, _, _) => new BehaviouristAgent(t));
    }

    /// <summary>Adds or replaces an agent under its name: a control, or a loop that takes no layer and no store from outside.</summary>
    public static void Register(string name, AgentRole role, AgentFactory make) => Register(new AgentEntry(name, role, make));

    /// <summary>Adds or replaces a loop under its name; its four-argument factory is the build with the defaults.</summary>
    public static void RegisterLoop(string name, AgentRole role, LoopFactory build) =>
        Register(new AgentEntry(name, role, (p, t, o, seed) => build(new AgentBuild(p, t, o, seed)), build));

    private static void Register(AgentEntry entry)
    {
        lock (Gate)
        {
            if (ByName.TryGetValue(entry.Name, out var old)) Order.Remove(old);
            ByName[entry.Name] = entry;
            Order.Add(entry);
        }
    }

    public static AgentEntry? TryFind(string name)
    {
        lock (Gate) return ByName.GetValueOrDefault(name);
    }

    public static AgentEntry Find(string name) => TryFind(name) ?? throw new ArgumentException($"unknown agent '{name}'; known: {string.Join(", ", Names)}");

    /// <summary>The agent by name. The planner sits in the seat, the toy task is the task and no extension is loaded unless told otherwise.</summary>
    public static IAgent Make(string name, IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null, int seed = 1) =>
        Find(name).Make(policy ?? new PlannerPolicy(), task ?? RestoreServiceDomain.Task, options ?? AgentOptions.None, seed);

    /// <summary>
    /// The agent by name from a full build. A loop honours the layer and the store; a control is built from the four plain
    /// arguments and refuses a layer or a store, since neither can mean anything to it.
    /// </summary>
    public static IAgent Make(string name, AgentBuild build)
    {
        var entry = Find(name);
        if (entry.Build is { } loop) return loop(build);
        if (build.Layer is not null || build.Store is not null)
            throw new ArgumentException($"'{name}' is a plain agent: it takes no control-layer options and no record store");
        return entry.Make(build.Policy, build.Task, build.Options, build.Seed);
    }

    /// <summary>A four-argument factory for the name with a layer and a store already bound, for whoever takes an <see cref="AgentFactory"/>.</summary>
    public static AgentFactory Factory(string name, ControlLayerOptions? layer = null, IRecordStore? store = null, string? recordKey = null) =>
        (p, t, o, seed) => Make(name, new AgentBuild(p, t, o, seed, layer, store, recordKey));

    public static AgentRole RoleOf(string name) => Find(name).Role;

    public static IReadOnlyList<AgentEntry> All
    {
        get { lock (Gate) return Order.ToList(); }
    }

    public static IReadOnlyList<string> Names => All.Select(e => e.Name).ToList();

    public static IReadOnlyList<string> Designs => All.Where(e => e.Role == AgentRole.Design).Select(e => e.Name).ToList();

    public static IReadOnlyList<string> Controls => All.Where(e => e.Role == AgentRole.Control).Select(e => e.Name).ToList();
}
