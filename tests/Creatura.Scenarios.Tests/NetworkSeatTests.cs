using System.Text.Json.Nodes;
using Creatura.Bench;
using Creatura.Seats;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Step 2 of docs/design-trained-models.md: the policy network in the seat. Its arithmetic, its refusals, that the C#
/// seat reproduces what the trainer measured, and arm N (T4 here; T2 is the battery's).
/// </summary>
public sealed class NetworkSeatTests
{
    /// <summary>Two features, three actions, one hidden layer of two, written by hand so every score can be checked.</summary>
    private static string Tiny(string world = "convoy", string format = PolicyNetwork.Format, double[][]? output = null) => new JsonObject
    {
        ["format"] = format,
        ["kind"] = "policy",
        ["world"] = world,
        ["map"] = "tiny",
        ["schemaHash"] = "0000",
        ["inputs"] = "features+visible",
        ["features"] = new JsonArray("f1", "f2"),
        ["actions"] = new JsonArray("a", "b", "wait"),
        ["layers"] = new JsonArray(
            new JsonObject
            {
                ["activation"] = "relu",
                // inputs: f1, f2, visible a, visible b, visible wait
                ["weights"] = Rows([[1, 0, 0, 0, 0], [0, -1, 0, 0, 0]]),
                ["bias"] = new JsonArray(0.0, 0.5),
            },
            new JsonObject
            {
                ["activation"] = "none",
                ["weights"] = Rows(output ?? [[2, 0], [0, 4], [0, 0]]),
                ["bias"] = new JsonArray(0.0, 0.0, 0.1),
            }),
    }.ToJsonString();

