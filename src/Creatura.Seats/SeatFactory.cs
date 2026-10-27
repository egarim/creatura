namespace Creatura.Seats;

/// <summary>
/// The one place a program builds a model seat by name: the catalog's endpoint, a <see cref="ModelPolicy"/> over a client,
/// and the key named, never read, until the first call. The DeepSeek entries honour <c>DEEPSEEK_MODEL</c> as the first
/// seat did; a model name the catalog lacks gets DeepSeek's endpoint with the model swapped, so a new DeepSeek model
/// can be tried without a catalog edit.
/// </summary>
public static class SeatFactory
{
    public const string DeepSeekDefault = "deepseek-chat";

    /// <summary>The endpoint by catalog name, or an error that lists the names and why any row was skipped.</summary>
    public static ModelEndpoint Endpoint(string name, ModelCatalog? catalog = null)
    {
        var c = catalog ?? ModelCatalog.Current;
        return c.TryFind(name) ?? throw new ArgumentException($"no model '{name}' in the catalog ({c.Source}); known: {string.Join(", ", c.Names)}"
            + (c.Skipped.Count == 0 ? "" : "; skipped: " + string.Join("; ", c.Skipped)));
    }

    /// <summary>A model seat by catalog name.</summary>
    public static ModelPolicy Model(string name, ModelClient? client = null, CallLog? log = null, string contextTag = "", ModelCatalog? catalog = null) =>
        new(Endpoint(name, catalog), client ?? new ModelClient(), log, contextTag);

    /// <summary>DeepSeek's endpoint for a model: the argument, else <c>DEEPSEEK_MODEL</c>, else <c>deepseek-chat</c>.</summary>
    public static ModelEndpoint DeepSeekEndpoint(string? model = null, ModelCatalog? catalog = null)
    {
        var name = model ?? Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? DeepSeekDefault;
        var c = catalog ?? ModelCatalog.Current;
        return c.TryFind(name) ?? Endpoint(DeepSeekDefault, c) with { Name = name, Model = name, Reasoning = name.Contains("reasoner", StringComparison.OrdinalIgnoreCase) };
    }

    /// <summary>The DeepSeek seat: a <see cref="ModelPolicy"/> over DeepSeek's endpoint.</summary>
    public static ModelPolicy DeepSeek(string? model = null, ModelClient? client = null, CallLog? log = null, string contextTag = "deepseek", ModelCatalog? catalog = null) =>
        new(DeepSeekEndpoint(model, catalog), client ?? new ModelClient(), log, contextTag);

    /// <summary>The variable names an endpoint's key is read from when none of them is set; null when a key is present or none is needed.</summary>
    public static string? MissingKey(ModelEndpoint endpoint) => endpoint.KeyEnv is not null && endpoint.ApiKey() is null ? endpoint.KeyEnv : null;
}
