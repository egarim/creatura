using Creatura.Cells;

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
public sealed class ConvoyScenario(ConvoySetup setup)
{
    public ConvoySetup Setup { get; } = setup;

    public static ConvoyScenario Create(ConvoyArm arm, int seed) => new(ConvoySetup.For(arm, seed));

    public async Task<ConvoyRun> RunAsync(IScenarioObserver? observer = null, CancellationToken cancellationToken = default)
    {
        var world = new ConvoyWorld(Setup);
        var mesh = Setup.Ledger ? new ScenarioMesh(Setup.Seed * 7919 + 17, Setup.Loss, Setup.DelayChance) : null;
        using var owner = OwnerKey.Generate();

        var cells = new List<LocalCell>();
        var views = new List<TruckView>();
        var agents = new List<(ReferenceLoop Loop, StoryExtension Story)>();
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
            agents.Add((Loop(spec, story), story));
        }

        try
        {
            foreach (var view in views) view.Introduce();

            var header = Header(cells);
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
            var summary = new ReplaySummary(measures, Outcome(world, measures));
            observer?.End(summary);
            return new ConvoyRun(Setup, header, ticks, summary, finished.OfType<AgentRun>().ToList(), errors);
        }
        finally
        {
            foreach (var cell in cells) cell.Dispose();
        }
    }

    private ReferenceLoop Loop(TruckSpec spec, StoryExtension story)
    {
        var task = ConvoyTask.For(Setup.Map);
        var policy = new ConvoyPolicy(Setup.Map, spec.Machine);
        var options = AgentOptions.With(story);
        var vessel = new Vessel { Model = "convoy-planner", Physical = spec.Machine };
        return spec.Design switch
        {
            "levin" => new ReferenceLoop(new ColonyRegulator(Identity.Levin with { Vessel = vessel }, leak: 0.5), policy, task, options,
                ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, spec.Name),
            "bach" => new ReferenceLoop(new SelfModelRegulator(Identity.Bach with { Vessel = vessel }, stage: 3), policy, task, options, name: spec.Name),
            "peterson" => new ReferenceLoop(new MapKeeperRegulator(Identity.MapKeeper with { Vessel = vessel }), policy, task, options,
                ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }, spec.Name),
            _ => new ReferenceLoop(new PlainRegulator(new Identity("truck driver", "joche") { Vessel = vessel }), policy, task, options, name: spec.Name),
        };
    }

    private static string StopReason(AgentRun run)
    {
        if (run.GoalReached) return "every village is served, as far as it knows";
        var last = run.Trace.LastOrDefault();
        return last is null ? "its loop ended" : $"{last.Action} ({last.Detail})".Trim();
    }

    private ReplayHeader Header(IReadOnlyList<LocalCell> cells)
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

        return new ReplayHeader("convoy", Setup.Seed, Setup.Arm.ToString(), Setup.ArmText, Setup.MaxTicks,
            map.Nodes.Select(n => new ReplayNode(n.Id, n.Name, n.X, n.Y, n.Kind.ToString().ToLowerInvariant(), n.Demand)).ToList(),
            map.Roads.Select(r => new ReplayRoad(r.Id, r.A, r.B)).ToList(),
            Setup.Trucks.Select(t => new ReplayTruck(t.Name, cells.FirstOrDefault(c => c.Name == t.Name)?.Id ?? "", t.Design, t.Machine.Capacity, t.Machine.FuelCapacity, map.Depot)).ToList(),
            faults);
    }

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
