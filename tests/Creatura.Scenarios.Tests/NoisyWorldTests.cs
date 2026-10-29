using Creatura.Bench;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Step 6 of docs/design-trained-models.md, the noisy world: the readings are noisy at the registered levels and nothing
/// else is, the world's truth is kept beside every verdict, the fused check is the one registered, and arms U and V are
/// scored as T10 to T10g say.
/// </summary>
public sealed class NoisyWorldTests
{
    /// <summary>A truck's first reading at the depot, over many seeds: how often the position and the gauge are off.</summary>
    private static (double Misread, double Low, double High) FirstReadings(ConvoyArm arm, int seeds)
    {
        int misread = 0, low = 0, high = 0;
        for (var seed = 0; seed < seeds; seed++)
        {
            var setup = ConvoySetup.For(arm, seed);
            var world = new ConvoyWorld(setup);
            var truck = world.Trucks.First(t => t.Name != setup.Faults.GaugeLiar);
            var view = new TruckView(world, truck, setup, null, null, null);
            if (!view.Has($"at:{world.Map.Depot}")) misread++;
            if (view.Has($"fuel:{truck.Fuel - 1}")) low++;
            if (view.Has($"fuel:{truck.Fuel + 1}")) high++;
        }
        return (misread / (double)seeds, low / (double)seeds, high / (double)seeds);
    }

    [Fact]
    public void TheReadingsAreNoisyAtTheRegisteredLevels()
    {
        Assert.Equal(new SensorNoise(0.10, 0.15), SensorNoise.Registered);
        var (misread, low, high) = FirstReadings(ConvoyArm.U, 4000);
        Assert.InRange(misread, 0.085, 0.115);
        Assert.InRange(low, 0.13, 0.17);
        Assert.InRange(high, 0.13, 0.17);
    }

    [Fact]
    public void WithoutNoiseEveryReadingIsTheTruth()
    {
        Assert.Null(ConvoySetup.For(ConvoyArm.J, 1).Noise);
        Assert.Equal((0.0, 0.0, 0.0), FirstReadings(ConvoyArm.J, 300));
    }

