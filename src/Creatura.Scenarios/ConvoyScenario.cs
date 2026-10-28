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
/// </summary>
internal sealed class RoadDistrustExtension(ConvoyWorld world, string truck) : IAgentExtension
{
    private readonly HashSet<string> _roads = new(StringComparer.Ordinal);

    public string Name => "road-distrust";

    public IReadOnlyCollection<string> Roads => _roads;

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        if (verdict != ActionVerdict.Lied || !outcome.Succeeded || !tool.Id.StartsWith("drive:", StringComparison.Ordinal)) return;
        var road = tool.Id.Split(':')[1];
        if (!_roads.Add(road)) return;
        var r = world.Map.Road(road);
        world.Record(new ReplayEvent("distrust", $"{truck} stops trusting the road between {world.Name(r.A)} and {world.Name(r.B)}, both ways, for good.") { Truck = truck, Road = road });
    }

    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible) =>
        visible.Where(t => !(t.Id.StartsWith("drive:", StringComparison.Ordinal) && _roads.Contains(t.Id.Split(':')[1]))).ToList();

    public IEnumerable<string> PolicyNotes(TickContext context) => _roads.Select(r => $"avoid:{r}");
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
            var built = Build(spec, story);
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
                MessagesLost: mesh?.Lost ?? 0);
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

    private ReferenceLoop Loop(TruckSpec spec, StoryExtension story) => Build(spec, story).Loop;

    /// <summary>The agent for one truck and its definition for people, from one switch so the two always agree.</summary>
    internal (ReferenceLoop Loop, AgentDefinition Definition) Build(TruckSpec spec, StoryExtension story)
    {
        var task = ConvoyTask.For(Setup.Map);
        IPolicy policy = new ConvoyPolicy(Setup.Map, spec.Machine);
        ModelEndpoint? endpoint = null;
        if (Setup.SeatModel is { } seatModel)
        {
            endpoint = SeatFactory.Endpoint(seatModel, catalog);
            var model = new ModelPolicy(endpoint, models ?? new ModelClient(), calls, contextTag: $"convoy {Setup.Arm} seed {Setup.Seed} {spec.Name}");
            _seats.Add(model);
            policy = new ConvoyModelSeat(model, Setup.Map, spec.Machine, spec.Name);
        }
        if (recorder is not null) policy = recorder.Seat(policy, spec, Setup.Map);
        var observers = new List<IAgentExtension> { story };
        if (Setup.DistrustRoads) observers.Add(new RoadDistrustExtension(_world!, spec.Name));
        if (recorder is not null) observers.AddRange(recorder.Extensions(spec));
        var options = new AgentOptions(observers);
        var vessel = new Vessel { Model = endpoint?.Model ?? "convoy-planner", Physical = spec.Machine };
        var m = spec.Machine;
        var variable = spec.Name.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        var extensions = new List<string> { "StoryExtension: tells the replay about every action the control layer judged a lie" };
        if (Setup.DistrustRoads) extensions.Add("RoadDistrustExtension: a road caught lying is avoided both ways for the rest of the run");

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

        var policyCode = endpoint is null
            ? "new ConvoyPolicy(map, machine),      // the offline planner: no language model is called"
            : $"new ConvoyModelSeat(SeatFactory.Model(\"{endpoint.Name}\"), map, machine, \"{spec.Name}\"),  // {endpoint.Model} through {endpoint.ProviderName}, from models.json";
        var extensionCode = Setup.DistrustRoads
            ? $"AgentOptions.With(new StoryExtension(world, \"{spec.Name}\"), new RoadDistrustExtension(world, \"{spec.Name}\"))"
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
            endpoint is null
                ? "convoy-planner: an offline planner that knows roads, fuel and loads, and believes its sensors"
                : $"a language model: {endpoint.Name}, briefed on how the convoy works, choosing one tool per tick from the truck's facts and visible tools",
            endpoint is null
                ? "none: no language model is called in this scenario (decision D3)"
                : Describe(endpoint),
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
