using Creatura.Seats;
using Creatura.Worlds;

namespace Creatura.Tests;

/// <summary>
/// The seat's biology (migration step 4's VesselBiologyTests, moved here with BiologyExtension at step 15): the record
/// is the endpoint's sheet plus what scoreboard rows measured; the extension fills an empty vessel and never overwrites
/// one the identity stated; <c>biology</c> is a catalog name bound to a seat and refused for the planner.
/// </summary>
public class BiologyTests
{
    [Fact]
    public async Task TheBiologyExtensionFillsAnEmptyVesselAndKeepsAStatedOne()
    {
        var biology = new ModelBiology("deepseek-chat", "deepseek", false, "json", false, 128_000, 64_000, 8_192, null, 0.27m, 1.10m, 2.14, null, null, null, null);

        var measured = new SelfModelRegulator(Identity.Bach);
        await new ReferenceLoop(measured, options: AgentOptions.With(new BiologyExtension(biology))).RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Equal("deepseek-chat (json, ctx 64,000, 2.1s/call, $0.27 in / $1.10 out per million tokens) on cloud via deepseek", measured.Self!.Vessel!.Render());
        Assert.Contains("vessel: deepseek-chat", measured.Self.Render(), StringComparison.Ordinal);

        var stated = new SelfModelRegulator(Identity.Bach with { Vessel = new Vessel { Model = "stated by the host" } });
        await new ReferenceLoop(stated, options: AgentOptions.With(new BiologyExtension(biology))).RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.Equal("stated by the host", stated.Self!.Vessel!.Model);
    }

    [Fact]
    public void TheRecordIsTheSheetPlusTheMeasurements()
    {
        var endpoint = ModelCatalog.Compile(_ => null).TryFind("phi4-mini")!;
        var unmeasured = Biology.For(endpoint);
        Assert.Equal("phi4-mini", unmeasured.Model);
        Assert.Equal("ollama", unmeasured.Provider);
        Assert.True(unmeasured.Local);
        Assert.Equal("json", unmeasured.Protocol);
        Assert.Equal((128_000, 4_096, 4_096), (unmeasured.ContextWindow, unmeasured.ContextAsRun, unmeasured.MaxOutput));
        Assert.Null(unmeasured.MeasuredRuns);
        Assert.Contains("context window 128,000 tokens, run here with 4,096; max output 4,096 tokens", unmeasured.Lines());
        Assert.Contains("note: 4-bit quantised; no native tool calling in this build", unmeasured.Lines());

        var rows = new[] { 2.0, 4.0, 3.0 }.Select(s => new Creatura.Bench.Row { Seat = "phi4-mini", Calls = 10, Invalid = 1, MedianCallSeconds = s, TokensPerSecond = 20 }).ToList();
        var measured = Biology.For(endpoint, rows);
        Assert.Equal(3, measured.MeasuredRuns);
        Assert.Equal(3.0, measured.MedianCallSeconds);
        Assert.Equal(0.1, measured.InvalidRate!.Value, 6);
        Assert.Contains("measured over 3 runs here: median call 3.0 s, 20 tokens/s, 10% of your answers were invalid", measured.Lines());
        Assert.Equal("phi4-mini (json, ctx 4,096, 3.0s/call)", measured.OneLine());

        var unknown = Biology.For(new ModelEndpoint("x", Provider.OpenAiCompatible, "http://x", "x", null, true, false, null, null));
        Assert.Contains("context window unknown tokens; max output unknown tokens", unknown.Lines());
        Assert.Contains("price unknown", unknown.Lines());
    }

    [Fact]
    public void TheSelfReportIsScoredAgainstTheRecord()
    {
        var biology = Biology.For(ModelCatalog.Compile(_ => null).TryFind("deepseek-chat")!);
        Assert.Equal(3, BiologyExtension.ScoreReport(biology, "I answer with one function call.\nMy context window is 128,000 tokens.\nI cost $0.28 in and $0.42 out per million tokens."));
        Assert.Equal(0, BiologyExtension.ScoreReport(biology, "One JSON object; 4,096 tokens; free, local."));
        Assert.Equal(1, BiologyExtension.ScoreReport(biology, "One function call. Window unknown. Free."));
        Assert.Equal(0, BiologyExtension.ScoreReport(biology, null));
    }

    [Fact]
    public void TheBiologyExtensionIsACatalogNameBoundToASeat()
    {
        var root = Path.Combine(Path.GetTempPath(), "creatura-bio-" + Guid.NewGuid().ToString("N"));
        BiologySwitch.Register(new ModelClient(), root);
        BiologySwitch.Seat(null);
        try
        {
            Assert.True(ExtensionCatalog.Knows("biology"));
            Assert.False(BiologySwitch.Bound);
            var refused = Assert.Throws<InvalidOperationException>(() => ExtensionCatalog.Parse(["biology"]));
            Assert.Contains("needs a model in the seat", refused.Message);

            var endpoint = ModelCatalog.Compile(_ => null).Endpoints[0];
            BiologySwitch.Seat(endpoint);
            Assert.True(BiologySwitch.Bound);
            var options = ExtensionCatalog.Parse(["honest", "biology"]);
            var biology = Assert.IsType<BiologyExtension>(options.Extensions[1]);
            Assert.Equal(endpoint.Name, biology.Record.Model);
            Assert.Equal("honest-failure", options.Extensions[0].Name);
        }
        finally
        {
            BiologySwitch.Seat(null);
        }
    }
}
