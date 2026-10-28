using System.Text.Json;
using System.Text.Json.Nodes;
using Creatura.Seats;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Step 1 of docs/design-trained-models.md: the recorder, the feature encoder, the action vocabulary, the transitions,
/// the seat's prompt exposed, and the exporter. T1 first: recording changes nothing.
/// </summary>
public sealed class RecorderTests
{
    private static async Task<(ConvoyRun Run, ConvoyRecorder Recorder)> Recorded(ConvoyArm arm, int seed)
    {
        var recorder = new ConvoyRecorder();
        var run = await new ConvoyScenario(ConvoySetup.For(arm, seed), recorder: recorder).RunAsync();
        return (run, recorder);
    }

    private static async Task<string> Replay(ConvoyArm arm, int seed, ConvoyRecorder? recorder)
    {
        using var text = new StringWriter();
        await new ConvoyScenario(ConvoySetup.For(arm, seed), recorder: recorder).RunAsync(new JsonlReplayWriter(text));
        return ConvoyBattery.Scrub(text.ToString());
    }

    /// <summary>T1: the recorder changes nothing: a run with it replays byte for byte as one without it.</summary>
    [Theory]
    [InlineData(ConvoyArm.J, 1)]
    [InlineData(ConvoyArm.C, 2)]
    [InlineData(ConvoyArm.D, 3)]
    [InlineData(ConvoyArm.L, 4)]
    public async Task T1_RecordingChangesNothing(ConvoyArm arm, int seed)
    {
        var without = await Replay(arm, seed, null);
        var recorder = new ConvoyRecorder();
        var with = await Replay(arm, seed, recorder);
        Assert.Equal(without, with);
        Assert.NotEmpty(Assert.Single(recorder.Runs).Decisions);
    }

