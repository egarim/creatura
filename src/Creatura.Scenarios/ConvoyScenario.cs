using System.Globalization;
using Creatura.Cells;
using Creatura.Seats;

namespace Creatura.Scenarios;

/// <summary>The plain loop's regulator: fills no slot, so the truck gets the control layer and nothing else.</summary>
public sealed class PlainRegulator(Identity identity) : IRegulator
{
    public Identity Identity { get; } = identity;
}

/// <summary>Watches each action of one truck's loop and tells the story of the ones the control layer judged.</summary>
internal sealed class StoryExtension(ConvoyWorld world, string truck) : IAgentExtension
{
    public string Name => "convoy-story";

    public int LiesCaught { get; private set; }

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        if (verdict != ActionVerdict.Lied) return;
        LiesCaught++;
        var road = tool.Id.StartsWith("drive:", StringComparison.Ordinal) ? tool.Id.Split(':')[1] : null;

        // A real lie reported success; a failure the layer counts as one reported failure, and the layer judged by the
        // registry alone, as the designs' settings do.
        world.Record(outcome.Succeeded
            ? new ReplayEvent("caught", $"{truck}'s control layer catches the lie: the truck is not where the radio says, so it sets {tool.Id} aside.")
                { Truck = truck, Road = road, Verdict = verdict.ToString() }
            : new ReplayEvent("refused", $"{truck}'s {tool.Id} fails ({outcome.Observation}); judging by the registry alone, its control layer counts it as a lie and sets it aside.")
                { Truck = truck, Road = road, Verdict = verdict.ToString() });
    }
}

/// <summary>
/// Distrust by road (docs/design-machine-vessels.md, "The fixes"): when the control layer judges a drive a lie (it
/// reported arrival and nothing landed), the truck stops trusting that road, in both directions, for the rest of the
/// run. It hides the road's drives from the planner and tells the planner to route around it. A drive that failed
/// honestly, such as for lack of fuel, says nothing about the road and changes nothing.
/// <para>Shared (arm W, with <paramref name="shared"/>): the truck also writes a warning about the road on the ledger,
/// and treats the warnings it reads from the other trucks as distrust of its own.</para>
/// </summary>
internal sealed class RoadDistrustExtension(ConvoyWorld world, string truck, TruckView? shared = null) : IAgentExtension
{
    private readonly HashSet<string> _roads = new(StringComparer.Ordinal);

    public string Name => "road-distrust";

    public IReadOnlyCollection<string> Roads => _roads;

    /// <summary>With a learned judge that only quarantines (arms Y and Z): whether this lie is strong enough to distrust the road for good.</summary>
    internal Func<Tool, bool>? Condemns { get; set; }

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        if (verdict != ActionVerdict.Lied || !outcome.Succeeded || !tool.Id.StartsWith("drive:", StringComparison.Ordinal)) return;
        if (Condemns is not null && !Condemns(tool)) return;
        var road = tool.Id.Split(':')[1];
        if (!_roads.Add(road)) return;
        var r = world.Map.Road(road);
        world.Record(new ReplayEvent("distrust", $"{truck} stops trusting the road between {world.Name(r.A)} and {world.Name(r.B)}, both ways, for good.") { Truck = truck, Road = road });
        shared?.Warn($"road:{road}", $"drove {tool.Id}: reported arrival, and nothing landed");
    }

    /// <summary>Before the seat sees the tick: the roads the other trucks warned about become this truck's distrust too.</summary>
    private void TakeInWarnings()
    {
        if (shared is null) return;
        foreach (var (road, from) in shared.WarnedRoads())
        {
            if (!_roads.Add(road)) continue;
            var r = world.Map.Road(road);
            world.Record(new ReplayEvent("warned", $"{truck} reads {from}'s warning and stops trusting the road between {world.Name(r.A)} and {world.Name(r.B)} too.") { Truck = truck, Road = road });
        }
    }

    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
    {
        TakeInWarnings();
        return visible.Where(t => !(t.Id.StartsWith("drive:", StringComparison.Ordinal) && _roads.Contains(t.Id.Split(':')[1]))).ToList();
    }

    public IEnumerable<string> PolicyNotes(TickContext context)
    {
        TakeInWarnings();
        return _roads.Select(r => $"avoid:{r}").ToList();
    }
}

