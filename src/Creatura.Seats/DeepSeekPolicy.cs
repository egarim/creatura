namespace Creatura.Seats;

/// <summary>
/// The first model seat, kept as a shim: a <see cref="ModelPolicy"/> over the OpenAI-compatible transport at DeepSeek's
/// endpoint, driven by the JSON protocol as it always was. The key may be given to the client directly (the offline
/// tests do) or read from <c>DEEPSEEK_API_KEY</c> or <c>deepseek</c>; it is never written to output, logs or exceptions.
/// New code names the seat through the catalog (<c>deepseek-chat</c>) and builds a <see cref="ModelPolicy"/>.
/// </summary>
[Obsolete("The DeepSeek seat is ModelPolicy over the OpenAI-compatible transport; build it from ModelCatalog (\"deepseek-chat\") or SeatFactory. This shim keeps the first seat's shape and its offline tests.")]
public sealed class DeepSeekClient
{
    public const string BaseUrl = "https://api.deepseek.com";

    /// <summary>The environment variables the key is read from, first set one wins.</summary>
    public const string KeyEnv = "DEEPSEEK_API_KEY|deepseek";

    private ModelPolicy? _policy;

    public DeepSeekClient(string apiKey, string model = "deepseek-chat", HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("empty API key", nameof(apiKey));
        }

        Model = model;
        Endpoint = new ModelEndpoint(model, Provider.OpenAiCompatible, BaseUrl, model, KeyEnv, NativeTools: false, Reasoning: model.Contains("reasoner", StringComparison.OrdinalIgnoreCase), 0.28m, 0.42m);
        Client = new ModelClient(http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, keys: _ => apiKey);
    }

    public string Model { get; }

    /// <summary>The endpoint as the catalog would describe it: DeepSeek's base URL, the JSON protocol, the key by name.</summary>
    public ModelEndpoint Endpoint { get; }

    /// <summary>The client the shim's policy calls through; the key given to the constructor is the one it sends.</summary>
    public ModelClient Client { get; }

    public int Calls => _policy?.Telemetry.Calls ?? 0;

    public int PromptTokens => (int)(_policy?.Telemetry.PromptTokens ?? 0);

    public int CompletionTokens => (int)(_policy?.Telemetry.CompletionTokens ?? 0);

    /// <summary>The key, or null. Never logged, never written anywhere.</summary>
    public static string? ApiKeyFromEnvironment() =>
        Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
        ?? Environment.GetEnvironmentVariable("deepseek");

    /// <summary>Reads DEEPSEEK_API_KEY (or the lowercase <c>deepseek</c> variable) and DEEPSEEK_MODEL. Never logs the key.</summary>
    public static DeepSeekClient FromEnvironment(HttpClient? http = null)
    {
        var key = ApiKeyFromEnvironment();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("no DeepSeek key found in DEEPSEEK_API_KEY or deepseek");
        }

        var model = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-chat";
        return new DeepSeekClient(key, model, http);
    }

    internal void Bind(ModelPolicy policy) => _policy = policy;
}

/// <summary>
/// A policy backed by DeepSeek: one action per tick, chosen from the tools the architecture makes visible. The model is
/// the decision component only; everything it proposes is validated by <see cref="ModelPolicy"/>, which this wraps.
/// </summary>
[Obsolete("DeepSeekPolicy is a shim over ModelPolicy + OpenAiCompatibleTransport; build the seat from ModelCatalog (\"deepseek-chat\") or SeatFactory instead.")]
public sealed class DeepSeekPolicy : IPolicy
{
    private readonly DeepSeekClient _client;
    private readonly ModelPolicy _policy;
    private readonly List<string> _log = [];

    public DeepSeekPolicy(DeepSeekClient client, CallLog? log = null)
    {
        _client = client;
        _policy = new ModelPolicy(client.Endpoint, client.Client, log, "deepseek");
        client.Bind(_policy);
    }

    public string Name => $"deepseek:{_client.Model}";

    /// <summary>The seat's own one-line account of each tick, as the first seat kept it.</summary>
    public IReadOnlyList<string> Log => _log;

    public ModelPolicy Inner => _policy;

    public int Calls => _client.Calls;

    public int PromptTokens => _client.PromptTokens;

    public int CompletionTokens => _client.CompletionTokens;

    public async Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var suggestion = await _policy.NextAsync(context, cancellationToken).ConfigureAwait(false);
        if (suggestion.Tool is null && suggestion.Rationale.StartsWith("proposed unavailable action '", StringComparison.Ordinal))
        {
            var named = suggestion.Rationale["proposed unavailable action '".Length..].Split('\'')[0];
            _log.Add($"tick {context.Tick}: model proposed '{named}' which is not visible — refused");
        }
        else if (suggestion.Tool is null && suggestion.Rationale.EndsWith("needs a payload and none was supplied", StringComparison.Ordinal))
        {
            _log.Add($"tick {context.Tick}: {suggestion.Rationale.Split(' ')[0]} chosen without a payload — refused");
        }
        else
        {
            _log.Add($"tick {context.Tick}: {suggestion.Tool ?? "null"} — {suggestion.Rationale}");
        }

        return suggestion;
    }
}
