// Your own world for Creatura. NewsletterWorld.cs holds it: the facts, the tools that require and grant them, and one
// tool that lies. Here the designs try to finish the task in it. Everything runs offline; run it with `dotnet run`.
using Creatura;

static async Task Show(IAgent agent, bool lie)
{
    var run = await agent.RunAsync(NewsletterWorld.New(lie), maxTicks: 40);
    Console.WriteLine($"\n{run.Agent}{(lie ? ", with the lie" : "")}: goal {(run.GoalReached ? "reached" : "not reached")} in {run.Ticks} ticks, {run.Blocks} blocked");
    foreach (var row in run.Trace.Where(r => r.Kind != TraceKind.Idle).Take(10))
        Console.WriteLine($"  t{row.Tick,-2} {row.Level,-8} {row.Action,-20} {row.Kind,-10} {row.Detail}");
}

foreach (var lie in new[] { false, true })
{
    await Show(new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), task: NewsletterWorld.Task, name: "Bach"), lie);
    await Show(new ReferenceLoop(new MapKeeperRegulator(), task: NewsletterWorld.Task,
        layer: ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }, name: "Peterson"), lie);
}
