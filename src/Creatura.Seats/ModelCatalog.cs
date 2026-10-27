using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Seats;

/// <summary>
/// One row of <c>models.json</c>, and of the compiled table. The file is a JSON object <c>{ "models": [ row, ... ] }</c>;
/// each row:
/// <code>
/// {
///   "name": "deepseek-chat",              // the seat's name on the command line and in the rows (required, unique)
///   "provider": "openai",                 // ollama | openai (any OpenAI-compatible server: DeepSeek, Azure OpenAI v1, Ollama's /v1) | azure-foundry
///   "baseUrl": "https://api.deepseek.com", // the base the transport appends its path to (optional when baseUrlEnv is set)
///   "baseUrlEnv": "AZURE_AOAI_BASE",      // an environment variable holding the base; it wins over baseUrl when set, and a row
///                                         //   with neither a set variable nor a baseUrl is skipped with a message naming the variable
///   "model": "deepseek-chat",             // what the provider calls the model (a deployment name on Azure)
///   "keyEnv": "DEEPSEEK_API_KEY|deepseek", // environment variable NAMES for the key, first set one wins; null for a keyless endpoint.
///                                         //   Never a key value: the catalog, the rows and the logs only ever hold the name.
///   "nativeTools": true,                  // drive the seat by function calling (tools) or by one JSON object (json)
///   "reasoning": false,                   // a model that thinks before it answers: the cap is widened, reasoning text kept
///   "inputPerMillion": 0.28,              // USD per million tokens from the provider's public list; null = unknown, cost reported as unknown
///   "outputPerMillion": 0.42,
///   "sheet": {                            // what the provider publishes; null = unknown, never zero (optional)
///     "contextWindow": 128000, "contextAsRun": null, "maxOutput": 8192, "knowledgeCutoff": null, "caveat": null
///   }
/// }
/// </code>
/// The file replaces the compiled table as a whole (it does not merge); <c>samples/models.json</c> is the compiled table
/// written out, to copy beside an executable and edit.
/// </summary>
public sealed record CatalogRow
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; init; }
    [JsonPropertyName("baseUrlEnv")] public string? BaseUrlEnv { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("keyEnv")] public string? KeyEnv { get; init; }
    [JsonPropertyName("nativeTools")] public bool NativeTools { get; init; }
    [JsonPropertyName("reasoning")] public bool Reasoning { get; init; }
    [JsonPropertyName("inputPerMillion")] public decimal? InputPerMillion { get; init; }
    [JsonPropertyName("outputPerMillion")] public decimal? OutputPerMillion { get; init; }
    [JsonPropertyName("sheet")] public CatalogSheet? Sheet { get; init; }
}

public sealed record CatalogSheet
{
    [JsonPropertyName("contextWindow")] public int? ContextWindow { get; init; }
    [JsonPropertyName("contextAsRun")] public int? ContextAsRun { get; init; }
    [JsonPropertyName("maxOutput")] public int? MaxOutput { get; init; }
    [JsonPropertyName("knowledgeCutoff")] public string? KnowledgeCutoff { get; init; }
    [JsonPropertyName("caveat")] public string? Caveat { get; init; }

    public ModelSheet ToSheet() => new(ContextWindow, ContextAsRun, MaxOutput, KnowledgeCutoff, Caveat);

    public static CatalogSheet From(ModelSheet s) => new() { ContextWindow = s.ContextWindow, ContextAsRun = s.ContextAsRun, MaxOutput = s.MaxOutput, KnowledgeCutoff = s.KnowledgeCutoff, Caveat = s.Caveat };
}

public sealed record CatalogFile
{
    [JsonPropertyName("models")] public required IReadOnlyList<CatalogRow> Models { get; init; }
}

/// <summary>
/// The models a seat can be: an optional <c>models.json</c> beside the executable, else the compiled table. Hosts that are
/// anyone's own (Azure OpenAI, Azure AI Foundry) are never in the code: their rows name an environment variable for the
/// base and are skipped, with a message, when it is not set. Keys are environment-variable names only.
/// </summary>
public sealed class ModelCatalog
{
    public const string FileName = "models.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly object Gate = new();
    private static ModelCatalog? _current;

    public ModelCatalog(IReadOnlyList<ModelEndpoint> endpoints, IReadOnlyList<string> skipped, string source)
    {
        Endpoints = endpoints;
        Skipped = skipped;
        Source = source;
    }

    public IReadOnlyList<ModelEndpoint> Endpoints { get; }

    /// <summary>One line per row left out because the variable its base URL comes from is not set.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>Where the rows came from: the file's path, or "compiled default".</summary>
    public string Source { get; }

