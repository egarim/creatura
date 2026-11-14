// AXI with Creatura (docs/design-axi.md, slice C): the four translations between Creatura's types and AXI's JSON, and
// the first vertical slice on the virtual TikTok, offline. Creatura keeps its own names; AXI's appear only in the JSON.
// `dotnet run --project samples/Axi -- [--user muted-commuter] [--seed 101] [--answer no] [--debug true]` prints one
// axi/v1 line per message (Singular ADR-0108 §4, docs/axi/axi-v1.schema.json). Free text (detail, description) only with
// --debug: Singular discards it at intake, and a production line should not carry it.
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
var debug = opts.GetValueOrDefault("--debug") == "true";
var yes = opts.GetValueOrDefault("--answer", "yes") != "no";
var line = new Axi(debug, $"s-{Guid.NewGuid():N}");
line.Goals[task.Name] = Axi.GoalOf(world.Goal);
var events = new AxiEvents(line);
bool? accepted = null;

// 1-2. The user begins a goal; the world observes. Creatura runs its first plays under its own control layer.
Emit(line.Started(task));
var before = (Proximity: task.Progress(world), Friction: 0.0);
await Run(task, maxTicks: 4);

// 3. Friction, as AXI reads it from Creatura's events: the share of plays that failed or lied.
var friction = events.Friction;
// 4. AXI looks at the capabilities Creatura offers now and, when friction is high, proposes asking the user.
foreach (var c in line.Candidates(world, task)) Emit(c);
if (!task.Goal(world.Facts) && friction >= 0.5 && world.AvailableTools.Any(t => t.Id == "ask-what-they-want"))
{
    // Singular produces proposals (ADR-0108 §4): this line simulates Singular's, so the run reads end to end. It is
    // never posted; Singular refuses an axi.proposal from outside (proposal.singular-only).
    var proposal = line.Proposal("ask-what-they-want", task, friction, events, world.Ticks, confirm: true);
    var proposalId = (string)proposal["id"]!;
    Emit(proposal);
    // 5. The user is asked, and answers. (The synthetic user says yes unless --answer no; a real app asks on its own
    //    screen.) Everything about the proposal carries its id as the correlation id.
    var outer = line.Correlation;
    line.Correlation = proposalId;
    Emit(line.Event("approval.requested", world.Ticks, "ask-what-they-want", task, "the app asks the user"));
    Emit(line.Event(yes ? "approval.accepted" : "approval.rejected", world.Ticks, "ask-what-they-want", task, yes ? "the user said yes" : "the user said no"));
    accepted = yes;
    if (yes)
    {
        // 6. Creatura runs it as a goal of its own, under its own control layer, with its planner, which plans toward
        //    that goal: AXI grants nothing and runs nothing itself. Its lines echo the proposal's goal (the app declared
        //    no other; Singular refuses an undeclared one as goal.unknown), and its one outcome names the proposal (§7.4).
        var ask = task with { Name = "ask what they want", GoalText = "The user said what they came for.", Goal = f => f.Contains("goal.known") };
        line.Goals[ask.Name] = line.GoalId(task);
        var askBefore = (Proximity: ask.Progress(world), Friction: Math.Round(events.Friction, 2));
        await Run(ask, maxTicks: 2, new PlannerPolicy());
        Emit(line.Outcome(ask, world, events, askBefore, accepted: true, proposalId));
    }
    line.Correlation = outer;
}

// 7. Progress is re-evaluated: the main goal goes on while it is not met, and the outcome goes back to AXI. It names no
//    proposal: the app reports on its own goal; the proposal's outcome went out above.
if (!task.Goal(world.Facts)) await Run(task, maxTicks: 12);
Emit(line.Outcome(task, world, events, before, accepted, proposalId: null));
return 0;

async Task Run(AgentTask goal, int maxTicks, IPolicy? seat = null)
{
    var agent = AgentCatalog.Make("orienting", new AgentBuild(seat ?? new MaxStaytimePolicy(world), goal, AgentOptions.With(new FeedNotes(), events), seed));
    await agent.RunAsync(world, maxTicks);
    foreach (var e in events.Drain()) Emit(e);
}

static void Emit(JsonObject message) => Console.WriteLine(message.ToJsonString());

/// <summary>The four translations, as axi/v1 lines. Pure functions over Creatura's types, plus the envelope: a uuid per
/// line, one session per run, a correlation id per task or proposal, the time. The shapes are docs/design-axi.md's.</summary>
sealed class Axi(bool debug, string session)
{
    /// <summary>Creatura's task names to AXI's stable dotted goal ids (<c>&lt;app-goal&gt;[:&lt;scope&gt;]</c>).</summary>
    public Dictionary<string, string> Goals { get; } = new(StringComparer.Ordinal);

