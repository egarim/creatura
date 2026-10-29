using Creatura.Bench;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Step 6's second attempt: with the noise sealed, the truck is offered what fits where its position reads it is; the
/// memory of its own actions is the same in the loop and in an exported set; the fused check's memory rule ends its
/// repeats; and arms U2, V2 and P2 are scored as T11 to T11g say.
/// </summary>
public sealed class SealedWorldTests
{
    /// <summary>Over many first readings at the depot: sealed, a misread truck is offered the roads from the place it reads; unsealed, the roads from where it is.</summary>
    [Theory]
    [InlineData(ConvoyArm.U2, true)]
    [InlineData(ConvoyArm.U, false)]
    public void SealedOffersFollowTheReading(ConvoyArm arm, bool followsReading)
    {
        var misreads = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            var setup = ConvoySetup.For(arm, seed);
            var world = new ConvoyWorld(setup);
            var view = new TruckView(world, world.Trucks[0], setup, null, null, null);
            var read = view.Facts.Single(f => f.StartsWith("at:", StringComparison.Ordinal))["at:".Length..];
            if (read == world.Map.Depot) continue;
            misreads++;
            var from = followsReading ? read : world.Map.Depot;
            var drives = view.AvailableTools.Where(t => t.Id.StartsWith("drive:", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(drives);
            Assert.All(drives, t => Assert.True(world.Map.Road(t.Id.Split(':')[1]).Touches(from), $"{t.Id} does not leave {from}"));
            Assert.Equal(!followsReading, view.AvailableTools.Any(t => t.Id == "load"));
        }
        Assert.InRange(misreads, 20, 70);
    }

    [Fact]
    public void TheMemoryIsTheTrucksOwnLastActionAndItsTries()
    {
        var memory = new ActionMemory();
        Assert.Equal([0.0, 0.0], memory.For("drive:r12:alba"));
        memory.Record("drive:r12:alba", reported: true);
        Assert.Equal([1.0, 0.2], memory.For("drive:r12:alba"));
        Assert.Equal([0.0, 0.0], memory.For("drive:r01:j1"));
        memory.Record("drive:r12:alba", reported: false);
        Assert.Equal([0.0, 0.4], memory.For("drive:r12:alba"));
        for (var i = 0; i < 6; i++) memory.Record("wait", reported: true);
        Assert.Equal([1.0, 1.0], memory.For("wait"));
        Assert.False(memory.Again("drive:r12:alba"));
    }

    /// <summary>An exported set carries each truck's memory of its own actions, as the recorded decisions give it, in the order they ran.</summary>
    [Fact]
    public async Task AnExportedSetCarriesTheMemory()
    {
        var recorder = new ConvoyRecorder();
        await new ConvoyScenario(ConvoySetup.For(ConvoyArm.U2, 77), recorder: recorder).RunAsync();
        var path = Path.GetTempFileName();
        try
        {
            ConvoyDatasets.ExportWorld(recorder.Runs, path);
            var rows = File.ReadLines(path).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement).ToList();
            var ran = recorder.Runs.Single().Decisions.Where(d => d.Verdict is not null && d.ActionIndex is not null).ToList();
            Assert.Equal(ran.Count, rows.Count);
            for (var i = 0; i < ran.Count; i++)
            {
                var earlier = ran.Take(i).Where(d => d.Truck == ran[i].Truck).ToList();
                var again = earlier.Count > 0 && earlier[^1].Chosen == ran[i].Chosen && earlier[^1].Reported == true ? 1.0 : 0.0;
                var tries = Math.Min(earlier.Count(d => d.Chosen == ran[i].Chosen) / 5.0, 1);
                Assert.Equal([again, tries], rows[i].GetProperty("memory").EnumerateArray().Select(x => x.GetDouble()).ToArray());
            }
            Assert.Contains(rows, r => r.GetProperty("memory")[0].GetDouble() == 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The fused check's memory rule cuts the lying road's repeats in the sealed world: a lie met twice in a row is called at the second, so there is no third.</summary>
    [Fact]
    public async Task TheMemoryRuleCutsTheFusedChecksRepeats()
    {
        int without = 0, with = 0;
        for (var seed = 50; seed < 150; seed++)
        {
            without += (await new ConvoyScenario(ConvoySetup.For(ConvoyArm.V2, seed) with { FusedMemory = false }).RunAsync()).Measures.LyingRoadRepeats;
            with += (await new ConvoyScenario(ConvoySetup.For(ConvoyArm.V2, seed)).RunAsync()).Measures.LyingRoadRepeats;
        }
        Assert.True(with < without, $"repeats with memory {with}, without {without}");
    }

    [Theory]
    [InlineData(ConvoyArm.U2)]
    [InlineData(ConvoyArm.V2)]
    public async Task T11g_TheSealedArmsReplayByteForByte(ConvoyArm arm) => Assert.True(await ConvoyBattery.ReplaysMatchAsync(arm, 0));

    /// <summary>The C# judge with memory gives the probability the Python trainer gave, and the memory moves it.</summary>
    [Fact]
    public void TheSealedJudgeIsTheTrainersArithmetic()
    {
        var judge = ConvoyNetwork.LoadJudge(ConvoyNetwork.EmbeddedJudgeSealed, RoadMap.FloodedValley());
        Assert.Equal(ActionMemory.Names, judge.Memory);
        var a = Enumerable.Range(0, judge.Features.Count).Select(i => (i % 7) / 6.0).ToArray();
        var b = Enumerable.Range(0, judge.Features.Count).Select(i => ((i * 3) % 5) / 4.0).ToArray();
        // From tools/train/train_landed.py's score on models/convoy-judge-sealed.json.
        Assert.Equal(5.492503511619078e-07, judge.Score(a, 5, b, [0, 0]), 15);
        Assert.Equal(0.00010559485099074125, judge.Score(a, 5, b, [1, 0.4]), 12);
        Assert.Throws<ArgumentException>(() => judge.Score(a, 5, b));
    }

    [Fact]
    public void ArmP2CarriesTheRegisteredThresholds()
    {
        var p2 = ConvoySetup.For(ConvoyArm.P2, 1);
        Assert.Equal((ConvoyNetwork.EmbeddedJudgeSealed, 0.499305, 0.972585, true), (p2.LearnedJudge, p2.LearnedJudgeThreshold, p2.LearnedJudgeCondemn, p2.LearnedJudgeCheckableOnly));
        Assert.All(new[] { p2, ConvoySetup.For(ConvoyArm.U2, 1), ConvoySetup.For(ConvoyArm.V2, 1) }, s => Assert.Equal(SensorNoise.SealedRegistered, s.Noise));
        Assert.True(ConvoySetup.For(ConvoyArm.V2, 1).FusedMemory);
        Assert.Contains(ConvoyArm.P2, ConvoyBattery.DefaultArms);
    }

    /// <summary>In arm P2 the judge's memory is the truck's own: a lie tried again right after it reported arrival is judged with again = 1, and the judge's counts are the truth's.</summary>
    [Fact]
    public async Task InArmP2TheJudgeRemembers()
    {
        var run = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.P2, 77)).RunAsync();
        Assert.Contains(run.Ticks.SelectMany(t => t.Events), e => e.Kind == "surprised" && e.Text.Contains("lie judge", StringComparison.Ordinal));
        Assert.Equal((run.Measures.CheckFalseAlarms, run.Measures.CheckMisses), (run.Measures.JudgeFalseAlarms, run.Measures.JudgeMisses));
        // The first attempt took the lying road 76 times on this seed; with memory and loop data it is a handful.
        Assert.True(run.Measures.LiesBelieved < 10, $"{run.Measures.LiesBelieved} lies");
    }

    [Theory]
    [InlineData(ConvoyArm.P2)]
    public async Task T11g_ArmP2ReplaysByteForByte(ConvoyArm arm) => Assert.True(await ConvoyBattery.ReplaysMatchAsync(arm, 0));

    [Fact]
    public void TheSecondAttemptIsScoredAsRegistered()
    {
        static ConvoyMeasures M(int delivered, int served, int repeats = 0, int @checked = 0, int fa = 0, int misses = 0, int checkedLies = 0) =>
            new(delivered, 30, 0, 0, 2 + repeats, repeats, served, true, 0, 10, 0) { Checked = @checked, CheckFalseAlarms = fa, CheckMisses = misses, CheckedLies = checkedLies };
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>
        {
            [ConvoyArm.J] = [M(30, 20)],
            [ConvoyArm.U2] = [M(30, 30, 0, 200, 10, 0, 4)],
            [ConvoyArm.V2] = [M(30, 24, 1, 200, 2, 1, 4)],
            [ConvoyArm.P2] = [M(30, 23, 1, 200, 1, 1, 4)],
        };
        var verdicts = ConvoyBattery.Score(runs, true).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T11"].Verdict);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T11c"].Verdict);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T11d"].Verdict); // 1.0% against 1.5%
        Assert.Equal(HypothesisVerdict.Held, verdicts["T11e"].Verdict); // 1 repeat each
        Assert.Equal(HypothesisVerdict.Held, verdicts["T11f"].Verdict); // 1 of 200
        Assert.Equal(HypothesisVerdict.Held, verdicts["T11g"].Verdict);

        runs[ConvoyArm.U2] = [M(30, 30, 0, 200, 2, 0, 4)];
        runs[ConvoyArm.P2] = [M(29, 31, 2, 200, 2, 1, 4)];
        verdicts = ConvoyBattery.Score(runs, false).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T11"].Verdict); // 1% is not more than 1%
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T11c"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T11d"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T11e"].Verdict); // 2 repeats against 1
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T11f"].Verdict); // 2 of 200 is 1%
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T11g"].Verdict);
    }
}
