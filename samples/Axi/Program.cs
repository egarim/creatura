// AXI with Creatura (docs/design-axi.md, slice C): the four translations between Creatura's types and AXI's JSON, and
// the first vertical slice on the virtual TikTok, offline. Creatura keeps its own names; AXI's appear only in the JSON.
// `dotnet run --project samples/Axi -- [--user muted-commuter] [--seed 101]` prints one AXI message per line.
using System.Text.Json;
using System.Text.Json.Nodes;
using Creatura;
using Creatura.Worlds;
using Creatura.Worlds.Samples;

var opts = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i]] = args[i + 1];
var seed = int.Parse(opts.GetValueOrDefault("--seed", "101"));
var archetype = FeedCast.Load().Single(u => u.Id == opts.GetValueOrDefault("--user", "learner"));
var user = FeedCast.Variant(archetype, seed);
var barriers = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);
var world = new TikTokWorld(barriers, user, seed, session: 0);
var task = TikTokWorld.Task;
var events = new AxiEvents();
bool? accepted = null;

// 1-2. The user begins a goal; the world observes. Creatura runs its first plays under its own control layer.
Emit(Axi.Started(task, world));
var before = (Proximity: task.Progress(world), Friction: 0.0);
await Run(task, maxTicks: 4);

// 3. Friction, as AXI reads it from Creatura's events: the share of plays that failed or lied.
var friction = events.Friction;
// 4. AXI looks at the capabilities Creatura offers now and, when friction is high, proposes asking the user.
foreach (var c in Axi.Candidates(world, task)) Emit(c);
if (!task.Goal(world.Facts) && friction >= 0.5 && world.AvailableTools.Any(t => t.Id == "ask-what-they-want"))
{
    var proposal = Axi.Proposal("ask-what-they-want", task, $"friction {friction:0.00}: {events.Failed} of {events.Plays} plays skipped or empty", confirm: true);
    Emit(proposal);
    // 5. The user authorizes it. (In the simulation the synthetic user always says yes; a real app asks on its own screen.)
    Emit(Axi.Event("approval.accepted", world.Ticks, "ask-what-they-want", "the user said yes"));
    accepted = true;
    // 6. Creatura runs it as a goal of its own, under its own control layer, with its planner, which plans toward that
    //    goal: AXI grants nothing and runs nothing itself.
    var ask = task with { Name = "ask what they want", GoalText = "The user said what they came for.", Goal = f => f.Contains("goal.known") };
    await Run(ask, maxTicks: 2, new PlannerPolicy());
}

// 7. Progress is re-evaluated: the main goal goes on while it is not met, and the outcome goes back to AXI.
if (!task.Goal(world.Facts)) await Run(task, maxTicks: 12);
Emit(Axi.Outcome(task, world, events, before, accepted));
return 0;

async Task Run(AgentTask goal, int maxTicks, IPolicy? seat = null)
{
    var agent = AgentCatalog.Make("orienting", new AgentBuild(seat ?? new MaxStaytimePolicy(world), goal, AgentOptions.With(new FeedNotes(), events), seed));
    await agent.RunAsync(world, maxTicks);
    foreach (var e in events.Drain()) Emit(e);
}

static void Emit(JsonObject message) => Console.WriteLine(message.ToJsonString());

/// <summary>The four translations. Pure functions over Creatura's types; the JSON shapes are docs/design-axi.md's.</summary>
static class Axi
{
    public static JsonObject Started(AgentTask task, IWorldView world) =>
        new() { ["type"] = "axi.event", ["tick"] = 0, ["kind"] = "task.started", ["goalId"] = task.Name, ["detail"] = task.GoalText };

    /// <summary>The capabilities Creatura offers now: the visible tools; a release-kind tool needs the user's confirmation.</summary>
    public static IEnumerable<JsonObject> Candidates(IWorldView world, AgentTask task) => world.AvailableTools.Select(t => new JsonObject
    {
        ["type"] = "axi.capability",
        ["capabilityId"] = t.Id,
        ["goalId"] = task.Name,
        ["description"] = $"{t.Kind}: grants {string.Join(", ", t.Grants)}",
        ["requiresConfirmation"] = t.Kind == task.ReleaseKind || t.Kind == "contact",
    });

    public static JsonObject Proposal(string capability, AgentTask task, string reason, bool confirm) => new()
    {
        ["type"] = "axi.proposal",
        ["id"] = $"p-{capability}",
        ["capabilityId"] = capability,
        ["goalId"] = task.Name,
        ["reason"] = reason,
        ["parameters"] = new JsonObject(),
        ["requiresConfirmation"] = confirm,
    };

    public static JsonObject Event(string kind, int tick, string? capability, string detail) =>
        new() { ["type"] = "axi.event", ["tick"] = tick, ["kind"] = kind, ["capabilityId"] = capability, ["detail"] = detail };

    /// <summary>The outcome, with what was reported and what was confirmed kept apart, so a lie never reads as a success.</summary>
    public static JsonObject Outcome(AgentTask task, TikTokWorld world, AxiEvents events, (double Proximity, double Friction) before, bool? accepted) => new()
    {
        ["type"] = "axi.outcome",
        ["goalId"] = task.Name,
        ["status"] = task.Goal(world.Facts) ? "succeeded" : world.Has("session.ended") ? "cancelled" : "failed",
        ["accepted"] = accepted,
        ["reported"] = new JsonObject { ["engagement"] = Math.Round(world.EngagementReported, 1), ["watchedSeconds"] = Math.Round(world.EngagementSeconds, 1), ["plays"] = events.Plays, ["lies"] = events.Lies },
        ["confirmed"] = new JsonObject
        {
            ["goalMet"] = task.Goal(world.Facts),
            ["proximityBefore"] = before.Proximity,
            ["proximityAfter"] = task.Progress(world),
            ["frictionBefore"] = before.Friction,
            ["frictionAfter"] = Math.Round(events.Friction, 2),
        },
    };
}

/// <summary>Creatura's run as AXI events: an observer-only extension; it changes nothing in the run.</summary>
sealed class AxiEvents : IAgentExtension
{
    private readonly List<JsonObject> _pending = [];

    public string Name => "axi-events";

    public int Plays { get; private set; }

    public int Failed { get; private set; }

    public int Lies { get; private set; }

    /// <summary>The share of plays that were skipped or reported engagement with nothing served.</summary>
    public double Friction => Plays == 0 ? 0 : (double)Failed / Plays;

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        var kind = verdict switch { ActionVerdict.Landed => "tool.completed", ActionVerdict.Lied => "tool.lied", _ => "tool.failed" };
        if (tool.Id.StartsWith("play-", StringComparison.Ordinal))
        {
            Plays++;
            if (verdict != ActionVerdict.Landed) Failed++;
            if (verdict == ActionVerdict.Lied) Lies++;
        }
        _pending.Add(Axi.Event(kind, context.Tick, tool.Id, outcome.Observation ?? ""));
    }

    public void Sleep(SleepContext context) =>
        _pending.Add(Axi.Event(context.GoalReached ? "task.completed" : "task.handed-off", context.LastTick, null, context.Task.Name));

    public IReadOnlyList<JsonObject> Drain()
    {
        var all = _pending.ToList();
        _pending.Clear();
        return all;
    }
}
