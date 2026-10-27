using Creatura.Seats;

namespace Creatura.Tests;

/// <summary>
/// The catalog (migration step 15, third commit): the compiled table against an environment, the rows whose base URL
/// is an unset variable skipped with a message naming it, the optional models.json replacing the table, and
/// samples/models.json kept equal to the compiled table. No host that is anyone's own is in the code.
/// </summary>
public class ModelCatalogTests
{
    /// <summary>Set to anything to rewrite samples/models.json from the compiled table instead of comparing.</summary>
    public const string RecordVariable = "CREATURA_RECORD_SAMPLES";

    private static string? Nothing(string _) => null;

    private static string? Everything(string name) => name switch
    {
        "AZURE_AOAI_BASE" => "https://aoai.test.example/openai/v1",
        "AZURE_FOUNDRY_BASE" => "https://foundry.test.example",
        _ => null,
    };

    private static string Samples => Path.Combine(Path.GetDirectoryName(GoldenTraceTests.Directory_)!, "..", "..", "samples", ModelCatalog.FileName);

    [Fact]
    public void TheCompiledTableHasThirteenSeatsWhenTheAzureBasesAreSet()
    {
        var all = ModelCatalog.Compile(Everything);
        Assert.Equal(13, all.Endpoints.Count);
        Assert.Empty(all.Skipped);
        Assert.Equal("compiled default", all.Source);
        var mini = all.TryFind("gpt-4.1-mini")!;
        Assert.Equal("https://aoai.test.example/openai/v1", mini.BaseUrl);
        Assert.Equal(Provider.OpenAiCompatible, mini.Provider);
        Assert.Equal("AZURE_AOAI_KEY", mini.KeyEnv);
        var phi = all.TryFind("phi-4@azure")!;
        Assert.Equal(Provider.AzureFoundry, phi.Provider);
        Assert.Equal("https://foundry.test.example", phi.BaseUrl);
        Assert.Equal(16_000, phi.Sheet!.ContextWindow);
        var qwen = all.TryFind("qwen2.5:7b")!;
        Assert.Equal("http://localhost:11434", qwen.BaseUrl);
        Assert.Null(qwen.KeyEnv);
        Assert.Equal(4_096, qwen.Sheet!.ContextAsRun);
        Assert.Equal(0.28m, all.TryFind("deepseek-chat")!.InputPerMillion);
        Assert.Equal("DEEPSEEK_API_KEY|deepseek", all.TryFind("deepseek-chat")!.KeyEnv);
    }

    [Fact]
    public void TheAzureRowsAreSkippedWithAMessageNamingTheVariableWhenItIsNotSet()
    {
        var bare = ModelCatalog.Compile(Nothing);
        Assert.Equal(5, bare.Endpoints.Count);
        Assert.Equal(8, bare.Skipped.Count);
        Assert.Contains("gpt-4.1-mini: AZURE_AOAI_BASE is not set; skipped", bare.Skipped[0]);
        Assert.Contains("phi-4-mini@azure: AZURE_FOUNDRY_BASE is not set; skipped", bare.Skipped[4]);
        Assert.Null(bare.TryFind("gpt-4.1-mini"));
        Assert.All(bare.Endpoints, e => Assert.DoesNotContain("azure", e.BaseUrl, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheOllamaHostComesFromTheEnvironmentWhenSet()
    {
        var moved = ModelCatalog.Compile(n => n == "OLLAMA_HOST_URL" ? "http://box:11434" : null);
        Assert.Equal("http://box:11434", moved.TryFind("phi4-mini")!.BaseUrl);
    }

    [Fact]
    public void AModelsFileReplacesTheTableAndItsRowsFollowTheSameRules()
    {
        const string json = """
            { "models": [
              { "name": "mine", "provider": "openai", "baseUrl": "http://localhost:8080/v1", "model": "local-llm", "keyEnv": null, "nativeTools": false, "reasoning": false, "inputPerMillion": 0, "outputPerMillion": 0,
                "sheet": { "contextWindow": 8192, "contextAsRun": null, "maxOutput": 1024, "knowledgeCutoff": "2025", "caveat": "home box" } },
              { "name": "theirs", "provider": "azure-foundry", "baseUrlEnv": "MY_FOUNDRY", "model": "phi-4", "keyEnv": "MY_FOUNDRY_KEY" }
            ] }
            """;
        var catalog = ModelCatalog.Parse(json, "test.json", Nothing);
        Assert.Equal(["mine"], catalog.Names);
        Assert.Equal("theirs: MY_FOUNDRY is not set; skipped (set it to your own azure-foundry endpoint's base URL)", Assert.Single(catalog.Skipped));
        var mine = catalog.TryFind("MINE")!;
        Assert.Equal("local-llm", mine.Model);
        Assert.Equal("home box", mine.Sheet!.Caveat);
        Assert.Equal("json", mine.Protocol);

        var set = ModelCatalog.Parse(json, "test.json", n => n == "MY_FOUNDRY" ? "https://f.example" : null);
        Assert.Equal(2, set.Endpoints.Count);
        Assert.Equal("https://f.example", set.TryFind("theirs")!.BaseUrl);

        Assert.Throws<FormatException>(() => ModelCatalog.Parse("""{ "models": [ { "name": "a", "provider": "openai", "baseUrl": "x", "model": "m" }, { "name": "A", "provider": "openai", "baseUrl": "x", "model": "m" } ] }""", "dup.json", Nothing));
        Assert.Throws<ArgumentException>(() => ModelCatalog.Parse("""{ "models": [ { "name": "a", "provider": "anthropic", "baseUrl": "x", "model": "m" } ] }""", "bad.json", Nothing));
    }

    [Fact]
    public void TheSampleFileIsTheCompiledTable()
    {
        var expected = ModelCatalog.DefaultJson();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RecordVariable)))
        {
            File.WriteAllText(Samples, expected);
        }

        Assert.True(File.Exists(Samples), $"missing {Path.GetFullPath(Samples)}; set {RecordVariable} to write it");
        Assert.Equal(expected, File.ReadAllText(Samples));
        var parsed = ModelCatalog.Load(Samples, Everything);
        Assert.Equal(ModelCatalog.Compile(Everything).Endpoints, parsed.Endpoints);
        // Nobody's host and no key value is in the file: the Azure rows name their variables and nothing else.
        Assert.DoesNotContain("azure.com", expected);
        Assert.DoesNotContain("microsoft.com", expected);
        Assert.Contains("\"baseUrlEnv\": \"AZURE_AOAI_BASE\"", expected);
    }

    [Fact]
    public void TheCurrentCatalogCanBeReplacedAndReset()
    {
        var mine = ModelCatalog.Parse("""{ "models": [ { "name": "only", "provider": "ollama", "baseUrl": "http://localhost:11434", "model": "only" } ] }""", "mine", Nothing);
        try
        {
            ModelCatalog.Use(mine);
            Assert.Same(mine, ModelCatalog.Current);
            Assert.Equal("only", ModelCatalog.Find("only")!.Name);
            Assert.Null(ModelCatalog.Find("deepseek-chat"));
        }
        finally
        {
            ModelCatalog.Use(null);
        }

        Assert.NotNull(ModelCatalog.Find("deepseek-chat"));
        Assert.True(ModelCatalog.Current.Endpoints.Count >= 5);
    }
}
