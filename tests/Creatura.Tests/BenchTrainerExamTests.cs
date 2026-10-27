using Creatura.Bench;
using Creatura.Worlds;

namespace Creatura.Tests;

/// <summary>
/// Train and test (migration step 14, third commit): a trainer over the licence curriculum carries the record and the
/// staged designs release by case 2 (the H5 shape); an exam loads the record once and never writes; the frozen store
/// refuses every save and hands out copies.
/// </summary>
public class BenchTrainerExamTests
{
    [Theory]
    [InlineData("bach")]
    [InlineData("hybrid")]
    public async Task TheTrainerCarriesTheRecordAndTheStagedDesignReleasesByCaseTwo(string design)
    {
        var store = new InMemoryRecordStore();
        var report = await Trainer.RunAsync(design, Curriculum.Licence(4), store, "trained");

        Assert.Equal(4, report.Cases.Count);
        // Case 1: two predictions do not earn the release; the design refuses it and hands off.
        Assert.Equal(OutcomeClass.HandedOff, report.Cases[0].Outcome);
        Assert.True(report.Cases[0].Row.DeployBlocked > 0);
        // Case 2: the carried record earns it.
        Assert.Equal(2, report.FirstRealCase);
        Assert.All(report.Cases.Skip(1), c => Assert.Equal(OutcomeClass.Real, c.Outcome));
        Assert.Contains(report.Cases[1].Run.Notes, n => n.StartsWith("record carried from 1 earlier case", StringComparison.Ordinal));

        var record = store.Load("trained");
        Assert.NotNull(record);
        Assert.Equal(4, record!.Cases);
        Assert.Equal(4, record.StageLevel);
        Assert.Equal(record.Cases, report.Record.Cases);
        Assert.Equal("licence", report.Rows[0].Test);
        Assert.Equal("record", report.Rows[0].Arm);
        Assert.Equal(21, LegacyCsv.Split(report.Rows[0].ToLegacyCsv(LegacyShape.BatteryResults)).Count);
    }

    [Fact]
    public async Task TheTrainerWorksForAPlainFactoryThatCarriesTheRecordThroughTheExtensionAlone()
    {
        var store = new InMemoryRecordStore();
        AgentFactory make = AgentCatalog.Find("bach").Make;
        var report = await Trainer.RunAsync(make, Curriculum.Licence(3), store, "plain");
        Assert.Equal(2, report.FirstRealCase);
        Assert.Equal(3, store.Load("plain")!.Cases);
    }

    [Fact]
    public async Task TheColonyDoesNotImproveFromItsRecord()
    {
        // H5's other half: the colony keeps no stage, so a short case with a release reads the same every time.
        var store = new InMemoryRecordStore();
        var report = await Trainer.RunAsync("levin", Curriculum.Licence(3), store, "colony");
        Assert.All(report.Cases, c => Assert.Equal(report.Cases[0].Outcome, c.Outcome));
        Assert.Equal(3, store.Load("colony")!.Cases);
    }

    [Fact]
    public async Task AnExamLoadsTheRecordOnceAndNeverWrites()
    {
        var store = new InMemoryRecordStore();
        await Trainer.RunAsync("bach", Curriculum.Licence(3), store, "examinee");
        var before = store.Load("examinee")!.ToJson();

        // The canonical exam: panic on a carried record, beside a plain licence case, two repeats each.
        var battery = new Battery("exam", "test",
            [new Hypothesis("X1", "the examinee finishes the licence case on its carried stage", "it refuses the release", rows =>
                rows.Where(r => r.Test == "licence").All(r => r.Outcome == OutcomeClass.Real) ? HypothesisVerdict.Held : HypothesisVerdict.Failed)],
            [
                new Arm("panic", "bach", "permit") { EveryToolLiesOnce = true, Test = "panic" },
                new Arm("carried", "bach", "licence") { Test = "licence" },
            ],
            Repeats: 2);
        var report = await Exam.RunAsync(battery, store, "examinee");

        Assert.Equal(4, report.Runs.Count);
        Assert.True(report.SavesRefused > 0, "the loop and the record extension both tried to save; the wrapper refused each");
        Assert.Equal(before, store.Load("examinee")!.ToJson());
        Assert.Equal(3, report.Record!.Cases);
        // Every run started from the same frozen record: the carried note names three cases every time, never four.
        Assert.All(report.Runs, r => Assert.Contains(r.Run.Notes, n => n.StartsWith("record carried from 3 earlier case", StringComparison.Ordinal)));
        Assert.Equal(HypothesisVerdict.Held, report["X1"]);
        Assert.All(report.Runs.Where(r => r.Arm.Test == "licence"), r => Assert.Equal(OutcomeClass.Real, r.Outcome));
        Assert.Contains(report.Runs, r => r.Arm.Test == "panic" && r.Row.LiesCaught > 0);
        Assert.Contains("X1   held", report.Lines().First());
    }

    [Fact]
    public async Task AnExamRunsASeriesArmThroughTheSeries()
    {
        var store = new InMemoryRecordStore();
        var battery = new Battery("series", "test", [SokolovBattery.Battery.Find("S1")],
            [new Arm("opaque", "levin", "permit") { Opaque = true, Extensions = ["honest", "explore:scaled"], Protocol = Protocol.Series, MaxTicks = 120, Test = "ingenuity" }],
            Repeats: 1);
        var report = await Exam.RunAsync(battery, store, "nobody");
        var row = Assert.Single(report.Rows);
        Assert.NotNull(row.RoutesFound);
        Assert.NotNull(row.RoutesExist);
        Assert.True(row.RoutesExist > 0);
        Assert.True(report.SavesRefused > 0, "every run of the series tried to save; the wrapper refused each");
        Assert.Null(report.Record);
    }

    [Fact]
    public void TheFrozenStoreHandsOutCopiesAndRefusesSaves()
    {
        var inner = new InMemoryRecordStore();
        inner.Save("k", new AgentRecord { Cases = 2, StageLevel = 4 });
        var frozen = new ReadOnlyRecordStore(inner);

        var first = frozen.Load("k")!;
        first.Cases = 99;
        frozen.Save("k", first);
        inner.Save("k", new AgentRecord { Cases = 7 });

        Assert.Equal(1, frozen.SavesRefused);
        Assert.Equal(2, frozen.Load("k")!.Cases);
        Assert.Equal(2, frozen.Frozen!.Cases);
        Assert.Equal(7, inner.Load("k")!.Cases);
        Assert.Null(new ReadOnlyRecordStore(new InMemoryRecordStore()).Load("none"));
    }

    [Fact]
    public async Task ACatalogueBuildHonoursTheLayerAndRefusesItForAControl()
    {
        var store = new InMemoryRecordStore();
        var build = new AgentBuild(new PlannerPolicy(), WorldCatalog.Permit.Task, AgentOptions.None, Layer: ControlLayerOptions.Design with { JudgeRule = JudgeRule.ThreeWay }, Store: store, RecordKey: "layered");
        var agent = AgentCatalog.Make("bach", build);
        var run = await agent.RunAsync(WorldCatalog.Permit.NewWorld(WorldCatalog.Permit.Set("none")), 30);
        Assert.True(run.GoalReached);
        Assert.NotNull(store.Load("layered"));
        Assert.Throws<ArgumentException>(() => AgentCatalog.Make("careless", build));
        Assert.NotNull(AgentCatalog.Make("careless", build with { Layer = null, Store = null }));
    }
}
