using Creatura.Seats;

namespace Creatura.Worlds.Web;

/// <summary>
/// A tester by name, as a run or a matrix names it: <c>script:&lt;path&gt;</c> (the offline seat), <c>script:demo</c> (the
/// demo's bundled script for the task), or <c>model:&lt;catalog name&gt;</c> (a persona over that model, with the adapter's
/// escalation model when it names one). <see cref="Check"/> says what is wrong before any browser starts;
/// <see cref="Build"/> seats the tester in an opened world.
/// </summary>
public sealed class UxSeats
{
    public const string BundledScript = "script:demo";

    private readonly ModelCatalog _catalog;
    private readonly ModelClient _client;
    private readonly CallLog? _log;

    public UxSeats(ModelCatalog? catalog = null, ModelClient? client = null, CallLog? log = null)
    {
        _catalog = catalog ?? ModelCatalog.Current;
        _client = client ?? new ModelClient();
        _log = log;
    }

    /// <summary>The model the persona escalates to when stuck: this name, else the adapter's <c>limits.models.escalate</c>, else none.</summary>
    public string? Escalate { get; init; }

    public static bool IsModel(string tester) => !tester.StartsWith("script:", StringComparison.Ordinal);

    private static string ModelName(string tester) => tester.StartsWith("model:", StringComparison.Ordinal) ? tester["model:".Length..] : tester;

    /// <summary>What is wrong with a tester for this adapter, or null: a missing script, an unknown model, a model without its key.</summary>
    public string? Check(string tester, UxAdapter adapter)
    {
        if (!IsModel(tester))
        {
            var path = tester["script:".Length..];
            return path == "demo" || File.Exists(path) ? null : $"the script '{path}' does not exist";
        }

        var name = ModelName(tester);
        if (_catalog.TryFind(name) is not { } decide) return $"no model '{name}' in the catalog ({_catalog.Source}); known: {string.Join(", ", _catalog.Names)}";
        var escalateName = Escalate ?? adapter.Limits.Models.GetValueOrDefault("escalate");
        var escalate = escalateName is null ? null : _catalog.TryFind(escalateName);
        if (escalateName is not null && escalate is null) return $"no model '{escalateName}' to escalate to in the catalog";
        foreach (var endpoint in new[] { decide, escalate }.OfType<ModelEndpoint>())
        {
            if (SeatFactory.MissingKey(endpoint) is { } missing) return $"{endpoint.Name} needs a key in {missing}: set it and retry";
        }

        return null;
    }

    /// <summary>The tester seated in an opened world, as this persona on this device. Call <see cref="Check"/> first.</summary>
    public IPolicy Build(string tester, UxAdapter adapter, UxTask task, UxPersona persona, UxEnvironment environment, WebWorld world)
    {
        if (!IsModel(tester))
        {
            var path = tester["script:".Length..];
            return path == "demo"
                ? new WebScriptPolicy(DemoFiles.ScriptFor(task.Id), () => world.Current, BundledScript)
                : WebScriptPolicy.FromFile(path, () => world.Current);
        }

        var decide = _catalog.TryFind(ModelName(tester)) ?? throw new ArgumentException($"no model '{ModelName(tester)}' in the catalog");
        var escalateName = Escalate ?? adapter.Limits.Models.GetValueOrDefault("escalate");
        var escalate = escalateName is null ? null : _catalog.TryFind(escalateName);
        return new PersonaPolicy(() => world.Current, persona, environment, decide, _client, escalate, adapter.Limits, _log);
    }
}