    public IReadOnlyList<string> Names => Endpoints.Select(e => e.Name).ToList();

    public ModelEndpoint? TryFind(string name) => Endpoints.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- the process-wide catalog

    /// <summary>The file beside the executable, when there is one.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The catalog in use: <see cref="DefaultPath"/> when the file exists, else the compiled table against the environment. Built once.</summary>
    public static ModelCatalog Current
    {
        get
        {
            lock (Gate) return _current ??= File.Exists(DefaultPath) ? Load(DefaultPath) : Compile();
        }
    }

    /// <summary>Replaces the catalog in use (a runner's <c>--models-file</c>, a test); null goes back to the default.</summary>
    public static void Use(ModelCatalog? catalog)
    {
        lock (Gate) _current = catalog;
    }

    public static IReadOnlyList<ModelEndpoint> All => Current.Endpoints;

    public static ModelEndpoint? Find(string name) => Current.TryFind(name);

    // ---------------------------------------------------------------- loading

    public static ModelCatalog Load(string path, Func<string, string?>? env = null) => Parse(File.ReadAllText(path), path, env);

    public static ModelCatalog Parse(string json, string source, Func<string, string?>? env = null)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json, Json) ?? throw new FormatException($"{source}: not a models file");
        return Resolve(file.Models, source, env);
    }

    /// <summary>The compiled table against the environment (<paramref name="env"/> for a test; the process environment when null).</summary>
    public static ModelCatalog Compile(Func<string, string?>? env = null) => Resolve(Rows, "compiled default", env);

    /// <summary>The compiled table as <c>models.json</c> text: what <c>samples/models.json</c> holds.</summary>
    public static string DefaultJson() => JsonSerializer.Serialize(new CatalogFile { Models = Rows }, Json) + "\n";

    private static ModelCatalog Resolve(IReadOnlyList<CatalogRow> rows, string source, Func<string, string?>? env)
    {
        env ??= Environment.GetEnvironmentVariable;
        var endpoints = new List<ModelEndpoint>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!seen.Add(row.Name)) throw new FormatException($"{source}: the name '{row.Name}' appears twice");
            var fromEnv = row.BaseUrlEnv is null ? null : env(row.BaseUrlEnv);
            var baseUrl = !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv : row.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                skipped.Add(row.BaseUrlEnv is null
                    ? $"{row.Name}: no baseUrl and no baseUrlEnv; skipped"
                    : $"{row.Name}: {row.BaseUrlEnv} is not set; skipped (set it to your own {Providers.Parse(row.Provider).AsRow()} endpoint's base URL)");
                continue;
            }

            endpoints.Add(new ModelEndpoint(row.Name, Providers.Parse(row.Provider), baseUrl, row.Model, row.KeyEnv, row.NativeTools, row.Reasoning, row.InputPerMillion, row.OutputPerMillion)
            {
                Sheet = row.Sheet?.ToSheet(),
            });
        }

        return new ModelCatalog(endpoints, skipped, source);
    }

    // ---------------------------------------------------------------- the compiled table

    private const string Ollama = "OLLAMA_HOST_URL";
    private const string DeepSeekKey = "DEEPSEEK_API_KEY|deepseek";
    private const string AoaiBase = "AZURE_AOAI_BASE";
    private const string AoaiKey = "AZURE_AOAI_KEY";
    private const string FoundryBase = "AZURE_FOUNDRY_BASE";
    private const string FoundryKey = "AZURE_FOUNDRY_KEY";

    private static CatalogSheet Sheet(int? context, int? asRun, int? maxOutput, string? cutoff, string? caveat) =>
        new() { ContextWindow = context, ContextAsRun = asRun, MaxOutput = maxOutput, KnowledgeCutoff = cutoff, Caveat = caveat };

    /// <summary>
    /// The thirteen seats the research repository knew, prices in USD per million tokens from the providers' public
    /// lists at the time of writing (null: not looked up), sheets from their model pages. Ollama runs every model with
    /// a 4,096-token window unless num_ctx is set, whatever the model's own window is: that is the number the agent
    /// lives in. The Azure rows have no base in code: AZURE_AOAI_BASE (an Azure OpenAI v1 endpoint) and
    /// AZURE_FOUNDRY_BASE (a Foundry model-inference endpoint) must be set, or the rows are skipped.
    /// </summary>
    public static IReadOnlyList<CatalogRow> Rows { get; } =
    [
        // Local, through Ollama. qwen and llama speak native tool calls; phi4-mini answers in text and gets the JSON protocol.
        new() { Name = "qwen2.5:7b", Provider = "ollama", BaseUrl = "http://localhost:11434", BaseUrlEnv = Ollama, Model = "qwen2.5:7b", NativeTools = true, InputPerMillion = 0m, OutputPerMillion = 0m, Sheet = Sheet(32_768, 4_096, 8_192, "2024", "4-bit quantised") },
        new() { Name = "phi4-mini", Provider = "ollama", BaseUrl = "http://localhost:11434", BaseUrlEnv = Ollama, Model = "phi4-mini", InputPerMillion = 0m, OutputPerMillion = 0m, Sheet = Sheet(128_000, 4_096, 4_096, "2024", "4-bit quantised; no native tool calling in this build") },
        new() { Name = "llama3.2:1b", Provider = "ollama", BaseUrl = "http://localhost:11434", BaseUrlEnv = Ollama, Model = "llama3.2:1b", NativeTools = true, InputPerMillion = 0m, OutputPerMillion = 0m, Sheet = Sheet(128_000, 4_096, 4_096, "2023-12", "4-bit quantised") },

        // DeepSeek's own API, OpenAI-compatible. The reasoner returns reasoning_content.
        new() { Name = "deepseek-chat", Provider = "openai", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat", KeyEnv = DeepSeekKey, NativeTools = true, InputPerMillion = 0.28m, OutputPerMillion = 0.42m, Sheet = Sheet(128_000, null, 8_192, null, null) },
        new() { Name = "deepseek-reasoner", Provider = "openai", BaseUrl = "https://api.deepseek.com", Model = "deepseek-reasoner", KeyEnv = DeepSeekKey, Reasoning = true, InputPerMillion = 0.28m, OutputPerMillion = 0.42m, Sheet = Sheet(128_000, null, 64_000, null, "thinking tokens are billed as output") },

        // Azure OpenAI, through its OpenAI-compatible v1 endpoint (the base comes from the environment). gpt-5.x are reasoning models.
        new() { Name = "gpt-4.1-mini", Provider = "openai", BaseUrlEnv = AoaiBase, Model = "gpt-4.1-mini", KeyEnv = AoaiKey, NativeTools = true, InputPerMillion = 0.40m, OutputPerMillion = 1.60m, Sheet = Sheet(1_047_576, null, 32_768, "2024-06", null) },
        new() { Name = "gpt-4.1-nano", Provider = "openai", BaseUrlEnv = AoaiBase, Model = "gpt-4.1-nano", KeyEnv = AoaiKey, NativeTools = true, InputPerMillion = 0.10m, OutputPerMillion = 0.40m, Sheet = Sheet(1_047_576, null, 32_768, "2024-06", null) },
        new() { Name = "gpt-5.4-mini", Provider = "openai", BaseUrlEnv = AoaiBase, Model = "gpt-5.4-mini", KeyEnv = AoaiKey, NativeTools = true, Reasoning = true, Sheet = Sheet(null, null, null, null, "reasoning model; temperature fixed; cap covers thinking and answer") },
        new() { Name = "gpt-5.4-nano", Provider = "openai", BaseUrlEnv = AoaiBase, Model = "gpt-5.4-nano", KeyEnv = AoaiKey, NativeTools = true, Reasoning = true, Sheet = Sheet(null, null, null, null, "reasoning model; temperature fixed; cap covers thinking and answer") },

        // Azure AI Foundry model deployments (Phi, DeepSeek) through a model-inference endpoint (the base comes from the environment).
        new() { Name = "phi-4-mini@azure", Provider = "azure-foundry", BaseUrlEnv = FoundryBase, Model = "phi-4-mini", KeyEnv = FoundryKey, Sheet = Sheet(128_000, null, 4_096, "2024", null) },
        new() { Name = "phi-4@azure", Provider = "azure-foundry", BaseUrlEnv = FoundryBase, Model = "phi-4", KeyEnv = FoundryKey, Sheet = Sheet(16_000, null, 16_000, "2024", null) },
        new() { Name = "phi-4-reasoning@azure", Provider = "azure-foundry", BaseUrlEnv = FoundryBase, Model = "phi-4-reasoning", KeyEnv = FoundryKey, Reasoning = true, Sheet = Sheet(32_000, null, 32_000, "2024", "reasoning model") },
        new() { Name = "deepseek-v31@azure", Provider = "azure-foundry", BaseUrlEnv = FoundryBase, Model = "deepseek-v31", KeyEnv = FoundryKey, NativeTools = true, Sheet = Sheet(128_000, null, 8_192, null, null) },
    ];
}