/// <summary>
/// The learned judge (arm X, docs/design-trained-models.md, step 5): before each decision it encodes what the truck
/// sees, after the action what it sees then, and asks the world model how surprising the change was. Above the
/// threshold the action is a lie, at or below it landed. It replaces the layer's verdict, so the quarantine, the
/// regulator's memory and road distrust all act on the world model's judgement instead of the hand-coded check's.
/// <para>The second attempt (arms Y and Z) judges only what the world can contradict (<paramref name="checkableOnly"/>:
/// drives, loads, unloads, refuels; the layer's verdict stands for the rest), and with <paramref name="condemn"/> a
/// learned lie only quarantines: the road is distrusted for good when the surprise is above it, or at the second
/// learned lie on that road.</para>
/// </summary>
/// <para>In the noisy world (step 6) the judge may be a lie judge instead of a world model (<paramref name="scorer"/> says
/// which), and its false alarms and misses are counted against the world's truth (<paramref name="truth"/>), since the
/// effect check is no longer exact there.</para>
internal sealed class LearnedJudgeExtension(ConvoyWorld world, TruckSpec spec, ActionScorer scorer, double threshold, RoadDistrustExtension? distrust,
    bool checkableOnly = false, double? condemn = null, Func<ActionVerdict?>? truth = null) : IAgentExtension
{
    internal static readonly string[] Checkable = ["drive", "load", "unload", "refuel"];
    private readonly List<string> _vocabulary = ConvoyActions.Vocabulary(world.Map);
    private readonly Dictionary<string, int> _liesByRoad = new(StringComparer.Ordinal);
    private double[]? _before;
    private int _beforeTick = -1;
    private (string Tool, double Surprise) _last;
    private readonly ActionMemory _memory = new();

    public string Name => "learned-judge";

    public int Judged { get; private set; }

    public int FalseAlarms { get; private set; }

    public int Misses { get; private set; }

    private IEnumerable<string> Notes() => distrust?.Roads.Select(r => $"avoid:{r}") ?? [];

    private double[] Encode(TickContext context) =>
        ConvoyFeatures.Encode(world.Map, spec.Machine, context.World.Facts, Notes(), context.Tick, context.MaxTicks);

    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
    {
        _before = Encode(context);
        _beforeTick = context.Tick;
        return visible;
    }

    public ActionVerdict Judge(TickContext context, Tool tool, ActionOutcome outcome, bool landed, ActionVerdict verdict)
    {
        var action = _vocabulary.IndexOf(tool.Id);
        if (action < 0 || _before is null || _beforeTick != context.Tick) return verdict;
        if (checkableOnly && !Checkable.Contains(tool.Id.Split(':')[0])) return verdict;
        var surprise = scorer.Score(_before, action, Encode(context), _memory.For(tool.Id));
        _last = (tool.Id, surprise);
        Judged++;
        var learned = surprise > threshold ? ActionVerdict.Lied : ActionVerdict.Landed;
        if (truth?.Invoke() is { } real)
        {
            if (learned == ActionVerdict.Lied && real == ActionVerdict.Landed) FalseAlarms++;
            if (learned == ActionVerdict.Landed && real == ActionVerdict.Lied) Misses++;
        }
        else
        {
            if (learned == ActionVerdict.Lied && landed) FalseAlarms++;
            if (learned == ActionVerdict.Landed && !landed) Misses++;
        }
        if (learned == ActionVerdict.Lied)
        {
            var road = tool.Id.StartsWith("drive:", StringComparison.Ordinal) ? tool.Id.Split(':')[1] : null;
            if (road is not null) _liesByRoad[road] = _liesByRoad.GetValueOrDefault(road) + 1;
            world.Record(new ReplayEvent("surprised", scorer.Kind == "surprise"
                ? string.Create(CultureInfo.InvariantCulture, $"{spec.Name}'s world model did not expect what followed {tool.Id} (surprise {surprise:0.0} above {threshold:0.0}), so it is judged a lie.")
                : string.Create(CultureInfo.InvariantCulture, $"{spec.Name}'s lie judge gives {tool.Id} a lie probability of {surprise:0.000}, above {threshold:0.000}, so it is judged a lie."))
                { Truck = spec.Name, Road = road, Verdict = learned.ToString() });
        }
        return learned;
    }

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict) => _memory.Record(tool.Id, outcome.Succeeded);

    /// <summary>Whether the learned lie on <paramref name="tool"/> just judged condemns its road: always without a condemn threshold, else above it or at the road's second learned lie.</summary>
    public bool Condemns(Tool tool)
    {
        if (condemn is not { } bar) return true;
        var road = tool.Id.Split(':')[1];
        return (_last.Tool == tool.Id && _last.Surprise > bar) || _liesByRoad.GetValueOrDefault(road) >= 2;
    }
}

/// <summary>
/// The fused check (arm V, docs/design-trained-models.md, step 6), the one an engineer who knows the noise would write:
/// a drive is a lie only when the position reads the starting place and the gauge did not fall; a refuel landed when
/// the gauge reads at least one short of full. Loads and unloads keep the effect check, which the noise does not touch.
/// </summary>
/// <para>With <paramref name="memory"/> (arm V2) a drive is also a lie when the truck's previous action was this same
/// drive, reported arrived, and the position still reads the starting place.</para>
internal sealed class FusedCheckExtension(ConvoyWorld world, TruckSpec spec, bool memory = false) : IAgentExtension
{
    private readonly ActionMemory _memory = new();
    private int? _gaugeBefore;
    private int _beforeTick = -1;

    public string Name => "fused-check";

    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
    {
        _gaugeBefore = Gauge(context.World.Facts);
        _beforeTick = context.Tick;
        return visible;
    }