    [Fact]
    public async Task EveryDecisionIsRecordedWithWhatItCameTo()
    {
        var (run, recorder) = await Recorded(ConvoyArm.J, 1);
        var recorded = Assert.Single(recorder.Runs);
        Assert.Equal((ConvoyArm.J, 1), (recorded.Setup.Arm, recorded.Setup.Seed));
        Assert.Equal(run.Measures, recorded.Measures);
        Assert.All(recorded.Decisions, d =>
        {
            Assert.NotNull(d.Chosen);
            Assert.NotNull(d.Verdict);
            Assert.Contains(d.Chosen, d.Seat.Tools.Select(t => t.Id));
            Assert.Contains(d.Seat.Facts, f => f.StartsWith("at:", StringComparison.Ordinal));
        });
        // Every truck's decisions come in tick order, one per tick it acted.
        foreach (var truck in recorded.Decisions.GroupBy(d => d.Truck))
        {
            var ticks = truck.Select(d => d.Tick).ToList();
            Assert.Equal(ticks.Order(), ticks);
            Assert.Equal(ticks.Distinct().Count(), ticks.Count);
        }
        // The generic seam ran beside it, unchanged.
        Assert.NotEmpty(recorder.Examples.Examples);
        Assert.Equal(recorded.Decisions.Count, recorder.Examples.Examples.Count(e => e.Run.StartsWith("Truck", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheFeaturesAndTheMaskFollowTheSchemaAndTheVocabulary()
    {
        var (_, recorder) = await Recorded(ConvoyArm.J, 2);
        var map = RoadMap.FloodedValley();
        var schema = ConvoyFeatures.Schema(map);
        var vocabulary = ConvoyActions.Vocabulary(map);
        Assert.Equal(36, vocabulary.Count);
        Assert.Equal(schema.Names.Count, schema.Names.Distinct().Count());
        foreach (var d in recorder.Runs.Single().Decisions)
        {
            Assert.Equal(schema.Names.Count, d.Features.Count);
            Assert.All(d.Features, x => Assert.InRange(x, 0.0, 1.0));
            Assert.Equal(1.0, schema.Names.Select((n, i) => (n, i)).Where(p => p.n.StartsWith("at.", StringComparison.Ordinal)).Sum(p => d.Features[p.i]));
            Assert.Equal(d.Seat.Tools.Select(t => vocabulary.IndexOf(t.Id)).Order(), d.Mask.Order());
            Assert.Contains(d.ActionIndex!.Value, d.Mask);
            Assert.Equal(d.Chosen, vocabulary[d.ActionIndex.Value]);
        }
        Assert.Equal(schema.Hash, ConvoyFeatures.Schema(RoadMap.FloodedValley()).Hash);
    }

    [Fact]
    public async Task ADriveThatLandedMovesTheTruckInItsTransition()
    {
        var (_, recorder) = await Recorded(ConvoyArm.J, 1);
        var drives = recorder.Runs.Single().Decisions.Where(d => d.Chosen!.StartsWith("drive:", StringComparison.Ordinal) && d.Verdict == "Landed").ToList();
        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.Contains($"at:{d.Chosen!.Split(':')[2]}", d.FactsAfter));
    }

    /// <summary>The prompt the exporter writes for a decision is exactly the prompt a model seat sends for it.</summary>
    [Fact]
    public async Task TheExportedPromptIsExactlyWhatAModelSeatSends()
    {
        var model = new CapturingModel();
        var client = new ModelClient(transports: [model]) { RetryDelay = TimeSpan.Zero };
        var catalog = ModelCatalog.Parse("""
            {"models":[{"name":"capture","provider":"ollama","baseUrl":"http://localhost:11434","model":"capture","keyEnv":null,
                        "nativeTools":false,"reasoning":false,"inputPerMillion":0,"outputPerMillion":0}]}
            """, "test");
        var recorder = new ConvoyRecorder();
        var setup = ConvoySetup.For(ConvoyArm.S, 1) with { SeatModel = "capture", MaxTicks = 6 };
        await new ConvoyScenario(setup, client, catalog: catalog, recorder: recorder).RunAsync();

        var recorded = recorder.Runs.Single();
        Assert.Equal(model.Requests.Count, recorded.Decisions.Count);
        for (var i = 0; i < model.Requests.Count; i++)
        {
            var exported = ConvoyDatasets.Prompt(recorded, recorded.Decisions[i]);
            var sent = model.Requests[i].Messages;
            Assert.Equal(sent.Select(m => (m.Role, m.Content)), exported.Select(m => (m.Role, m.Content)));
        }
    }

    [Fact]
    public async Task TheExporterWritesThreeSetsAndACardAndKeepsItsFilters()
    {
        using var dir = new TempDirectory();
        var recorder = new ConvoyRecorder();
        foreach (var seed in new[] { 100, 101, 102 })
            await new ConvoyScenario(ConvoySetup.For(ConvoyArm.A, seed), recorder: recorder).RunAsync();
        var served = recorder.Runs.Count(r => r.Measures.AllServed);

        var card = ConvoyDatasets.Export(recorder.Runs, new DatasetOptions { OnlyServed = false }, dir.Path);
        Assert.Equal(recorder.Runs.Sum(r => r.Decisions.Count), card.Decisions);
        foreach (var file in new[] { "policy.jsonl", "chat.jsonl", "transitions.jsonl", "card.json" }) Assert.True(File.Exists(Path.Combine(dir.Path, file)), file);
        var policy = File.ReadLines(Path.Combine(dir.Path, "policy.jsonl")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(card.Decisions, policy.Count);
        Assert.All(policy, p => Assert.Equal(card.Features.Count, p["features"]!.AsArray().Count));
        var chat = JsonNode.Parse(File.ReadLines(Path.Combine(dir.Path, "chat.jsonl")).First())!;
        Assert.Equal(["system", "user", "assistant"], chat["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.NotNull(JsonNode.Parse(chat["messages"]![2]!["content"]!.GetValue<string>())!["tool"]);

        using var onlyServed = new TempDirectory();
        var filtered = ConvoyDatasets.Export(recorder.Runs, new DatasetOptions { OnlyServed = true }, onlyServed.Path);
        Assert.Equal(served, filtered.Runs);
        Assert.Equal(recorder.Runs.Where(r => r.Measures.AllServed).Sum(r => r.Decisions.Count), filtered.Decisions);
        Assert.Equal([100, 101, 102], card.Seeds);
    }

    [Fact]
    public void TheBatterysSeedsAreNeverExported()
    {
        var recorder = new ConvoyRecorder();
        var run = new RecordedRun(ConvoySetup.For(ConvoyArm.J, 7), new ConvoyMeasures(30, 30, 0, 0, 0, 0, 20, true, 0, 0, 0), []);
        using var dir = new TempDirectory();
        var refused = Assert.Throws<ArgumentException>(() => ConvoyDatasets.Export([run], new DatasetOptions(), dir.Path));
        Assert.Contains("seed", refused.Message);
        Assert.Equal(50, ConvoyDatasets.FirstTrainingSeed);
    }

    /// <summary>A recording written to disk and read back holds the same runs and exports the same sets.</summary>
    [Fact]
    public async Task ARecordingReadBackExportsTheSame()
    {
        var recorder = new ConvoyRecorder();
        foreach (var seed in new[] { 120, 121 })
            await new ConvoyScenario(ConvoySetup.For(ConvoyArm.J, seed), recorder: recorder).RunAsync();
        using var dir = new TempDirectory();
        var file = System.IO.Path.Combine(dir.Path, "runs.jsonl");
        using (var writer = new StreamWriter(file)) recorder.WriteJsonl(writer);

        var back = ConvoyRecorder.ReadJsonl(file);
        Assert.Equal(recorder.Runs.Select(r => (r.Id, r.Measures, r.Decisions.Count)), back.Select(r => (r.Id, r.Measures, r.Decisions.Count)));
        Assert.Equal(recorder.Runs[0].Decisions[5].Features, back[0].Decisions[5].Features);
        Assert.Equal(recorder.Runs[1].Decisions[^1].FactsAfter, back[1].Decisions[^1].FactsAfter);

        using var a = new TempDirectory();
        using var b = new TempDirectory();
        ConvoyDatasets.Export(recorder.Runs, new DatasetOptions(), a.Path);
        ConvoyDatasets.Export(back, new DatasetOptions(), b.Path);
        foreach (var set in new[] { "policy.jsonl", "chat.jsonl", "transitions.jsonl", "card.json" })
            Assert.Equal(File.ReadAllText(System.IO.Path.Combine(a.Path, set)), File.ReadAllText(System.IO.Path.Combine(b.Path, set)));
    }

    private sealed class CapturingModel : IModelTransport
    {
        public Provider Provider => Provider.Ollama;
        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var answer = new JsonObject { ["tool"] = "wait", ["why"] = "capturing" }.ToJsonString();
            return Task.FromResult(new ModelReply(answer, null, [], 100, 5, null, null, 0.01, null, null, null, null, "stop", 200, null));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "creatura-dataset-" + Guid.NewGuid().ToString("N")[..10]);

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
