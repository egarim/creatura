using Creatura.Bench;
using Creatura.Seats;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Step 5 of docs/design-trained-models.md, the world model in the loop: the C# model repeats the trainer's
/// arithmetic, the learned judge's verdict is the one the trucks act on, and arm X is scored against J (T8 to T8c).
/// </summary>
public sealed class LearnedJudgeTests
{
    private static WorldModel Model => ConvoyNetwork.LoadWorld(ConvoyNetwork.EmbeddedWorld, RoadMap.FloodedValley());

    /// <summary>The surprise the C# model computes for a fixed input is the one the Python trainer computed for it.</summary>
    [Fact]
    public void TheSurpriseIsTheTrainersArithmetic()
    {
        var model = Model;
        var before = Enumerable.Range(0, model.Features.Count).Select(i => (i % 7) / 6.0).ToArray();
        var after = Enumerable.Range(0, model.Features.Count).Select(i => ((i * 3) % 5) / 4.0).ToArray();
        // From tools/train/train_world.py's predict and surprise on models/convoy-world-j.json.
        Assert.Equal(234.71997016536898, model.Surprise(before, 5, after), 9);
        Assert.Equal(1.4393573087557878e-08, model.Predict(before, 5)[0], 12);
    }

    [Fact]
    public void AModelForAnotherKindOrMapIsRefused()
    {
        Assert.Throws<FormatException>(() => WorldModel.Load(Path.Combine(Repo, "models", "convoy-policy-j.json")));
        Assert.Equal("convoy", Model.World);
        Assert.Equal(ConvoyFeatures.Schema(RoadMap.FloodedValley()).Hash, Model.SchemaHash);
    }

    [Fact]
    public async Task InArmXTheLearnedJudgeDecidesAndCatchesTheLyingRoad()
    {
        var setup = ConvoySetup.For(ConvoyArm.X, 3);
        Assert.Equal(ConvoyNetwork.EmbeddedWorld, setup.LearnedJudge);
        Assert.Contains(ConvoyArm.X, ConvoyBattery.DefaultArms);
        var run = await new ConvoyScenario(setup).RunAsync();
        Assert.Empty(run.Errors);
        Assert.True(run.Measures.Judged > 50);
        Assert.Equal(0, run.Measures.LyingRoadRepeats);
        // It catches the lie without crying wolf: an ordinary action that landed is not judged a lie.
        Assert.True(run.Measures.JudgeFalseAlarms <= 1, $"{run.Measures.JudgeFalseAlarms} false alarms");
        Assert.Contains(run.Ticks.SelectMany(t => t.Events), e => e.Kind == "surprised");
    }

    /// <summary>
    /// The verdict the trucks act on is the learned judge's: with a threshold no surprise reaches, no lie is caught and
    /// the lying road is taken again; with a threshold every action passes, every action is a lie.
    /// </summary>
    [Fact]
    public async Task TheThresholdDecidesWhatTheTrucksBelieve()
    {
        var blind = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.X, 3) with { LearnedJudgeThreshold = 1e9 }).RunAsync();
        Assert.True(blind.Measures.JudgeMisses > 0);
        Assert.True(blind.Measures.LyingRoadRepeats > 0);
        Assert.DoesNotContain(blind.Ticks.SelectMany(t => t.Events), e => e.Kind == "surprised");

        var jumpy = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.X, 3) with { LearnedJudgeThreshold = 0 }).RunAsync();
        Assert.Equal(jumpy.Measures.Judged, jumpy.Ticks.SelectMany(t => t.Events).Count(e => e.Kind == "surprised"));
        Assert.True(jumpy.Measures.JudgeFalseAlarms > 0);
    }

    [Fact]
    public async Task T8c_ArmXReplaysByteForByte() => Assert.True(await ConvoyBattery.ReplaysMatchAsync(ConvoyArm.X, 0));

    [Fact]
    public void TheLearnedJudgeIsScoredAgainstJ()
    {
        static ConvoyMeasures M(int delivered, int lies, int judged = 0, int alarms = 0) =>
            new(delivered, 30, 0, 0, lies, 0, 20, true, 0, 10, 0) { Judged = judged, JudgeFalseAlarms = alarms };
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>
        {
            [ConvoyArm.J] = [M(30, 2), M(30, 1)],
            [ConvoyArm.X] = [M(30, 2, 100, 0), M(30, 1, 100, 1)],
        };
        var verdicts = ConvoyBattery.Score(runs, true).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T8"].Verdict);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T8b"].Verdict); // 1 of 200 is 0.5%
        Assert.Equal(HypothesisVerdict.Held, verdicts["T8c"].Verdict);

        runs[ConvoyArm.X] = [M(30, 3, 100, 1), M(29, 1, 100, 1)];
        verdicts = ConvoyBattery.Score(runs, false).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T8"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T8b"].Verdict); // 2 of 200 is 1%
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T8c"].Verdict);
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
