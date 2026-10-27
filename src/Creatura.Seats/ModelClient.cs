using System.Diagnostics;

namespace Creatura.Seats;

/// <summary>
/// One client for every catalogued endpoint: the endpoint's <see cref="Provider"/> picks the transport, the endpoint's
/// key name picks the key, and one rule retries. A stalled connection is not a decision: a dropped connection, a
/// 401, a 408, a 429 and every 5xx are tried once more after a pause; any other 4xx is final. Keys are read at call
/// time and never written to a log, a reply or an exception.
/// </summary>
public sealed class ModelClient
{
    private readonly IReadOnlyDictionary<Provider, IModelTransport> _transports;
    private readonly Func<ModelEndpoint, string?> _keys;

    /// <param name="http">The client every default transport shares; a two-minute timeout when null.</param>
    /// <param name="transports">The transports by provider; the three built-in ones over <paramref name="http"/> when null.</param>
    /// <param name="keys">How a key is found for an endpoint; <see cref="ModelEndpoint.ApiKey"/> (the environment) when null.</param>
    public ModelClient(HttpClient? http = null, IEnumerable<IModelTransport>? transports = null, Func<ModelEndpoint, string?>? keys = null)
    {
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var list = transports?.ToList() ?? [new OllamaTransport(http), new OpenAiCompatibleTransport(http), new AzureFoundryTransport(http)];
        _transports = list.ToDictionary(t => t.Provider);
        _keys = keys ?? (e => e.ApiKey());
    }

    /// <summary>The pause before the one retry. A second by default; a test sets zero.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public IReadOnlyCollection<Provider> Providers => _transports.Keys.ToList();

    public IModelTransport TransportFor(Provider provider) =>
        _transports.TryGetValue(provider, out var t) ? t : throw new ArgumentException($"no transport for {provider}; this client has {string.Join(", ", _transports.Keys)}");

    /// <summary>Whether a failed call with this status is tried once more: no response (0), 401, 408, 429, or any 5xx.</summary>
    public static bool Retryable(int status) => status is 0 or 401 or 408 or 429 or >= 500;

    public async Task<ModelReply> ChatAsync(
        ModelEndpoint endpoint,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        bool jsonMode,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        var transport = TransportFor(endpoint.Provider);
        var key = _keys(endpoint);
        if (endpoint.KeyEnv is not null && key is null) return ModelReply.Failure(0, 0, $"no key in {endpoint.KeyEnv}");
        var request = new ModelRequest { Endpoint = endpoint, Messages = messages, Tools = tools, JsonMode = jsonMode, MaxTokens = maxTokens, ApiKey = key };

        ModelReply? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                last = await transport.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                last = ModelReply.Failure(sw.Elapsed.TotalSeconds, 0, $"{exception.GetType().Name}: {Secrets.Scrub(exception.Message)}");
            }

            if (!last.Failed || !Retryable(last.HttpStatus)) return last;
            if (attempt == 0 && RetryDelay > TimeSpan.Zero) await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return last!;
    }

    /// <summary>Kept for callers of the old client; <see cref="Secrets.Scrub"/> is the rule.</summary>
    public static string Scrub(string s) => Secrets.Scrub(s);
}