    /// <summary>In the clean world the effect check is exact: arm J's verdicts have no false alarm and no miss against the truth.</summary>
    [Fact]
    public async Task InTheCleanWorldTheCheckMatchesTheTruth()
    {
        var run = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.J, 3)).RunAsync();
        Assert.True(run.Measures.Checked > 30);
        Assert.True(run.Measures.CheckedLies > 0);
        Assert.Equal((0, 0), (run.Measures.CheckFalseAlarms, run.Measures.CheckMisses));
    }

    /// <summary>The recorder keeps the world's truth beside the layer's verdict, and with noise the two part ways.</summary>
    [Fact]
    public async Task TheTruthIsRecordedBesideTheVerdict()
    {
        var recorder = new ConvoyRecorder();
        var run = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.U, 51), recorder: recorder).RunAsync();
        var decisions = recorder.Runs.Single().Decisions.Where(d => d.Verdict is not null).ToList();
        Assert.All(decisions, d => Assert.Contains(d.Truth, new[] { "Landed", "Lied", "HonestFailure" }));
        Assert.Contains(decisions, d => d.Truth == "Lied" && d.Chosen!.StartsWith("drive:", StringComparison.Ordinal));
        Assert.Contains(decisions, d => d.Truth == "Landed" && d.Verdict == "Lied");
        Assert.True(run.Measures.CheckFalseAlarms > 0);
    }

    /// <summary>The fused check cries wolf less than the effect check in the same noise, and still catches lies.</summary>
    [Fact]
    public async Task TheFusedCheckRaisesFewerFalseAlarms()
    {
        int u = 0, v = 0, vMisses = 0, vLies = 0;
        for (var seed = 60; seed < 70; seed++)
        {
            var noisy = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.U, seed)).RunAsync();
            var fused = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.V, seed)).RunAsync();
            u += noisy.Measures.CheckFalseAlarms;
            v += fused.Measures.CheckFalseAlarms;
            vMisses += fused.Measures.CheckMisses;
            vLies += fused.Measures.CheckedLies;
        }
        Assert.True(v * 3 < u, $"fused {v} false alarms, effect check {u}");
        Assert.True(vMisses * 2 < vLies, $"fused missed {vMisses} of {vLies} lies");
    }

    [Theory]
    [InlineData(ConvoyArm.U)]
    [InlineData(ConvoyArm.V)]
    public async Task T10g_TheNoisyArmsReplayByteForByte(ConvoyArm arm) => Assert.True(await ConvoyBattery.ReplaysMatchAsync(arm, 0));

    /// <summary>The C# lie judge gives the probability the Python trainer gave for the same inputs.</summary>
    [Fact]
    public void TheLieJudgeIsTheTrainersArithmetic()
    {
        var judge = ConvoyNetwork.LoadJudge(ConvoyNetwork.EmbeddedJudgeNoisy, RoadMap.FloodedValley());
        var a = Enumerable.Range(0, judge.Features.Count).Select(i => (i % 7) / 6.0).ToArray();
        var b = Enumerable.Range(0, judge.Features.Count).Select(i => ((i * 3) % 5) / 4.0).ToArray();
        // From tools/train/train_landed.py's score on models/convoy-judge-noisy.json.
        Assert.Equal(2.1410099058544118e-08, judge.Score(a, 5, b), 15);
        Assert.Equal(4.784625793115855e-10, judge.Score(b, 0, a), 15);
    }

    [Fact]
    public void AJudgeAndAWorldModelAreNotTakenForEachOther()
    {
        var models = Path.Combine(Repo, "models");
        Assert.Throws<FormatException>(() => Creatura.Seats.WorldModel.Load(Path.Combine(models, "convoy-judge-noisy.json")));
        Assert.Throws<FormatException>(() => Creatura.Seats.LieJudge.Load(Path.Combine(models, "convoy-world-noisy.json")));
    }

    [Fact]
    public void ArmsFAndPCarryTheRegisteredThresholds()
    {
        var f = ConvoySetup.For(ConvoyArm.F, 1);
        var p = ConvoySetup.For(ConvoyArm.P, 1);
        Assert.Equal((ConvoyNetwork.EmbeddedWorldNoisy, 10.82, 12.534, true), (f.LearnedJudge, f.LearnedJudgeThreshold, f.LearnedJudgeCondemn, f.LearnedJudgeCheckableOnly));
        Assert.Equal((ConvoyNetwork.EmbeddedJudgeNoisy, 0.555544, 0.955596, true), (p.LearnedJudge, p.LearnedJudgeThreshold, p.LearnedJudgeCondemn, p.LearnedJudgeCheckableOnly));
        Assert.All(new[] { f, p, ConvoySetup.For(ConvoyArm.U, 1), ConvoySetup.For(ConvoyArm.V, 1) }, s => Assert.Equal(SensorNoise.Registered, s.Noise));
        // T10b failed, so arm P is not in the default battery; F, U and V are.
        Assert.DoesNotContain(ConvoyArm.P, ConvoyBattery.DefaultArms);
        Assert.Contains(ConvoyArm.F, ConvoyBattery.DefaultArms);
    }

    /// <summary>In arm P the lie judge's verdict is the one acted on, and its misses are counted against the truth (training seed 77, where it misses a lying road again and again).</summary>
    [Fact]
    public async Task InArmPTheLieJudgeDecides()
    {
        var run = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.P, 77)).RunAsync();
        Assert.Contains(run.Ticks.SelectMany(t => t.Events), e => e.Kind == "surprised" && e.Text.Contains("lie judge", StringComparison.Ordinal));
        Assert.True(run.Measures.JudgeMisses > 10);
        Assert.True(run.Measures.LyingRoadRepeats > 10);
        Assert.Equal(run.Measures.CheckMisses, run.Measures.JudgeMisses);

        // Its own false alarms and misses are counted against the truth, not the noisy check: on checkable actions its
        // verdict is the final one, so they are the tally's.
        for (var seed = 60; seed < 66; seed++)
        {
            var m = (await new ConvoyScenario(ConvoySetup.For(ConvoyArm.P, seed)).RunAsync()).Measures;
            Assert.Equal((m.CheckFalseAlarms, m.CheckMisses), (m.JudgeFalseAlarms, m.JudgeMisses));
        }
    }

    [Fact]
    public void TheNoisyWorldIsScoredAsRegistered()
    {
        static ConvoyMeasures M(int delivered, int lies, int served, int @checked = 0, int fa = 0, int misses = 0, int checkedLies = 0) =>
            new(delivered, 30, 0, 0, lies, 0, served, true, 0, 10, 0) { Checked = @checked, CheckFalseAlarms = fa, CheckMisses = misses, CheckedLies = checkedLies };
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>
        {
            [ConvoyArm.J] = [M(30, 2, 20)],
            [ConvoyArm.U] = [M(30, 2, 30, 200, 10, 0, 4)],
            [ConvoyArm.V] = [M(30, 2, 24, 200, 2, 1, 4)],
            [ConvoyArm.P] = [M(30, 2, 23, 200, 1, 0, 4)],
            [ConvoyArm.F] = [M(30, 3, 25, 200, 1, 1, 4)],
        };
        var verdicts = ConvoyBattery.Score(runs, true).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T10"].Verdict);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T10c"].Verdict);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T10d"].Verdict); // 0.5% against 1.5%
        Assert.Equal(HypothesisVerdict.Held, verdicts["T10e"].Verdict); // 1 of 200
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T10f"].Verdict); // 1 of 4 lies missed
        Assert.Equal(HypothesisVerdict.Held, verdicts["T10g"].Verdict);

        runs[ConvoyArm.U] = [M(30, 2, 30, 200, 2, 0, 4)];
        runs[ConvoyArm.P] = [M(30, 2, 31, 200, 2, 1, 4)];
        runs[ConvoyArm.F] = [M(30, 2, 25, 200, 1, 0, 4)];
        verdicts = ConvoyBattery.Score(runs, false).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T10"].Verdict); // 1% is not more than 1%
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T10c"].Verdict); // served later than U
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T10d"].Verdict); // 1.5% is not lower than 1.5%
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T10e"].Verdict); // 2 of 200 is 1%
        Assert.Equal(HypothesisVerdict.Held, verdicts["T10f"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T10g"].Verdict);
    }

    private static string Repo
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Creatura.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
        }
    }
}
