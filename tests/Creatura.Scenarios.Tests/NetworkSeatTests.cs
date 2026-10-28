using System.Text.Json.Nodes;
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
}