    public string Correlation { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The user's goal in the feed, as the app would declare it.</summary>
    public static string GoalOf(FeedGoal goal) => goal switch
    {
        FeedGoal.Connect => "user.connected",
        FeedGoal.Learn => "user.learned",
        _ => "user.entertained",
    };

    public string GoalId(AgentTask task) => Goals.TryGetValue(task.Name, out var id) ? id : throw new InvalidOperationException($"no goal id for task '{task.Name}'");

    private JsonObject Line(string type, int tick)
    {
        var o = new JsonObject
        {
            ["v"] = "axi/v1",
            ["type"] = type,
            ["eventId"] = Guid.NewGuid().ToString(),
            ["sessionId"] = session,
            ["correlationId"] = Correlation,
            ["at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["tick"] = tick,
        };
        return o;
    }

    /// <summary>Free text goes out only with --debug.</summary>
    private JsonObject Text(JsonObject o, string key, string? text)
    {
        if (debug && text is not null) o[key] = text;
        return o;
    }

    public JsonObject Started(AgentTask task) => Event("task.started", 0, null, task, task.GoalText);

    /// <summary>The capabilities Creatura offers now: the visible tools; a release-kind tool needs the user's confirmation.</summary>
    public IEnumerable<JsonObject> Candidates(IWorld world, AgentTask task) => world.AvailableTools.Select(t =>
    {
        var o = Line("axi.capability", world.Ticks);
        o["capabilityId"] = t.Id;
        o["goalId"] = GoalId(task);
        o["requiresConfirmation"] = t.Kind == task.ReleaseKind || t.Kind == "contact";
        return Text(o, "description", $"{t.Kind}: grants {string.Join(", ", t.Grants)}");
    });

    /// <summary>A proposal with a uuid id and a coded reason (no free text).</summary>
    public JsonObject Proposal(string capability, AgentTask task, double friction, AxiEvents events, int tick, bool confirm)
    {
        var o = Line("axi.proposal", tick);
        o["id"] = Guid.NewGuid().ToString();
        o["capabilityId"] = capability;
        o["goalId"] = GoalId(task);
        o["reason"] = new JsonObject { ["code"] = "friction.high", ["args"] = new JsonObject { ["friction"] = Math.Round(friction, 2), ["failed"] = events.Failed, ["plays"] = events.Plays } };
        o["parameters"] = new JsonObject();
        o["requiresConfirmation"] = confirm;
        return o;
    }

    public JsonObject Event(string kind, int tick, string? capability, AgentTask? task, string? detail)
    {
        var o = Line("axi.event", tick);
        o["kind"] = kind;
        if (task is not null) o["goalId"] = GoalId(task);
        o["capabilityId"] = capability;
        return Text(o, "detail", detail);
    }

    /// <summary>The outcome, with what was reported and what was confirmed kept apart, so a lie never reads as a success.</summary>
    public JsonObject Outcome(AgentTask task, TikTokWorld world, AxiEvents events, (double Proximity, double Friction) before, bool? accepted, string? proposalId)
    {
        var o = Line("axi.outcome", world.Ticks);
        o["goalId"] = GoalId(task);
        if (proposalId is not null) o["proposalId"] = proposalId;
        o["status"] = task.Goal(world.Facts) ? "succeeded" : world.Has("session.ended") ? "cancelled" : "failed";
        o["accepted"] = accepted;
        o["reported"] = new JsonObject { ["engagement"] = Math.Round(world.EngagementReported, 1), ["watchedSeconds"] = Math.Round(world.EngagementSeconds, 1), ["plays"] = events.Plays, ["lies"] = events.Lies };
        o["confirmed"] = new JsonObject
        {
            ["goalMet"] = task.Goal(world.Facts),
            ["proximityBefore"] = before.Proximity,
            ["proximityAfter"] = task.Progress(world),
            ["frictionBefore"] = before.Friction,
            ["frictionAfter"] = Math.Round(events.Friction, 2),
        };
        return o;
    }
}

/// <summary>Creatura's run as AXI events: an observer-only extension; it changes nothing in the run.</summary>
sealed class AxiEvents(Axi line) : IAgentExtension
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
        _pending.Add(line.Event(kind, context.Tick, tool.Id, context.Task, outcome.Observation));
    }

    public void Sleep(SleepContext context) =>
        _pending.Add(line.Event(context.GoalReached ? "task.completed" : "task.handed-off", context.LastTick, null, context.Task, context.Task.Name));

    public IReadOnlyList<JsonObject> Drain()
    {
        var all = _pending.ToList();
        _pending.Clear();
        return all;
    }
}
