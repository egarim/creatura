using Creatura.Seats;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Step 8 of docs/design-trained-models.md, the JEPA as a guide that calls the seat less: the latent guide repeats the
/// trainer's arithmetic, the guided seat calls its inner seat exactly when it should, and the guided arms count calls.
/// </summary>
public sealed class GuidedSeatTests
{
    private static LatentGuide Guide => (LatentGuide)ConvoyNetwork.LoadGuide(ConvoyNetwork.EmbeddedGuide, RoadMap.FloodedValley());

    /// <summary>The subgoal and the ranking the C# guide computes for a fixed input are the ones the Python trainer computes.</summary>
    [Fact]
    public void TheGuideIsTheTrainersArithmetic()
    {
        var guide = Guide;
        var before = Enumerable.Range(0, guide.Jepa.Features.Count).Select(i => (i % 7) / 6.0).ToArray();
        // From tools/train/train_goal.py's network and train_jepa.py's encode and predict_next on the embedded files.
        Assert.Equal(0.9478656969507333, guide.Goal.Predict(guide.Jepa.Encode(before))[0], 12);
        var (best, margin) = guide.Rank(before, [4, 5, 28, 29, 35]);
        Assert.Equal(29, best);
        Assert.Equal(0.03616206296942129, margin, 9);
        Assert.Equal(1, guide.Rank(before, [28]).Margin);
        Assert.Equal(5.3288, guide.Goal.SurpriseThresholds["p99"], 4);
    }

    [Fact]
    public void OtherKindsAreRefused()
    {
        Assert.Throws<FormatException>(() => LatentGoal.Load(Path.Combine(Repo, "models", "convoy-jepa-varied.json")));
        Assert.IsType<NetworkGuide>(ConvoyNetwork.LoadGuide(ConvoyNetwork.Embedded, RoadMap.FloodedValley()));
        Assert.Throws<ArgumentException>(() => ConvoyNetwork.GuideSurprise(ConvoyNetwork.LoadGuide(ConvoyNetwork.Embedded, RoadMap.FloodedValley()), "p99"));
        Assert.Null(ConvoyNetwork.GuideSurprise(Guide, null));
    }

    /// <summary>The inner seat is called below the margin, after a surprise, and never when there is only one choice.</summary>
    [Fact]
    public async Task TheInnerSeatIsCalledOnlyWhenTheGuideIsUnsure()
    {
        var guide = new FixedGuide();
        var inner = new CountingSeat(new Fixed("b"));
        var seat = new GuidedSeat(inner, guide, _ => [0.0], margin: 0.5, surprise: 1.0);

        guide.Margin = 0.9;
        Assert.Equal("a", (await seat.NextAsync(Context("a", "b"))).Tool);
        Assert.Equal(0, inner.Calls);

        guide.Margin = 0.2;
        Assert.Equal("b", (await seat.NextAsync(Context("a", "b"))).Tool);
        Assert.Equal(1, seat.MarginCalls);
        Assert.Equal(1, seat.Disagreements);

        guide.Margin = 0.9;
        guide.Surprised = 2.0;
        seat.Observe([0.0], "a", [0.0]);
        Assert.Equal("b", (await seat.NextAsync(Context("a", "b"))).Tool);
        Assert.Equal(1, seat.SurpriseCalls);
        Assert.Equal("a", (await seat.NextAsync(Context("a", "b"))).Tool);  // the surprise counts for one decision only

        guide.Margin = 0.0;
        Assert.Equal("a", (await seat.NextAsync(Context("a"))).Tool);  // one choice: no call
        Assert.Equal(2, inner.Calls);
        Assert.Equal(inner.Calls, seat.Calls);
        Assert.Equal(5, seat.Decisions);
    }

    [Fact]
    public async Task TheGuidedArmCallsThePlannerLessAndArmJCallsItEveryTime()
    {
        var j = (await ConvoyScenario.Create(ConvoyArm.J, 3).RunAsync()).Measures;
        Assert.True(j.Decisions > 0);
        Assert.Equal(j.Decisions, j.SeatCalls);
        var run = await ConvoyScenario.Create(ConvoyArm.M, 3).RunAsync();
        Assert.Empty(run.Errors);
        Assert.InRange(run.Measures.SeatCalls, 1, run.Measures.Decisions - 1);
        Assert.Equal(0, run.Measures.GuideSurpriseCalls);  // arm M's calibrated setting has no surprise trigger
        Assert.Contains(ConvoyArm.M, ConvoyBattery.DefaultArms);
    }

    private static PolicyContext Context(params string[] tools) => new()
    {
        Facts = new HashSet<string>(),
        Tools = tools.Select(t => new Tool(t, [], [], "test")).ToList(),
        Goal = _ => false,
        GoalText = "",
        Tick = 1,
        MaxTicks = 10,
        Freedom = 1,
    };

    private sealed class FixedGuide : ISeatGuide
    {
        public double Margin { get; set; }

        public double Surprised { get; set; }

        public string Name => "fixed";

        public IReadOnlyList<string> Actions { get; } = ["a", "b"];

        public (int Best, double Margin) Rank(IReadOnlyList<double> features, IReadOnlyList<int> visible) => (visible[0], Margin);  // even for one choice, so only the seat's own rule keeps it from calling

        public double? Surprise(IReadOnlyList<double> before, int action, IReadOnlyList<double> after) => Surprised;
    }

    private sealed class Fixed(string tool) : IPolicy
    {
        public string Name => "fixed";

        public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolicySuggestion(tool, "fixed"));
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
