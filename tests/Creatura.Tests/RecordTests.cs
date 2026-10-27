using System.Text.Json;
using Creatura;
using Creatura.Hotz;

namespace Creatura.Tests;

/// <summary>
/// The record store (migration step 10): every stateful Core type round-trips through its snapshot, the whole
/// record round-trips through the JSON store, the loop loads before the run and saves after sleep, the
/// regulators keep their private parts, and the record extension is a reader and writer over the same store.
/// </summary>
public class RecordTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AgentRecord.Json);

    private static T Reparse<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), AgentRecord.Json)!;

    [Fact]
    public void TheSelfModelRoundTrips()
    {
        var model = Identity.Bach.NewSelfModel(3);
        model.Vessel = new Vessel { Model = "planner", Machine = "air" };
        model.RecordOutcome("apply-patch", true);
        model.RecordOutcome("apply-patch", false);
        model.RecordOutcome("deploy", true);
        model.StageLevel = 4;

        var restored = new SelfModel();
        restored.Restore(Reparse(model.Snapshot()));

        Assert.Equal(Json(model.Snapshot()), Json(restored.Snapshot()));
        Assert.Equal(model.Render(), restored.Render());
        Assert.Equal(0.5, restored.Reliability("apply-patch"));
    }

    [Fact]
    public void ThePredictionTrackerRoundTripsThroughSeed()
    {
        var tracker = new PredictionTracker();
        foreach (var r in new[] { true, true, false, true, true, true, true, true, true, false }) tracker.Record(r);

        var restored = new PredictionTracker();
        restored.Restore(Reparse(tracker.Snapshot()));

        Assert.Equal(Json(tracker.Snapshot()), Json(restored.Snapshot()));
        Assert.Equal(tracker.RecentAccuracy, restored.RecentAccuracy);
        Assert.Equal(8, restored.Recent.Count);
        Assert.Equal(tracker.EarnedNextStage, restored.EarnedNextStage);
    }

    [Fact]
    public void TheCoherenceAuditRoundTrips()
    {
        var audit = new CoherenceAudit();
        audit.Claim();
        audit.Claim();
        audit.Accept("apply-patch produces patched", false, "reported success with no effect", 3);

        var restored = new CoherenceAudit();
        restored.Restore(Reparse(audit.Snapshot()));

        Assert.Equal(Json(audit.Snapshot()), Json(restored.Snapshot()));
        Assert.Equal(0.5, restored.Score);
        Assert.Equal(audit.Contradictions, restored.Contradictions);
    }

    [Fact]
    public void TheEffectMemoryRoundTrips()
    {
        var memory = new EffectMemory();
        var facts = new HashSet<string>(["a", "b"], StringComparer.Ordinal);
        memory.Observe("t1", facts, new ActionOutcome("t1", true, ["c"], "something changed"), ["c"]);
        memory.Observe("t1", new HashSet<string>(["a"], StringComparer.Ordinal), new ActionOutcome("t1", true, ["c"], "something changed"), ["c"]);
        memory.Observe("t2", facts, new ActionOutcome("t2", true, [], "reported success"), []);
        memory.Observe("t3", facts, new ActionOutcome("t3", false, [], "nothing happened"), []);

        var restored = new EffectMemory();
        restored.Restore(Reparse(memory.Snapshot()));

        Assert.Equal(Json(memory.Snapshot()), Json(restored.Snapshot()));
        Assert.Equal(["a"], restored.Learned(new Tool("t1", [], [], "write")).Requires);
        Assert.Equal(["c"], restored.Learned(new Tool("t1", [], [], "write")).Grants);
        Assert.Equal(1, restored.Entries["t2"].Lied);
        Assert.Equal(1, restored.Entries["t3"].Failed);
        Assert.Null(restored.Entries["t3"].RequiresGuess);
    }

    [Fact]
    public void TheDreamJournalRoundTrips()
    {
        var journal = new DreamJournal { Nights = 2, DreamedRoutes = 5, Confirmed = 3, Tested = 4 };
        journal.Prefer.Add("b");
        journal.Prefer.Add("a");
        journal.Avoid.Add("z");

        var restored = new DreamJournal();
        restored.Restore(Reparse(journal.Snapshot()));

        Assert.Equal(Json(journal.Snapshot()), Json(restored.Snapshot()));
        Assert.Equal(0.75, restored.Trust);
        Assert.Equal(["a", "b"], restored.Prefer.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTerritoryMapRoundTripsWithItsRules()
    {
        var map = new TerritoryMap();
        var story = new Story { Name = "root", ShouldBe = _ => true };
        story.Used.AddRange(["a", "b"]);
        map.Closed(story);
        map.Episodes.Add(new Episode(3, "b", "root", 0.5, RetryLanded: false));
        map.Episodes.Add(new Episode(5, "c", "sub", 0.25, RetryLanded: null));
        map.Unknown.Add("b");
        map.Unreachable.Add("c");
        map.Dead.Add("fact.x");
        for (var i = 0; i < TerritoryMap.RuleAfter; i++) map.Lied("b");
        map.Lied("c");

        var restored = new TerritoryMap();
        restored.Restore(Reparse(map.Snapshot()));

        Assert.Equal(Json(map.Snapshot()), Json(restored.Snapshot()));
        Assert.Equal(map.Rules(), restored.Rules());
        Assert.Equal(["b cannot be relied on: found lying 3 times"], restored.Rules());
        Assert.Equal(1, restored.Unreliability("c"));
        Assert.Equal(["a", "b"], restored.Procedures["root"]);
    }

    [Fact]
    public void TheInterruptBudgetRoundTripsItsRecordNotItsConfiguration()
    {
        var budget = new InterruptBudget { Threshold = 0.9, Warmup = 2 };
        budget.Record(true);
        budget.Record(false);
        budget.Record(true);

        var restored = new InterruptBudget { Threshold = 0.5, Warmup = 0 };
        restored.Restore(Reparse(budget.Snapshot()));

        Assert.Equal(Json(budget.Snapshot()), Json(restored.Snapshot()));
        Assert.Equal((3, 2, 1), (restored.Raised, restored.Upheld, restored.Dismissed));
        Assert.Equal(0.5, restored.Threshold);
    }

    [Fact]
    public void TheWholeRecordRoundTripsThroughTheJsonStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "creatura-records-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonRecordStore(directory);
            var record = new AgentRecord
            {
                Cases = 3,
                StageLevel = 4,
                Skills = { ["apply-patch"] = new SkillBelief("apply-patch", 2, 3) },
                RecentErrors = ["apply-patch did not do what I expected"],
                Predictions = new PredictionSnapshot(5, 6, [true, false, true]),
                Coherence = new CoherenceSnapshot(6, [new Contradiction(2, "claim", "evidence")]),
                Effects = new EffectMemorySnapshot(new Dictionary<string, EffectEntrySnapshot> { ["t"] = new(1, 1, 0, 0, ["x"], ["y"]) }),
                Dreams = new DreamSnapshot(["a"], ["b"], 1, 2, 1, 2),
                Territory = new TerritorySnapshot(new Dictionary<string, List<string>> { ["root"] = ["a"] }, [new Episode(1, "a", "root", 0.5, true)], ["u"], ["r"], ["d"], new Dictionary<string, int> { ["u"] = 3 }),
                Interrupts = new InterruptSnapshot(4, 3, 1),
            };
            record.SetPart(HybridRegulator.CellPart, Identity.Cell.NewSelfModel(1).Snapshot());
            record.SetPart(OrientingRegulator.RestingInhibitionPart, 0.11);

            store.Save("hybrid/case", record);
            var loaded = store.Load("hybrid/case");

            Assert.NotNull(loaded);
            Assert.Equal("hybrid/case", loaded.Key);
            Assert.Equal(AgentRecord.CurrentSchema, loaded.Schema);
            Assert.Equal(record.ToJson(), loaded.ToJson());
            Assert.Equal(0.11, loaded.GetPart<double>(OrientingRegulator.RestingInhibitionPart));
            Assert.Equal("effect cell", loaded.GetPart<SelfModelSnapshot>(HybridRegulator.CellPart)!.Role);
            // One file per key, the key made safe for a file name; a key never saved loads as nothing.
            Assert.Single(Directory.GetFiles(directory));
            Assert.Equal("hybrid_case.json", Path.GetFileName(store.PathFor("hybrid/case")));
            Assert.Null(store.Load("nobody"));
            // Quarantine is deliberately not in the record.
            Assert.DoesNotContain("uarantine", loaded.ToJson());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TheInMemoryStoreHandsOutCopies()
    {
        var store = new InMemoryRecordStore();
        var record = new AgentRecord { Cases = 1 };
        store.Save("k", record);
        record.Cases = 99;
        record.Skills["late"] = new SkillBelief("late", 1, 1);

        var loaded = store.Load("k")!;
        Assert.Equal(1, loaded.Cases);
        Assert.Empty(loaded.Skills);
        loaded.Cases = 50;
        Assert.Equal(1, store.Load("k")!.Cases);
        Assert.Throws<FormatException>(() => AgentRecord.Parse("""{"Schema": 99}"""));
    }

    [Fact]
    public async Task TheLoopLoadsBeforeTheRunAndSavesAfterSleepAndTheRunCarriesItsRecord()
    {
        var store = new InMemoryRecordStore();
        var loop = new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), layer: ControlLayerOptions.Design, name: "Bach", store: store, recordKey: "bach/restore");
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");

        var run = await loop.RunAsync(world, 30);

        Assert.True(run.GoalReached);
        var record = Assert.IsType<AgentRecord>(run.Record);
        Assert.Equal("bach/restore", record.Key);
        Assert.Equal(1, record.Cases);
        Assert.Equal(new SkillBelief("apply-patch", 0, 1), record.Skills["apply-patch"]);
        Assert.Equal(record.Predictions!.Total, run.Trace.Count(e => e.Level == "cell"));
        Assert.Single(record.Coherence!.Contradictions);
        Assert.Equal(record.ToJson(), store.Load("bach/restore")!.ToJson());

        // The Core carry is the extension's job, not the loop's: without one the design starts its second run from its own record,
        // and only the case count moves.
        var second = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Equal(2, second.Record!.Cases);
        Assert.DoesNotContain(second.Notes, n => n.StartsWith("record carried", StringComparison.Ordinal));
        Assert.All(second.Record.Skills.Values, s => Assert.Equal(s.Attempted, s.Supported)); // the carried lie is not in this run's skills
        Assert.All(second.Record.Predictions!.Recent, r => Assert.True(r)); // no lie this run, and the window holds this run's landings only
    }

    [Fact]
    public async Task TheHybridKeepsItsCellAsAPrivatePartAndFindsItFullNextCase()
    {
        var store = new InMemoryRecordStore();
        var regulator = new HybridRegulator(Identity.Hybrid, Identity.Cell, stage: 3);
        var policy = new RenderSpy();
        var loop = new ReferenceLoop(regulator, policy, layer: ControlLayerOptions.Design, name: "Hybrid colony", store: store);

        var first = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.DoesNotContain("cell record", policy.FirstRender);
        var cell = first.Record!.GetPart<SelfModelSnapshot>(HybridRegulator.CellPart);
        Assert.NotNull(cell);
        Assert.Equal("effect cell", cell.Role);
        Assert.Equal(regulator.Cell.Skills.Keys.Order(StringComparer.Ordinal), cell.Skills.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["propose: keep the contract set point as it is"], first.Proposals);

        // The cell comes back whole before the layer is built: the seat's first render shows it, and the lazy organism-to-cell copy leaves it alone.
        policy.FirstRender = null;
        var second = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Contains("cell record, how often each tool's effect landed when it said so:", policy.FirstRender);
        Assert.Equal(2, second.Record!.Cases);
        Assert.Equal(2, second.Record.GetPart<SelfModelSnapshot>(HybridRegulator.CellPart)!.Skills["apply-patch"].Attempted);
    }

    [Fact]
    public async Task TheOrientingAgentCarriesItsRestingInhibition()
    {
        var store = new InMemoryRecordStore();
        var regulator = new OrientingRegulator(Identity.Orienting);
        var loop = new ReferenceLoop(regulator, layer: ControlLayerOptions.Design with { JudgeRule = JudgeRule.ThreeWay }, name: "orienting agent", store: store);

        var first = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Equal(0.2, first.Record!.GetPart<double>(OrientingRegulator.RestingInhibitionPart), 6);
        Assert.Contains(first.Notes, n => n.EndsWith("resting inhibition 0.20", StringComparison.Ordinal));

        // Courage earned elsewhere arrives through the store and is what the next run settles to.
        var record = store.Load("orienting agent")!;
        record.SetPart(OrientingRegulator.RestingInhibitionPart, 0.11);
        store.Save("orienting agent", record);
        var second = await loop.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Equal(0.11, regulator.Affect.RestingI, 6);
        Assert.Contains(second.Notes, n => n.EndsWith("resting inhibition 0.11", StringComparison.Ordinal));
        Assert.Equal(0.11, second.Record!.GetPart<double>(OrientingRegulator.RestingInhibitionPart), 6);
    }

    [Fact]
    public async Task TheMapKeeperStoresItsTerritoryAndCountsItsExplorations()
    {
        var store = new InMemoryRecordStore();
        var memory = new EffectMemory();
        var regulator = new MapKeeperRegulator();
        var loop = new ReferenceLoop(regulator, layer: ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }, name: "Peterson", store: store);
        var world = new LearnedWorld(new OpaqueWorld(RestoreServiceDomain.NewWorld()), memory);

        var run = await loop.RunAsync(world, 40);

        Assert.True(run.Counters.Explorations > 0);
        Assert.Equal(run.Counters.Explorations, run.Trace.Count(e => e.Level == "tissue" && e.Detail.Contains("unexplored territory:", StringComparison.Ordinal)));
        var territory = run.Record!.Territory;
        Assert.NotNull(territory);
        Assert.Equal(Json(territory), Json(store.Load("Peterson")!.Territory));
        Assert.Equal(Json(regulator.Map.Snapshot()), Json(territory));
        // Without the map the patch reads as a liar twice over: the territory the run leaves says so, and that is what a next case would load.
        Assert.Contains("apply-patch", territory.Unknown);
        Assert.Equal(2, territory.Episodes.Count);
        Assert.Equal(2, territory.Unreliable["apply-patch"]);
    }

    [Fact]
    public async Task TheExplorationExtensionCountsThisRunsTriesOnTheRun()
    {
        var memory = new EffectMemory();
        var explorer = new ExplorationExtension(memory, scaled: true);
        var loop = new ReferenceLoop(new ColonyRegulator(Identity.Levin, leak: 0.5), options: AgentOptions.With(new HonestFailureExtension(), explorer),
            layer: ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, name: "Levin");

        var first = await loop.RunAsync(new LearnedWorld(new OpaqueWorld(RestoreServiceDomain.NewWorld()), memory), 40);
        Assert.True(first.Counters.ExploratoryTries > 0);
        Assert.Equal(explorer.ExploratoryTries, first.Counters.ExploratoryTries);

        var second = await loop.RunAsync(new LearnedWorld(new OpaqueWorld(RestoreServiceDomain.NewWorld()), memory), 40);
        // The instance keeps counting across runs; each run's counters hold that run's tries alone.
        Assert.Equal(explorer.ExploratoryTries, first.Counters.ExploratoryTries + second.Counters.ExploratoryTries);
        Assert.Equal(0, second.Counters.Explorations);
    }

    [Fact]
    public async Task TheRecordExtensionSeedsBeforeTheFirstTickAndWritesTheStoreAtSleep()
    {
        var directory = Path.Combine(Path.GetTempPath(), "creatura-records-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonRecordStore(directory);
            var tickSeen = new SeedTickSpy();
            var first = new RecordCarryExtension(store, "bach");
            var run1 = await Bach(AgentOptions.With(first, tickSeen)).RunAsync(Licence(), 20);
            Assert.False(run1.GoalReached);
            Assert.Contains(run1.Trace, e => e.Action == TraceText.DeployBlocked);
            Assert.Equal(1, first.Cases);
            Assert.Equal(3, first.Stage);
            Assert.Equal(2, first.Skills.Count);
            Assert.Equal(1.0, first.Reliability("check-file"));
            Assert.Equal(0.5, first.Reliability("never-tried"));
            Assert.Equal(first.Record.ToJson(), store.Load("bach")!.ToJson());

            // A new instance over the same file: the record arrives at tick 0, before anything is visible, and earns the release.
            Assert.Equal(2, tickSeen.TickWhenSkillsAppeared); // case 1: the first skill was earned at tick 2, nothing was carried
            var second = new RecordCarryExtension(store, "bach");
            var seededAt = new SeedTickSpy();
            var run2 = await Bach(AgentOptions.With(second, seededAt)).RunAsync(Licence(), 20);
            Assert.True(run2.GoalReached);
            Assert.Contains("record carried from 1 earlier case(s): 2 skills, 2 predictions", run2.Notes);
            Assert.Equal(0, seededAt.TickWhenSkillsAppeared); // case 2: the record was there before the first tick
            Assert.Equal(4, second.Stage);
            Assert.Equal(2, second.Cases);
            Assert.Equal(2, store.Load("bach")!.Cases);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        static ReferenceLoop Bach(AgentOptions options) =>
            new(new SelfModelRegulator(Identity.Bach, stage: 3), new PlannerPolicy(), LicenceTask, options, ControlLayerOptions.Design, "Bach agent");
    }

    [Fact]
    public async Task TheRecordExtensionAndTheLoopCanShareOneStoreWithoutCountingTwice()
    {
        var store = new InMemoryRecordStore();
        var record = new RecordCarryExtension(store, "shared");
        var loop = new ReferenceLoop(new HybridRegulator(), new PlannerPolicy(), LicenceTask, AgentOptions.With(record), ControlLayerOptions.Design, "Hybrid colony", store, "shared");

        var run1 = await loop.RunAsync(Licence(), 20);
        var run2 = await loop.RunAsync(Licence(), 20);

        Assert.Equal(1, run1.Record!.Cases);
        Assert.Equal(2, run2.Record!.Cases);
        Assert.Equal(2, record.Cases);
        var kept = store.Load("shared")!;
        Assert.Equal(2, kept.Cases);
        Assert.True(kept.HasPart(HybridRegulator.CellPart));
        Assert.True(kept.HasPart(RecordCarryExtension.LastTriedPart));
        Assert.True(run2.GoalReached);
    }

    [Fact]
    public async Task TheColonyKeepsItsRecordAsNotesAndTheColonyChangesNothing()
    {
        var record = new RecordCarryExtension();
        var loop = new ReferenceLoop(new ColonyRegulator(Identity.Levin, leak: 0.5), new PlannerPolicy(), LicenceTask, AgentOptions.With(record),
            ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, "Levin colony");
        var run1 = await loop.RunAsync(Licence(), 20);
        var run2 = await loop.RunAsync(Licence(), 20);

        Assert.True(run1.GoalReached && run2.GoalReached);
        Assert.Equal(run1.Trace, run2.Trace);
        Assert.Equal(4, record.Skills.Count);
        Assert.Equal(2, record.Skills["notify"].Attempted);
        Assert.Equal(0, record.Stage);
    }

    // ---- The licence case of the research repository's battery 2, spelled out here so the test owes nothing to it ----

    private static readonly AgentTask LicenceTask = new(
        "Licence",
        "Issue the licence and notify the citizen.",
        f => f.Contains("licence.issued") && f.Contains("citizen.notified"),
        w => new[] { "file.ok", "fee.paid", "licence.issued", "citizen.notified" }.Count(w.Has) / 4.0,
        w => new[] { "file.ok", "fee.paid", "licence.issued", "citizen.notified" }.Count(w.Has) / 4.0,
        []);

    private static World Licence() => new(
    [
        new Tool("check-file", ["request.filed"], ["file.ok"], "read"),
        new Tool("take-payment", ["file.ok"], ["fee.paid"], "write"),
        new Tool("issue-licence", ["fee.paid"], ["licence.issued"], "release"),
        new Tool("notify", ["licence.issued"], ["citizen.notified"], "contact"),
    ], ["request.filed"]);

    private sealed class SeedTickSpy : IAgentExtension
    {
        public string Name => "seed-spy";

        public int TickWhenSkillsAppeared { get; private set; } = -1;

        public void BeforeRun(TickContext context) => Seen(context);

        public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
        {
            Seen(context);
            return visible;
        }

        private void Seen(TickContext context)
        {
            if (TickWhenSkillsAppeared < 0 && context.SelfModel is { Skills.Count: > 0 }) TickWhenSkillsAppeared = context.Tick;
        }
    }

    private sealed class RenderSpy : IPolicy
    {
        private readonly PlannerPolicy _planner = new();

        public string Name => "render-spy";

        public string? FirstRender { get; set; }

        public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
        {
            FirstRender ??= context.SelfModel;
            return _planner.NextAsync(context, cancellationToken);
        }
    }
}