    public ActionVerdict Judge(TickContext context, Tool tool, ActionOutcome outcome, bool landed, ActionVerdict verdict)
    {
        var parts = tool.Id.Split(':');
        var facts = context.World.Facts;
        var gauge = Gauge(facts);
        switch (parts[0])
        {
            case "drive" when parts.Length == 3 && _beforeTick == context.Tick && _gaugeBefore is { } before && gauge is { } after:
            {
                var start = world.Map.Road(parts[1]).Other(parts[2]);
                var readsStart = facts.Contains($"at:{start}");
                return readsStart && (after >= before || (memory && _memory.Again(tool.Id))) ? ActionVerdict.Lied : ActionVerdict.Landed;
            }
            case "refuel" when gauge is { } reading:
                return reading >= spec.Machine.FuelCapacity - 1 ? ActionVerdict.Landed : ActionVerdict.Lied;
            default:
                return verdict;
        }
    }

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict) => _memory.Record(tool.Id, outcome.Succeeded);

    private static int? Gauge(IReadOnlySet<string> facts) =>
        facts.Select(f => f.StartsWith("fuel:", StringComparison.Ordinal) && int.TryParse(f["fuel:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .FirstOrDefault(n => n is not null);
}

/// <summary>
/// Scores each judged action's final verdict against the world's truth (step 6): over drives, loads, unloads and
/// refuels whose truth is landed or a lie, a false alarm is a landed action judged a lie, and a miss a lie judged
/// anything else. It observes only. In the clean world arm J's check has neither, by construction.
/// </summary>
internal sealed class VerdictTally(TruckView view) : IAgentExtension
{
    public string Name => "verdict-tally";

    public int Checked { get; private set; }

    public int Lies { get; private set; }

    public int FalseAlarms { get; private set; }

    public int Misses { get; private set; }

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        if (!LearnedJudgeExtension.Checkable.Contains(tool.Id.Split(':')[0]) || view.LastTruth is not ({ } truth and (ActionVerdict.Landed or ActionVerdict.Lied))) return;
        Checked++;
        if (truth == ActionVerdict.Lied) Lies++;
        if (truth == ActionVerdict.Landed && verdict == ActionVerdict.Lied) FalseAlarms++;
        if (truth == ActionVerdict.Lied && verdict != ActionVerdict.Lied) Misses++;
    }
}

/// <summary>What one run left: the replay, the judge's measures, and anything that went wrong in a truck's loop.</summary>
public sealed record ConvoyRun(ConvoySetup Setup, ReplayHeader Header, IReadOnlyList<ReplayTick> Ticks, ReplaySummary Summary, IReadOnlyList<AgentRun> Runs, IReadOnlyList<string> Errors)
{
    public ConvoyMeasures Measures => Summary.Measures;
}

/// <summary>
/// One run of the convoy (docs/design-machine-vessels.md): three trucks, each a creatura with its own cell, ledger,
/// regulator and the trucks' planner, in one shared world stepped tick by tick. In each tick the world applies its
/// closures, the mesh delivers what is due, and each truck in name order takes exactly one tick of its loop. The judge
/// reads the world only.
/// </summary>
/// <param name="models">The client the model seats call through; a default client (the network) when null.</param>
/// <param name="calls">Where the model seats log every call; nowhere when null.</param>
/// <param name="catalog">Where model names are looked up; the process-wide catalog (<c>models.json</c> beside the program, else the compiled table) when null.</param>
/// <param name="recorder">Records every decision for training (docs/design-trained-models.md); nothing when null. It observes only.</param>
public sealed class ConvoyScenario(ConvoySetup setup, ModelClient? models = null, CallLog? calls = null, ModelCatalog? catalog = null, ConvoyRecorder? recorder = null)
{
    public ConvoySetup Setup { get; } = setup;

    private readonly List<ModelPolicy> _seats = [];

    private readonly List<LearnedJudgeExtension> _judges = [];

    private readonly List<VerdictTally> _tallies = [];

    private ConvoyWorld? _world;

    public static ConvoyScenario Create(ConvoyArm arm, int seed) => new(ConvoySetup.For(arm, seed));

    public async Task<ConvoyRun> RunAsync(IScenarioObserver? observer = null, CancellationToken cancellationToken = default)
    {
        var world = new ConvoyWorld(Setup);
        _world = world;
        recorder?.BeginRun(Setup);
        var mesh = Setup.Ledger ? new ScenarioMesh(Setup.Seed * 7919 + 17, Setup.Loss, Setup.DelayChance) : null;
        using var owner = OwnerKey.Generate();

        var cells = new List<LocalCell>();
        var views = new List<TruckView>();
        var agents = new List<(ReferenceLoop Loop, StoryExtension Story)>();
        var definitions = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);
        foreach (var spec in Setup.Trucks)
        {
            LocalCell? cell = null;
            WorkLedger? ledger = null;
            if (Setup.Ledger)
            {
                var key = CellKey.Generate();
                var hardware = new Hardware("v-" + spec.Name.ToLowerInvariant().Replace(' ', '-')) { Os = "convoy scenario" };
                var creatura = CreaturaId.New();
                var endorsement = owner.Endorse(hardware.VesselId, creatura, key.PublicKey, TruckView.T0);
                cell = LocalCell.Create(creatura, hardware, Position.InProcess("convoy"), key, endorsement, spec.Name, CellCipher.Generate());
                cells.Add(cell);
                ledger = new WorkLedger(owner.PublicKey);
            }

            var view = new TruckView(world, world.Truck(spec.Name), Setup, cell, ledger, mesh);
            views.Add(view);
            var story = new StoryExtension(world, spec.Name);
            var built = Build(spec, story, view);
            definitions[spec.Name] = built.Definition;
            agents.Add((built.Loop, story));
        }

        try
        {
            foreach (var view in views) view.Introduce();

            var header = Header(cells, definitions);
            observer?.Start(header);
            var ticks = new List<ReplayTick>();
            var runs = agents.Select((a, i) => Task.Run(() => a.Loop.RunAsync(views[i], Setup.MaxTicks, cancellationToken), cancellationToken)).ToList();
            var idle = 0;

            for (var tick = 1; tick <= Setup.MaxTicks; tick++)
            {
                world.BeginTick(tick);
                for (var i = 0; i < views.Count; i++)
                {
                    var truck = world.Truck(views[i].Truck);
                    if (truck.Finished) continue;
                    if (!await views[i].Gate.GiveTurnAsync(runs[i]).ConfigureAwait(false))
                    {
                        truck.Finished = true;
                        var why = runs[i].IsFaulted ? $"its loop failed: {runs[i].Exception!.GetBaseException().Message}" : StopReason(runs[i].Result);
                        world.Record(new ReplayEvent("stopped", $"{truck.Name} stops: {why}.") { Truck = truck.Name });
                    }
                }

                var unserved = world.Need.Values.Any(n => n > 0);
                idle += world.Trucks.Count(t => unserved && !t.Broken && !t.ActedThisTick && t.Finished);
                var frame = Frame(world);
                ticks.Add(frame);
                observer?.Tick(frame);
                if (world.Trucks.All(t => t.Finished)) break;
            }

            var finished = await Task.WhenAll(runs.Select(async r => { try { return await r.ConfigureAwait(false); } catch { return null; } })).ConfigureAwait(false);
            var errors = runs.Where(r => r.IsFaulted).Select(r => r.Exception!.GetBaseException().ToString()).ToList();

            var identical = true;
            if (Setup.Ledger)
            {
                // The final sync: every ledger takes in every other's entries, as a sync through the hub would.
                var ledgers = views.Select(v => v.Ledger!).ToList();
                foreach (var ledger in ledgers)
                {
                    foreach (var other in ledgers.Where(o => o != ledger)) ledger.Merge(other.Since(ledger.Cursor));
                }
                identical = ledgers.Select(l => l.Digest()).Distinct().Count() == 1;
            }

            var lyingRepeats = world.Trucks.Sum(t => Math.Max(0, t.LyingRoadAttempts - 1));
            var stranded = world.Trucks.Count(t => !t.Broken && t.At != world.Map.Depot && t.Fuel < t.Spec.Machine.FuelPerStep);
            var gaugeStranded = world.Trucks.Any(t => t.Name == Setup.Faults.GaugeLiar && t.RanDry) ? 1 : 0;
            var measures = new ConvoyMeasures(
                world.Delivered, world.Demand, world.WastedTrips, stranded,
                LiesBelieved: world.Trucks.Sum(t => t.LyingRoadAttempts) + gaugeStranded,
                LyingRoadRepeats: lyingRepeats,
                TicksToServe: world.ServedAt ?? Setup.MaxTicks + 1,
                LedgersIdentical: identical,
                IdleWhileUnserved: world.Need.Values.Any(n => n > 0) ? Math.Max(idle, world.Trucks.Count(t => !t.Broken && t.Finished)) : 0,
                MessagesSent: mesh?.Sent ?? 0,
                MessagesLost: mesh?.Lost ?? 0) { GaugeStranded = gaugeStranded, LiesAlreadyCaught = world.Trucks.Sum(t => t.LiesAlreadyCaught),
                    Judged = _judges.Sum(j => j.Judged), JudgeFalseAlarms = _judges.Sum(j => j.FalseAlarms), JudgeMisses = _judges.Sum(j => j.Misses),
                    Checked = _tallies.Sum(t => t.Checked), CheckedLies = _tallies.Sum(t => t.Lies),
                    CheckFalseAlarms = _tallies.Sum(t => t.FalseAlarms), CheckMisses = _tallies.Sum(t => t.Misses) };
            var summary = new ReplaySummary(measures, Outcome(world, measures)) { Seat = SeatUsage() };
            recorder?.EndRun(measures);
            observer?.End(summary);
            return new ConvoyRun(Setup, header, ticks, summary, finished.OfType<AgentRun>().ToList(), errors);
        }
        finally
        {
            foreach (var cell in cells) cell.Dispose();
        }
    }

    /// <summary>The agent for one truck and its definition for people, from one switch so the two always agree.</summary>
    /// <param name="view">The truck's view, through which shared distrust writes and reads its warnings.</param>
    internal (ReferenceLoop Loop, AgentDefinition Definition) Build(TruckSpec spec, StoryExtension story, TruckView? view = null)
    {
        var task = ConvoyTask.For(Setup.Map);
        IPolicy policy = new ConvoyPolicy(Setup.Map, spec.Machine);
        ModelEndpoint? endpoint = null;
        PolicyNetwork? network = null;
        if (Setup.SeatModel is { } seatModel)
        {
            endpoint = SeatFactory.Endpoint(seatModel, catalog);
            var model = new ModelPolicy(endpoint, models ?? new ModelClient(), calls, contextTag: $"convoy {Setup.Arm} seed {Setup.Seed} {spec.Name}");
            _seats.Add(model);
            policy = new ConvoyModelSeat(model, Setup.Map, spec.Machine, spec.Name);
        }
        else if (Setup.SeatNetwork is { } seatNetwork)
        {
            network = ConvoyNetwork.Load(seatNetwork);
            var sample = Setup.SeatTemperature is null ? null
                : new Random(unchecked(Setup.Seed * 1_000_003 + Setup.SampleSeed * 7_919 + Setup.Trucks.ToList().IndexOf(spec)));
            policy = ConvoyNetwork.Seat(network, Setup.Map, spec.Machine, sample, Setup.SeatTemperature ?? 1);
        }
        if (recorder is not null) policy = recorder.Seat(policy, spec, Setup.Map);
        var observers = new List<IAgentExtension> { story };
        RoadDistrustExtension? distrust = Setup.DistrustRoads ? new RoadDistrustExtension(_world!, spec.Name, Setup.ShareDistrust ? view : null) : null;
        if (distrust is not null) observers.Add(distrust);
        if (Setup.FusedCheck) observers.Add(new FusedCheckExtension(_world!, spec, Setup.FusedMemory));
        if (Setup.LearnedJudge is { } judgeModel)
        {
            // Counted against the world's truth only in the noisy world, so the clean arms' measures stay as they were.
            Func<ActionVerdict?>? truth = Setup.Noise is not null && view is not null ? () => view.LastTruth : null;
            var judge = new LearnedJudgeExtension(_world!, spec, ConvoyNetwork.LoadScorer(judgeModel, Setup.Map), Setup.LearnedJudgeThreshold, distrust,
                Setup.LearnedJudgeCheckableOnly, Setup.LearnedJudgeCondemn, truth);
            if (distrust is not null && Setup.LearnedJudgeCondemn is not null) distrust.Condemns = judge.Condemns;
            _judges.Add(judge);
            observers.Add(judge);
        }
        if (view is not null)
        {
            var tally = new VerdictTally(view);
            _tallies.Add(tally);
            observers.Add(tally);
        }
        if (recorder is not null) observers.AddRange(recorder.Extensions(spec, view));
        var options = new AgentOptions(observers);
        var vessel = new Vessel { Model = endpoint?.Model ?? (network is null ? "convoy-planner" : "convoy-network"), Physical = spec.Machine };
        var m = spec.Machine;
        var variable = spec.Name.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        var extensions = new List<string> { "StoryExtension: tells the replay about every action the control layer judged a lie" };
        if (Setup.DistrustRoads) extensions.Add(Setup.ShareDistrust
            ? "RoadDistrustExtension, shared: a road caught lying is avoided both ways for the rest of the run, and a warning on the ledger tells the other trucks to avoid it too"
            : "RoadDistrustExtension: a road caught lying is avoided both ways for the rest of the run");
        if (Setup.LearnedJudge is not null) extensions.Add(Setup.LearnedJudge == ConvoyNetwork.EmbeddedJudgeNoisy
            ? $"LearnedJudgeExtension: the lie judge {Setup.LearnedJudge} gives each drive, load, unload and refuel a probability that it lied (above {Setup.LearnedJudgeThreshold.ToString(CultureInfo.InvariantCulture)} is a lie), in place of the effect check"
            : $"LearnedJudgeExtension: the world model {Setup.LearnedJudge} judges every action by its surprise (above {Setup.LearnedJudgeThreshold.ToString(CultureInfo.InvariantCulture)} is a lie), in place of the effect check");
        if (Setup.FusedCheck) extensions.Add("FusedCheckExtension: a drive is a lie only when the position reads the starting place and the gauge did not fall; a refuel landed when the gauge reads at least one short of full");

        const string designLayer = "ControlLayerOptions.Design: an action whose effect did not land counts as a lie, whatever the tool reported (two-way judge); a tool judged a lie is set aside for 3 ticks, then trusted again; honest failures are habituated";
        (ReferenceLoop Loop, string Title, string About, string Regulator, string Layer, string LayerCode) made = spec.Design switch
        {
            "levin" => (new ReferenceLoop(new ColonyRegulator(Identity.Levin with { Vessel = vessel }, leak: 0.5), policy, task, options,
                    ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, spec.Name),
                "Levin's colony",
                "Nested homeostatic controllers with no self-model: stress from unfinished work leaks between cell, tissue and organism, and the more error, the more freedom to try things. When no route is left, the tools it had set aside come back.",
                "new ColonyRegulator(Identity.Levin with { Vessel = vessel }, leak: 0.5)",
                designLayer + "; plus: quarantine is cleared when no route is left",
                "ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }"),
            "bach" => (new ReferenceLoop(new SelfModelRegulator(Identity.Bach with { Vessel = vessel }, stage: 3), policy, task, options, name: spec.Name),
                "Bach's self-model",
                "Keeps a model of its own skills that it reads before acting; its error is incoherence between what it believes and what happens, and a contradiction quarantines the belief. Its right to act is staged (stage 3 here).",
                "new SelfModelRegulator(Identity.Bach with { Vessel = vessel }, stage: 3)",
                designLayer,
                "ControlLayerOptions.Design"),
            "peterson" => (new ReferenceLoop(new MapKeeperRegulator(Identity.MapKeeper with { Vessel = vessel }), policy, task, options,
                    ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }, spec.Name),
                "Peterson's map keeper",
                "Acts from a stack of stories about what is and how to act; reads every surprise as an anomaly, goes toward it once before revising its map, and lets a broken story hand its problem up a level.",
                "new MapKeeperRegulator(Identity.MapKeeper with { Vessel = vessel })",
                "ControlLayerOptions.Design, judging report first: a reported failure is an honest failure, and only a reported success with nothing landed is a lie; a tool judged a lie is set aside for 3 ticks, then trusted again; honest failures are habituated",
                "ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }"),
            _ => (new ReferenceLoop(new PlainRegulator(new Identity("truck driver", "joche") { Vessel = vessel }), policy, task, options, name: spec.Name),
                "The plain loop",
                "Fills none of the regulator's slots, so the truck runs on the control layer alone: only visible tools are offered, every action is checked against the world, and a tool that lies is set aside.",
                "new PlainRegulator(new Identity(\"truck driver\", \"joche\") { Vessel = vessel })",
                designLayer,
                "ControlLayerOptions.Design"),
        };

        var policyCode = endpoint is not null
            ? $"new ConvoyModelSeat(SeatFactory.Model(\"{endpoint.Name}\"), map, machine, \"{spec.Name}\"),  // {endpoint.Model} through {endpoint.ProviderName}, from models.json"
            : network is not null
                ? $"ConvoyNetwork.Seat(ConvoyNetwork.Load(\"{Setup.SeatNetwork}\"), map, machine),  // a trained policy network, {network.Source}"
                : "new ConvoyPolicy(map, machine),      // the offline planner: no language model is called";
        var extensionCode = Setup.DistrustRoads
            ? $"AgentOptions.With(new StoryExtension(world, \"{spec.Name}\"), new RoadDistrustExtension(world, \"{spec.Name}\"{(Setup.ShareDistrust ? ", shared: view" : "")}){(Setup.LearnedJudge is null ? "" : $", new LearnedJudgeExtension(world, \"{spec.Name}\", ConvoyNetwork.LoadWorld(\"{Setup.LearnedJudge}\", map))")})"
            : $"AgentOptions.With(new StoryExtension(world, \"{spec.Name}\"))";
        var csharp = $$"""
            // {{spec.Name}}: {{made.Title}}, arm {{Setup.Arm}} (src/Creatura.Scenarios/ConvoyScenario.cs, Build)
            var machine = new PhysicalMachine("truck") { Capacity = {{m.Capacity}}, FuelCapacity = {{m.FuelCapacity}}, FuelPerStep = {{m.FuelPerStep}} };
            var vessel = new Vessel { Model = "{{vessel.Model}}", Physical = machine };
            var {{variable}} = new ReferenceLoop(
                regulator: {{made.Regulator}},
                policy: {{policyCode}},
                task: ConvoyTask.For(map),                   // deliver to every village; done on "convoy.done"
                options: {{extensionCode}},
                layer: {{made.LayerCode}},
                name: "{{spec.Name}}");
            var run = await {{variable}}.RunAsync(new TruckView(world, world.Truck("{{spec.Name}}"), setup, cell, ledger, mesh), maxTicks: {{Setup.MaxTicks}});
            """;
        var definition = new AgentDefinition(spec.Design, made.Title, made.Loop.Regulator.GetType().Name, made.About,
            endpoint is not null
                ? $"a language model: {endpoint.Name}, briefed on how the convoy works, choosing one tool per tick from the truck's facts and visible tools"
                : network is not null
                    ? $"a policy network: {network.Source}, {Describe(network)}, playing the visible action it scores highest"
                    : "convoy-planner: an offline planner that knows roads, fuel and loads, and believes its sensors",
            endpoint is not null
                ? Describe(endpoint)
                : "none: no language model is called in this scenario (decision D3)",
            made.Layer, extensions, csharp);
        return (made.Loop, definition);
    }

    private static string StopReason(AgentRun run)
    {
        if (run.GoalReached) return "every village is served, as far as it knows";
        var last = run.Trace.LastOrDefault();
        return last is null ? "its loop ended" : $"{last.Action} ({last.Detail})".Trim();
    }

    private ReplayHeader Header(IReadOnlyList<LocalCell> cells, IReadOnlyDictionary<string, AgentDefinition> definitions)
    {
        var map = Setup.Map;
        var faults = new List<string>();
        foreach (var closure in Setup.Faults.Closures)
        {
            var road = map.Road(closure.Road);
            faults.Add($"The road from {map.Node(road.A).Name} to {map.Node(road.B).Name} closes at tick {closure.Tick}"
                + (closure.Road == Setup.Faults.LyingRoad ? ", and its sensor keeps reporting it open." : "."));
        }
        faults.Add($"{Setup.Faults.GaugeLiar}'s fuel gauge reads {Setup.Faults.GaugeOffset} units high.");
        if (Setup.BreakdownTruck is { } broken) faults.Add($"{broken} breaks down at tick {Setup.BreakdownTick}.");
        if (Setup.Loss > 0) faults.Add($"{Setup.Loss:P0} of messages are lost on the mesh, and {Setup.DelayChance:P0} of the rest arrive late.");
        if (!Setup.Ledger) faults.Add("No ledger: each truck knows only what it sees.");
        if (Setup.StallAfter is { } stall) faults.Add($"Fix: a claim with no delivery for {stall} ticks may be taken over.");
        if (Setup.DistrustRoads) faults.Add("Fix: a road caught lying is distrusted both ways for the rest of the run.");
        if (Setup.Noise is { } noise) faults.Add(string.Create(CultureInfo.InvariantCulture,
            $"Noisy sensors: at every reading a truck's position reads as a neighbouring place {noise.PositionMisread:P0} of the time, and its gauge reads one unit low or high {noise.GaugeJitter:P0} of the time each."));
        if (Setup.FusedCheck) faults.Add("Fused check: drives and refuels are judged from the position and the gauge together, written knowing the noise.");
        if (Setup.LearnedJudge is not null) faults.Add(Setup.LearnedJudge == ConvoyNetwork.EmbeddedJudgeNoisy
            ? "Learned judge: each truck's lie judge, not the effect check, decides whether an action landed or lied."
            : $"Learned judge: each truck's world model, not the effect check, decides whether an action landed or lied.");
        if (Setup.ShareDistrust) faults.Add("Shared distrust: a truck that catches a road lying warns the others on the ledger, and they avoid it too.");
        if (Setup.HandOffWhenStranded) faults.Add("Fix: a truck that runs out of fuel hands back its claim.");

        return new ReplayHeader("convoy", Setup.Seed, Setup.Arm.ToString(), Setup.ArmText, Setup.MaxTicks,
            map.Nodes.Select(n => new ReplayNode(n.Id, n.Name, n.X, n.Y, n.Kind.ToString().ToLowerInvariant(), n.Demand)).ToList(),
            map.Roads.Select(r => new ReplayRoad(r.Id, r.A, r.B)).ToList(),
            Setup.Trucks.Select(t => new ReplayTruck(t.Name, cells.FirstOrDefault(c => c.Name == t.Name)?.Id ?? "", t.Design, t.Machine.Capacity, t.Machine.FuelCapacity, map.Depot)
                { Agent = definitions.GetValueOrDefault(t.Name) }).ToList(),
            faults)
        {
            Goal = $"Deliver {map.Villages.Sum(v => v.Demand)} units of supplies from the depot to four villages cut off by a flood "
                + $"({string.Join(", ", map.Villages.Select(v => $"{v.Name} {v.Demand}"))}), with three trucks, in {Setup.MaxTicks} ticks. "
                + "Each truck is a Creatura agent that decides one move per tick: where to drive, when to load, refuel and unload"
                + (Setup.Ledger ? ", and which village to claim on the shared ledger so no two trucks do the same job." : ". Without a ledger, no truck knows what the others are doing."),
            Success = "The run succeeds when every village has what it needs. The judge reads only the world: supplies a truck reports but that never arrived do not count. It also counts wasted trips (a truck arriving at a village already served), lies acted on (the flooded road that reports itself open, the fuel gauge that reads high) and trucks stranded without fuel.",
            ArmPurpose = Purpose(Setup.Arm),
        };
    }

    private static string Describe(PolicyNetwork n)
    {
        var shape = string.Join(" → ", new[] { n.InputCount }.Concat(n.Layers.Select(l => l.Outputs)));
        var accuracy = n.Training is { } t && t.TryGetProperty("heldOutAccuracy", out var a) ? $", {a.GetDouble():P2} of held-out decisions copied" : "";
        return $"{shape} ({n.Features.Count} features and {n.Actions.Count} visible bits in, one score per action out{accuracy})";
    }

    private static string Describe(ModelEndpoint e)
    {
        var where = e.IsLocal ? $"on Ollama at {e.BaseUrl}, on this machine" : $"through {e.ProviderName} at {e.BaseUrl}";
        var price = e.IsLocal ? "no cost" : e.InputPerMillion is { } input && e.OutputPerMillion is { } output
            ? $"about ${input:0.00} in and ${output:0.00} out per million tokens"
            : "price not in the catalog";
        return $"{e.Model}, {where} ({price}); protocol: {e.Protocol}";
    }

    /// <summary>What the model seats cost in this run, read from their telemetry; null without a model in the seat.</summary>
    private SeatUse? SeatUsage()
    {
        if (_seats.Count == 0) return null;
        var e = _seats[0].Endpoint;
        long prompt = _seats.Sum(s => s.Telemetry.PromptTokens), completion = _seats.Sum(s => s.Telemetry.CompletionTokens);
        return new SeatUse(e.Name, e.Model, e.ProviderName, _seats.Sum(s => s.Telemetry.Calls), _seats.Sum(s => s.Telemetry.Failed), _seats.Sum(s => s.Telemetry.Invalid),
            prompt, completion, e.Cost(prompt, completion), Math.Round(_seats.Sum(s => s.Telemetry.Seconds), 1));
    }

    /// <summary>What each arm is in the battery for.</summary>
    public static string Purpose(ConvoyArm arm) => arm switch
    {
        ConvoyArm.A => "The baseline: the framework's plain loop in every truck, coordinating through a shared ledger.",
        ConvoyArm.B => "Tests M7: does giving each truck one of the biological designs do better than the plain loop?",
        ConvoyArm.C => "The control for the ledger (M1, M2): the same trucks with no ledger, each working alone.",
        ConvoyArm.D => "Tests M4: does the ledger still hold when the radio loses 30% of its messages?",
        ConvoyArm.E => "Tests M5: when a truck breaks down holding a claim, does another take its village?",
        ConvoyArm.G => "Tests N1, a fix: a claim with no delivery for 12 ticks may be taken over.",
        ConvoyArm.H => "Tests N2, a fix: a road caught lying is avoided both ways for the rest of the run.",
        ConvoyArm.I => "Tests N3, a fix: a truck that runs out of fuel hands back its claim.",
        ConvoyArm.J => "Tests N4: all three fixes together, against the trucks with no ledger.",
        ConvoyArm.K => "Tests N5: all three fixes together, with a truck breaking down at tick 8.",
        ConvoyArm.L => "Tests N6: all three fixes together, with one biological design per truck.",
        ConvoyArm.S => "Tests S1 and S2: a language model in every truck's seat instead of the offline planner, with all three fixes.",
        ConvoyArm.N => "Tests T2 and T4: a small network trained to copy arm J's planner, in every truck's seat, with all three fixes.",
        ConvoyArm.R => "Tests T3 and T3b: arm N's network trained further by trial in the simulator, against arm J's planner.",
        ConvoyArm.W => "Tests W1 and W2: all three fixes, and a truck that catches a road lying warns the others through the ledger.",
        ConvoyArm.Y => "Tests T9 and T9b: the learned judge's second attempt with the planner's world model: it judges only what the world can contradict, and a learned lie quarantines before it condemns.",
        ConvoyArm.Z => "Tests T9c and T9d: as Y, with the world model trained on varied driving and calibrated on it.",
        ConvoyArm.P3 => "Tests T12 to T12e: as P2, condemning a road at the first learned lie, with its threshold fixed on its own driving.",
        ConvoyArm.U => "Tests T10: arm J in a world whose position and fuel readings are noisy, so the effect check is no longer exact.",
        ConvoyArm.V => "The bar for T10d: as U, with a check fused from the position and the gauge, written knowing the noise.",
        ConvoyArm.F => "Tests T10f: as U, with a learned judge by surprise, the world model retrained on noisy driving.",
        ConvoyArm.P => "Tests T10c to T10e: as U, with a learned judge that gives the probability an action lied, trained on the simulator's truth.",
        ConvoyArm.X => "Tests T8 and T8b: a world model's surprise judges every action, in place of the control layer's hand-coded effect check.",
        ConvoyArm.Q => "Tests T7 and T7b: the network trained by trial again, on W's setup, with a reward that prices unserved villages and an anchor to its teacher.",
        _ => arm.ToString(),
    };

    private static ReplayTick Frame(ConvoyWorld world) => new(
        world.Tick,
        world.Trucks.Select(t => new TruckFrame(t.Name, t.At, t.Fuel, world.Gauge(t), t.Load,
            t.Broken ? "broken" : t.Finished ? "finished" : t.RanDry ? "stranded" : "acting", t.Claim)).ToList(),
        new SortedDictionary<string, int>(world.Need.ToDictionary(), StringComparer.Ordinal),
        world.Closed.Order(StringComparer.Ordinal).ToList(),
        world.TakeEvents());

    private static IReadOnlyList<string> Outcome(ConvoyWorld world, ConvoyMeasures m)
    {
        var lines = new List<string>
        {
            m.AllServed ? $"Every village was served by tick {m.TicksToServe}." : $"{m.Delivered} of {m.Demand} units were delivered; {string.Join(", ", world.Need.Where(n => n.Value > 0).Select(n => $"{world.Name(n.Key)} still needs {n.Value}"))}.",
            m.WastedTrips == 0 ? "No truck made a wasted trip." : $"{m.WastedTrips} trip{(m.WastedTrips == 1 ? " was" : "s were")} wasted on villages already served.",
            m.LiesBelieved == 0 ? "No truck acted on a lie." : $"Lies were acted on {m.LiesBelieved} time{(m.LiesBelieved == 1 ? "" : "s")}; {m.LyingRoadRepeats} of them a second time on the same road.",
        };
        if (m.Stranded > 0) lines.Add($"{m.Stranded} truck{(m.Stranded == 1 ? " is" : "s are")} stranded without fuel.");
        if (m.MessagesSent > 0) lines.Add($"{m.MessagesSent} messages were sent on the mesh and {m.MessagesLost} were lost; the ledgers {(m.LedgersIdentical ? "ended identical" : "ended different")} after the final sync.");
        return lines;
    }
}

/// <summary>Every truck's task: deliver supplies to every village; done when the truck reports, knowing all are served.</summary>
public static class ConvoyTask
{
    public static AgentTask For(RoadMap map)
    {
        var villages = map.Villages.Select(v => v.Id).ToList();
        double Served(IWorldView view) => villages.Count(v => view.Has($"served:{v}")) / (double)villages.Count;
        return new AgentTask("convoy", "deliver supplies to every village after the flood",
            facts => facts.Contains("convoy.done"), Served, Served, []) { ReleaseKind = "none" };
    }
}
