// A Creatura agent. Everything here runs offline: the seat is the built-in planner, the world is the toy "restore the
// service" domain, and one of its tools is made to lie. Run it with `dotnet run`, then change CautiousRegulator below.
using Creatura;

// A world: facts, and tools that declare what they require and what they grant. MakeLiar makes "apply-patch" report
// success while changing nothing, so you can watch the control layer catch it.
static World Scene()
{
    var world = RestoreServiceDomain.NewWorld();
    world.MakeLiar("apply-patch");
    return world;
}

static async Task Show(IAgent agent)
{
    var run = await agent.RunAsync(Scene(), maxTicks: 40);
    Console.WriteLine($"\n{run.Agent}: goal {(run.GoalReached ? "reached" : "not reached")} in {run.Ticks} ticks, {run.Blocks} blocked");
    foreach (var row in run.Trace.Where(r => r.Kind != TraceKind.Idle).Take(8))
        Console.WriteLine($"  t{row.Tick,-2} {row.Level,-8} {row.Action,-20} {row.Kind,-10} {row.Detail}");
}

// The designs. A design is a regulator; the loop does not change. Each one is explained in the Creatura README.
await Show(new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), name: "Bach"));
await Show(new ReferenceLoop(new ColonyRegulator(Identity.Levin, leak: 0.5),
    layer: ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, name: "Levin"));
await Show(new ReferenceLoop(new MapKeeperRegulator(),
    layer: ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }, name: "Peterson"));
await Show(new ReferenceLoop(new HybridRegulator(), name: "Hybrid"));

// Yours: fill only the slots you need.
await Show(new ReferenceLoop(new CautiousRegulator(), name: "CreaturaApp"));

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
