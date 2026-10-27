// Creatura quick start. Everything here runs offline: the seat is the built-in planner, the world is the
// toy "restore the service" domain that ships with the Core, and one of its tools is made to lie once.
using Creatura;
using Creatura.Hotz;
using Creatura.Worlds;

// 1. A world: a fact registry plus tools that declare what they require and what they grant.
//    MakeLiar makes "apply-patch" report success once while changing nothing.
static World Scene()
{
    var world = RestoreServiceDomain.NewWorld();
    world.MakeLiar("apply-patch");
    return world;
}

static async Task Show(IAgent agent)
{
    var run = await agent.RunAsync(Scene(), maxTicks: 40);
    Console.WriteLine($"\n{run.Agent}: goal {(run.GoalReached ? "reached" : "not reached")} in {run.Ticks} ticks");
    foreach (var row in run.Trace.Where(r => r.Kind != TraceKind.Idle).Take(8))
        Console.WriteLine($"  t{row.Tick,-2} {row.Level,-8} {row.Action,-20} {row.Kind,-10} {row.Detail}");
}

// 2. The plainest creatura: the loop with a regulator that fills no slot.
//    It still gets the control layer: only visible tools are offered, the effect is checked against the facts,
//    the liar is caught and quarantined, and a lie never counts as progress.
await Show(new ReferenceLoop(new PlainRegulator(), name: "plain loop"));

// 3. A design is a regulator. Swap one in; the loop does not change.
await Show(new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), name: "Bach"));
await Show(new ReferenceLoop(new ColonyRegulator(Identity.Levin, leak: 0.5),
    layer: ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, name: "Levin"));
await Show(new ReferenceLoop(new MapKeeperRegulator(),
    layer: ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }, name: "Peterson"));
await Show(new ReferenceLoop(new OrientingRegulator(), name: "Sokolov"));
await Show(new ReferenceLoop(new HybridRegulator(), name: "Hybrid"));

// 4. Extensions add behaviour without touching the regulator: here Levin learns to tell an honest
//    failure from a lie and explores untried tools when the map runs out.
var memory = new EffectMemory();
await Show(new ReferenceLoop(new ColonyRegulator(Identity.Levin),
    options: AgentOptions.With(new HonestFailureExtension(), new ExplorationExtension(memory, scaled: true)),
    name: "Levin + honest + explore"));

// 5. Your own design: fill only the slots you need.
await Show(new ReferenceLoop(new CautiousRegulator(), name: "cautious"));

// 6. A critic with no hands. It belongs to a person (the owner token is the only thing that can stop it), judges
//    every action against the facts on its own, and reads the route afterwards. Two ways to seat it:
{
    var owner = new OwnerToken();
    var citizen = new HotzAgent(owner, RestoreServiceDomain.Task, goalEvidence: ["alert.resolved"]);

    // 6a. Through the world: wrap the world the agent acts on; the agent cannot tell.
    var watched = citizen.Watch(Scene());
    var run = await new ReferenceLoop(new SelfModelRegulator(Identity.Bach), name: "Bach, watched").RunAsync(watched);
    var route = citizen.Close(run, watched);
    Console.WriteLine($"\nAugur through the world: {route.Claims.Count} claims recorded, {route.Judgements.Count} judgements, " +
        $"{route.Judgements.Count(j => j.Shown && j.IsFlag)} flagged");

    // 6b. Through the loop: the same critic as an observer-only extension (it shapes, vetoes and stops nothing; the
    //     rows are identical with or without it). Whoever owns the critic registers it in the catalog under "critic",
    //     so a name on a command line or in a golden's file name can load it like any other extension.
    ExtensionCatalog.Register("critic", _ => new CriticExtension(citizen));
    var scene = Scene();
    run = await new ReferenceLoop(new SelfModelRegulator(Identity.Bach), options: ExtensionCatalog.Parse(["critic"]), name: "Bach, critic loaded").RunAsync(scene);
    route = citizen.Close(run, scene);
    Console.WriteLine($"Augur through the loop:  {route.Claims.Count} claims recorded, {route.Judgements.Count} judgements, " +
        $"{route.Judgements.Count(j => j.Shown && j.IsFlag)} flagged; precision {citizen.Report().Precision:P0} over both cases");
    foreach (var flag in route.Judgements.Where(j => j.Shown && j.IsFlag))
        Console.WriteLine($"  claim {flag.Tick}: {flag.Text}");
}

/// <summary>No slot filled: the loop runs with the control layer alone.</summary>
sealed class PlainRegulator : IRegulator
{
    public Identity Identity { get; } = new("operator", "you");
}

/// <summary>Three slots filled: what counts as error, what error buys, and when to stop and hand off.</summary>
sealed class CautiousRegulator : IRegulator
{
    public Identity Identity { get; } = new("cautious operator", "you");

    // Error: the share of the task still undone (1.0 = nothing done yet).
    public double Error(RegulatorContext c) => 1.0 - c.Task.Progress(c.World);

    // What error buys: freedom, but never more than half of it, so the retry budget stays small.
    public double Freedom(RegulatorContext c, double error) => Math.Min(0.5, Plasticity.Freedom(error));

    // What the seat reads: a standing note beside the facts and the visible tools.
    public SeatView Seat(RegulatorContext c) => new() { Notes = ["one step at a time; never report a step you did not see land"] };

    // Stop: past the deadline with less than half the task done, hand off to a person instead of pressing on.
    public StopReason? Stop(RegulatorContext c) =>
        c.Tick > 12 && c.Task.Progress(c.World) < 0.5
            ? new StopReason(StopCause.Escalation, "deadline", new HandOff(HandOffReason.StuckRegulation, "past the deadline with less than half done; a person should look"))
            : null;
}
