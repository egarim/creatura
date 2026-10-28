using Creatura.Bench;
using Creatura.Cells;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Shared distrust (docs/design-machine-vessels.md, "Shared distrust"): a truck that catches a road lying warns the
/// others on the ledger, and they distrust the road too. Arm W against arm J.
/// </summary>
public sealed class SharedDistrustTests
{
    private static async Task<(ConvoyRun Run, string Replay)> Run(ConvoyArm arm, int seed)
    {
        using var text = new StringWriter();
        var run = await new ConvoyScenario(ConvoySetup.For(arm, seed)).RunAsync(new JsonlReplayWriter(text));
        return (run, text.ToString());
    }

    [Fact]
    public void ArmWIsJWithDistrustShared()
    {
        var w = ConvoySetup.For(ConvoyArm.W, 4);
        var j = ConvoySetup.For(ConvoyArm.J, 4);
        Assert.True(w.ShareDistrust);
        Assert.False(j.ShareDistrust);
        Assert.Equal((j.Faults.LyingRoad, j.Faults.GaugeLiar, j.StallAfter, j.DistrustRoads, j.HandOffWhenStranded, j.Loss, j.Ledger, j.SeatNetwork, j.SeatModel),
            (w.Faults.LyingRoad, w.Faults.GaugeLiar, w.StallAfter, w.DistrustRoads, w.HandOffWhenStranded, w.Loss, w.Ledger, w.SeatNetwork, w.SeatModel));
        Assert.Contains(ConvoyArm.W, ConvoyBattery.DefaultArms);
    }

    /// <summary>
    /// Seed 101 (a training seed, not the battery's): in arm J a truck takes the lying road after another truck was
    /// caught on it. In arm W the others read the warning the next tick, and nobody takes it again.
    /// </summary>
    [Fact]
    public async Task AWarningKeepsTheOthersOffARoadAlreadyCaught()
    {
        var (j, _) = await Run(ConvoyArm.J, 101);
        Assert.True(j.Measures.LiesAlreadyCaught > 0);

        var (w, replay) = await Run(ConvoyArm.W, 101);
        Assert.Equal(0, w.Measures.LiesAlreadyCaught);
        Assert.True(w.Measures.LiesBelieved < j.Measures.LiesBelieved);
        Assert.Contains("\"kind\":\"warned\"", replay);
        Assert.Empty(w.Errors);

        // Every truck read a warning only after the tick it was written in, never the same tick.
        var events = w.Ticks.SelectMany(t => t.Events.Select(e => (t.Tick, e))).ToList();
        var caught = events.Where(x => x.e.Kind == "distrust").Select(x => x.Tick).Min();
        Assert.All(events.Where(x => x.e.Kind == "warned"), x => Assert.True(x.Tick > caught));
    }

    [Fact]
    public async Task WithoutSharingNoWarningIsWritten()
    {
        var (j, replay) = await Run(ConvoyArm.J, 101);
        Assert.DoesNotContain("\"kind\":\"warned\"", replay);
        Assert.DoesNotContain("warning about", replay);
        Assert.True(j.Measures.LiesBelieved > 0);
    }

    /// <summary>W3: with warnings shared, a seed replays byte for byte.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task W3_ArmWReplaysByteForByte(int seed) => Assert.True(await ConvoyBattery.ReplaysMatchAsync(ConvoyArm.W, seed));

    [Fact]
    public void SharedDistrustIsScoredAgainstJ()
    {
        static ConvoyMeasures M(int delivered, int lies, int already = 0, int gauge = 0) =>
            new(delivered, 30, 0, 0, lies, 0, 20, true, 0, 10, 0) { GaugeStranded = gauge, LiesAlreadyCaught = already };
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>
        {
            [ConvoyArm.J] = [M(30, 3, already: 1, gauge: 1), M(30, 1)],
            [ConvoyArm.W] = [M(30, 3, gauge: 2), M(30, 1)],
        };
        var verdicts = ConvoyBattery.Score(runs, true).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Held, verdicts["W1"].Verdict);
        Assert.Equal(HypothesisVerdict.Held, verdicts["W2"].Verdict); // road lies W 2, J 3: the gauge's do not count
        Assert.Equal(HypothesisVerdict.Held, verdicts["W3"].Verdict);

        runs[ConvoyArm.W] = [M(30, 3, already: 1, gauge: 1), M(29, 1)];
        verdicts = ConvoyBattery.Score(runs, false).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["W1"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["W2"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["W3"].Verdict);
    }
}