    private static JsonArray Rows(double[][] rows) => new(rows.Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray())).ToArray());

    private static PolicyContext Context(params string[] tools) => new()
    {
        Facts = new HashSet<string>(),
        Tools = tools.Select(t => new Tool(t, [], [], "act")).ToList(),
        Goal = _ => false,
        GoalText = "",
        Tick = 0,
        MaxTicks = 10,
        Freedom = 0,
    };

    [Fact]
    public void TheScoresAreTheLayersArithmetic()
    {
        var network = PolicyNetwork.Parse(Tiny(), "tiny");
        // f1 = 0.3, f2 = 0.2: hidden = relu(0.3), relu(-0.2 + 0.5) = 0.3, 0.3; scores = 0.6, 1.2, 0.1
        var scores = network.Scores([0.3, 0.2, 1, 1, 1]);
        Assert.Equal([0.6, 1.2, 0.1], scores.Select(s => Math.Round(s, 10)));
        // f2 = 0.9 drives the second hidden unit below zero: ReLU holds it at 0, so b scores 0.
        Assert.Equal(0.0, network.Scores([0.3, 0.9, 1, 1, 1])[1]);
    }

    [Fact]
    public void TheSeatPlaysTheBestVisibleActionAndNeverAHiddenOne()
    {
        var seat = new NetworkSeat(PolicyNetwork.Parse(Tiny(), "tiny"), _ => [0.3, 0.2]);
        Assert.Equal("b", seat.Next(Context("a", "b", "wait")).Tool);
        // b scores highest but is not visible: the best visible one is played.
        Assert.Equal("a", seat.Next(Context("a", "wait")).Tool);
        // Nothing it knows is visible: it waits when it can, and gives no tool when it cannot.
        Assert.Equal("wait", new NetworkSeat(PolicyNetwork.Parse(Tiny(), "tiny"), _ => [0.3, 0.2]).Next(Context("other", "wait")).Tool);
        Assert.Null(seat.Next(Context("other")).Tool);
    }

    [Fact]
    public void ATieGoesToTheLowestIndex()
    {
        var seat = new NetworkSeat(PolicyNetwork.Parse(Tiny(output: [[1, 0], [1, 0], [0, 0]]), "tiny"), _ => [0.3, 0.2]);
        Assert.Equal("a", seat.Next(Context("b", "a", "wait")).Tool);
    }

    [Fact]
    public void AMalformedNetworkIsRefused()
    {
        Assert.Throws<FormatException>(() => PolicyNetwork.Parse(Tiny(format: "creatura.network/0"), "old"));
        Assert.Throws<FormatException>(() => PolicyNetwork.Parse(Tiny(output: [[2, 0], [0, 4]]), "two scores for three actions"));
        Assert.Throws<FormatException>(() => PolicyNetwork.Parse(Tiny(output: [[2, 0, 1], [0, 4, 1], [0, 0, 1]]), "rows too long"));
        var seat = new NetworkSeat(PolicyNetwork.Parse(Tiny(), "tiny"), _ => [0.3]);
        Assert.Throws<InvalidOperationException>(() => seat.Next(Context("a")));
    }

    [Fact]
    public void ANetworkForAnotherWorldOrSchemaIsRefusedInTheConvoy()
    {
        var map = RoadMap.FloodedValley();
        ConvoyNetwork.Fits(ConvoyNetwork.Load(ConvoyNetwork.Embedded), map);
        Assert.Contains("world", Assert.Throws<ArgumentException>(() => ConvoyNetwork.Fits(PolicyNetwork.Parse(Tiny(world: "irrigation"), "irrigation"), map)).Message);
        Assert.Contains("schema", Assert.Throws<ArgumentException>(() => ConvoyNetwork.Fits(PolicyNetwork.Parse(Tiny(), "tiny"), map)).Message);
    }

    [Fact]
    public void TheEmbeddedNetworkIsTheConvoysAndSaysHowItWasTrained()
    {
        var network = ConvoyNetwork.Load(ConvoyNetwork.Embedded);
        var schema = ConvoyFeatures.Schema(RoadMap.FloodedValley());
        Assert.Equal(schema.Hash, network.SchemaHash);
        Assert.Equal(ConvoyActions.Vocabulary(RoadMap.FloodedValley()), network.Actions);
        var training = network.Training!.Value;
        Assert.Equal("J", training.GetProperty("arms")[0].GetString());
        // Trained on seeds at or above the first training seed: never on the battery's.
        Assert.True(training.GetProperty("seeds")[0].GetInt32() >= ConvoyDatasets.FirstTrainingSeed);
        Assert.True(training.GetProperty("heldOutAccuracy").GetDouble() >= 0.99);
    }

    /// <summary>
    /// The C# seat reproduces what the trainer measured: on decisions of held-out seeds, recorded from arm J's planner,
    /// it chooses what the planner chose as often as the trainer reported. Wrong arithmetic would collapse this.
    /// </summary>
    [Fact]
    public async Task TheSeatChoosesWhatThePlannerChoseOnHeldOutSeeds()
    {
        var recorder = new ConvoyRecorder();
        foreach (var seed in new[] { 560, 561, 562, 563 })
            await new ConvoyScenario(ConvoySetup.For(ConvoyArm.J, seed), recorder: recorder).RunAsync();
        var map = RoadMap.FloodedValley();
        var network = ConvoyNetwork.Load(ConvoyNetwork.Embedded);
        var setup = recorder.Runs[0].Setup;
        int agreed = 0, total = 0;
        foreach (var run in recorder.Runs)
        {
            foreach (var d in run.Decisions)
            {
                var seat = ConvoyNetwork.Seat(network, map, setup.Trucks.Single(t => t.Name == d.Truck).Machine);
                total++;
                if (seat.Next(d.Seat.ToPolicyContext()).Tool == d.Chosen) agreed++;
            }
        }
        Assert.True(total > 200);
        Assert.True(agreed >= 0.99 * total, $"agreed on {agreed} of {total}");
    }

    /// <summary>T4: the network in the seat replays byte for byte for the same seed.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task T4_ArmNReplaysByteForByte(int seed) => Assert.True(await ConvoyBattery.ReplaysMatchAsync(ConvoyArm.N, seed));

    [Fact]
    public async Task ArmNPutsTheNetworkInEverySeatAndSaysSo()
    {
        var setup = ConvoySetup.For(ConvoyArm.N, 2);
        Assert.Equal(ConvoyNetwork.Embedded, setup.SeatNetwork);
        Assert.Null(setup.SeatModel);
        Assert.True(setup.DistrustRoads && setup.HandOffWhenStranded && setup.StallAfter is not null);
        Assert.Contains(ConvoyArm.N, ConvoyBattery.DefaultArms);

        using var text = new StringWriter();
        var run = await new ConvoyScenario(setup).RunAsync(new JsonlReplayWriter(text));
        Assert.Empty(run.Errors);
        var header = JsonNode.Parse(text.ToString().Split('\n')[0])!;
        var definitions = header["trucks"]!.AsArray().Select(t => t!["agent"]!).ToList();
        Assert.Equal(3, definitions.Count);
        Assert.All(definitions, d =>
        {
            Assert.Contains("policy network", d!["seat"]!.GetValue<string>());
            Assert.Contains("ConvoyNetwork.Seat", d["cSharp"]!.GetValue<string>());
        });
    }

    // ------------------------------------------------------------------ step 3: sampling for trial, and arm R

    [Fact]
    public void SamplingPlaysOnlyVisibleActionsInProportionAndRepeatsWithTheSameSeed()
    {
        var network = PolicyNetwork.Parse(Tiny(), "tiny");
        string Draw(Random random) => new NetworkSeat(network, _ => [0.3, 0.2], sample: random, temperature: 1).Next(Context("a", "b")).Tool!;
        var first = new Random(5);
        var again = new Random(5);
        var picks = Enumerable.Range(0, 4000).Select(_ => Draw(first)).ToList();
        Assert.Equal(picks.Take(200), Enumerable.Range(0, 200).Select(_ => Draw(again)));
        Assert.All(picks, p => Assert.Contains(p, new[] { "a", "b" }));
        // Scores 0.6 and 1.2 over the visible a and b: b has e^0.6 / (1 + e^0.6) = 64.6% of the draws.
        Assert.InRange(picks.Count(p => p == "b") / 4000.0, 0.62, 0.67);
        // At a temperature of 2 the scores are halved: b has e^0.3 / (1 + e^0.3) = 57.4%.
        var warm = new Random(6);
        var warmPicks = Enumerable.Range(0, 4000).Select(_ => new NetworkSeat(network, _ => [0.3, 0.2], sample: warm, temperature: 2).Next(Context("a", "b")).Tool).ToList();
        Assert.InRange(warmPicks.Count(p => p == "b") / 4000.0, 0.55, 0.60);
    }

    [Fact]
    public async Task ATrialRunRepeatsForItsSampleSeedAndDiffersForAnother()
    {
        async Task<string> Trial(int sampleSeed)
        {
            using var text = new StringWriter();
            var setup = ConvoySetup.For(ConvoyArm.N, 1000) with { SeatTemperature = 2, SampleSeed = sampleSeed };
            await new ConvoyScenario(setup).RunAsync(new JsonlReplayWriter(text));
            return ConvoyBattery.Scrub(text.ToString());
        }
        Assert.Equal(await Trial(1), await Trial(1));
        Assert.NotEqual(await Trial(1), await Trial(2));
    }

    [Fact]
    public void ArmRCarriesTheNetworkTrainedByTrialAndSaysHowItWasTrained()
    {
        var setup = ConvoySetup.For(ConvoyArm.R, 3);
        Assert.Equal(ConvoyNetwork.EmbeddedTrial, setup.SeatNetwork);
        Assert.Null(setup.SeatTemperature);
        Assert.Contains(ConvoyArm.R, ConvoyBattery.DefaultArms);
        var network = ConvoyNetwork.Load(ConvoyNetwork.EmbeddedTrial);
        ConvoyNetwork.Fits(network, setup.Map);
        var training = network.Training!.Value;
        Assert.Equal("trial", training.GetProperty("by").GetString());
        Assert.Equal("example", training.GetProperty("startedFrom").GetProperty("by").GetString());
        // Neither its trial seeds nor its selection seeds are the battery's.
        Assert.True(training.GetProperty("trialSeeds")[0].GetInt32() >= ConvoyDatasets.FirstTrainingSeed);
        Assert.True(training.GetProperty("selectionSeeds")[0].GetInt32() >= ConvoyDatasets.FirstTrainingSeed);
    }

    /// <summary>T4 for arm R: trained by trial, it still plays its best choice, so a seed replays byte for byte.</summary>
    [Fact]
    public async Task T4_ArmRReplaysByteForByte() => Assert.True(await ConvoyBattery.ReplaysMatchAsync(ConvoyArm.R, 0));

    [Fact]
    public void TheTrainedArmsAreScoredAgainstJ()
    {
        static ConvoyMeasures M(int delivered, int lies = 0, int stranded = 0, int gauge = 0) =>
            new(delivered, 30, 0, stranded, lies, 0, 20, true, 0, 10, 0) { GaugeStranded = gauge };
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>
        {
            [ConvoyArm.J] = [M(30, lies: 3, stranded: 1, gauge: 1), M(30, lies: 1)],
            [ConvoyArm.N] = [M(29, lies: 3, stranded: 1, gauge: 1), M(30, lies: 1)],
            [ConvoyArm.R] = [M(30, lies: 2), M(30, lies: 1)],
        };
        var verdicts = ConvoyBattery.Score(runs, true).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T2"].Verdict); // 59 of 60 is over 95%
        Assert.Equal(HypothesisVerdict.Held, verdicts["T3"].Verdict);
        Assert.Equal(HypothesisVerdict.Held, verdicts["T3b"].Verdict);
        Assert.Contains("road lies R 3, J 3", verdicts["T3"].Evidence);

        runs[ConvoyArm.N] = [M(27), M(28)];
        runs[ConvoyArm.R] = [M(30, lies: 4, stranded: 1), M(29)];
        verdicts = ConvoyBattery.Score(runs, true).ToDictionary(v => v.Id);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T2"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T3"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, verdicts["T3b"].Verdict);
        Assert.Equal(HypothesisVerdict.Failed, ConvoyBattery.Score(runs, false).Single(v => v.Id == "T4R").Verdict);
    }
}
