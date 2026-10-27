using Creatura.Bench;

namespace Creatura.Seats;

/// <summary>What a seat name resolves to.</summary>
public enum SeatKind
{
    /// <summary>The built-in breadth-first planner (<c>bfs</c>).</summary>
    Planner,

    /// <summary>A script of tool ids replayed one per tick (<c>scripted:&lt;path&gt;</c>).</summary>
    Scripted,

    /// <summary>A catalogued model (<c>model:&lt;name&gt;</c>, or the bare name).</summary>
    Model,
}

/// <summary>
/// A seat that replays a script: one tool id per line, in order, one per tick; a blank line, <c>-</c>, <c>null</c> or
/// <c>no_action</c> chooses nothing that tick; a line starting with <c>#</c> is a comment and no tick, and a trailing
/// <c>#</c> comment is dropped; past the end the seat chooses nothing. A scripted tool that is not visible is refused, as a model's would be: the script cannot route
/// around the architecture either.
/// </summary>
public sealed class ScriptedPolicy : IPolicy
{
    private readonly IReadOnlyList<string?> _steps;
    private int _next;

    public ScriptedPolicy(IEnumerable<string?> steps, string name = "scripted")
    {
        _steps = steps.ToList();
        Name = name;
    }

    public static ScriptedPolicy FromFile(string path) =>
        new(File.ReadLines(path).Where(l => !l.TrimStart().StartsWith('#')).Select(Step), "scripted:" + Path.GetFileName(path));

    private static string? Step(string line)
    {
        var s = line.Trim();
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s[..hash].Trim();
        return s is "" or "-" or "null" or ModelPolicy.NoAction ? null : s;
    }

    public string Name { get; }

    public int Position => _next;

    public int Length => _steps.Count;

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        if (_next >= _steps.Count) return Task.FromResult(new PolicySuggestion(null, "script exhausted"));
        var step = _steps[_next++];
        if (step is null) return Task.FromResult(new PolicySuggestion(null, $"script step {_next}: no action"));
        if (!context.Tools.Any(t => string.Equals(t.Id, step, StringComparison.Ordinal)))
            return Task.FromResult(new PolicySuggestion(null, $"proposed unavailable action '{step}': script step {_next}"));
        return Task.FromResult(new PolicySuggestion(step, $"script step {_next}"));
    }
}

/// <summary>
/// The seam <see cref="BenchOptions.Seats"/> plugs into: a <see cref="SeatSpec"/> (<c>bfs</c> | <c>scripted:&lt;path&gt;</c> |
/// <c>model:&lt;catalog name&gt;</c>, or a bare catalog name) becomes an <see cref="IPolicy"/>. A model seat gets a
/// <see cref="ModelPolicy"/> over the resolver's client, logging to the path the resolver's <c>logFor</c> names for the spec
/// (no log when it names null); the policy last built is kept on <see cref="LastModel"/> so a runner can read its telemetry.
/// </summary>
public sealed class SeatResolver
{
    private readonly ModelClient _client;
    private readonly ModelCatalog? _catalog;
    private readonly Func<SeatSpec, string?>? _logFor;

    /// <param name="client">The client model seats call through; a default one when null.</param>
    /// <param name="catalog">The catalog names are looked up in; <see cref="ModelCatalog.Current"/> when null.</param>
    /// <param name="logFor">Where a model seat's call log goes for a spec; null (the default) logs nowhere.</param>
    public SeatResolver(ModelClient? client = null, ModelCatalog? catalog = null, Func<SeatSpec, string?>? logFor = null)
    {
        _client = client ?? new ModelClient();
        _catalog = catalog;
        _logFor = logFor;
    }

    public ModelCatalog Catalog => _catalog ?? ModelCatalog.Current;

    /// <summary>The model policy most recently resolved, or null when the last seat was not a model.</summary>
    public ModelPolicy? LastModel { get; private set; }

    /// <summary>Raised with every seat resolved.</summary>
    public event Action<SeatSpec, IPolicy>? Seated;

    /// <summary>The resolver as <see cref="BenchOptions.Seats"/> takes it.</summary>
    public Func<SeatSpec, IPolicy> Seats => Resolve;

    public static SeatKind KindOf(SeatSpec spec) =>
        spec.IsPlanner ? SeatKind.Planner
        : spec.Name.StartsWith("scripted:", StringComparison.Ordinal) ? SeatKind.Scripted
        : SeatKind.Model;

    /// <summary>The catalog name a model spec names: <c>model:x</c> and <c>x</c> both name <c>x</c>.</summary>
    public static string ModelName(SeatSpec spec) => spec.Name.StartsWith("model:", StringComparison.Ordinal) ? spec.Name["model:".Length..] : spec.Name;

    /// <summary>The endpoint a model spec resolves to, or null for the planner, a script or a name the catalog lacks.</summary>
    public ModelEndpoint? EndpointFor(SeatSpec spec) => KindOf(spec) == SeatKind.Model ? Catalog.TryFind(ModelName(spec)) : null;

    public IPolicy Resolve(SeatSpec spec)
    {
        IPolicy policy;
        switch (KindOf(spec))
        {
            case SeatKind.Planner:
                LastModel = null;
                policy = new PlannerPolicy();
                break;
            case SeatKind.Scripted:
                LastModel = null;
                var path = spec.Name["scripted:".Length..];
                if (!File.Exists(path)) throw new ArgumentException($"the seat script '{path}' does not exist");
                policy = ScriptedPolicy.FromFile(path);
                break;
            default:
                var name = ModelName(spec);
                var endpoint = Catalog.TryFind(name)
                    ?? throw new ArgumentException($"no model '{name}' in the catalog ({Catalog.Source}); known: {string.Join(", ", Catalog.Names)}"
                        + (Catalog.Skipped.Count == 0 ? "" : "; skipped: " + string.Join("; ", Catalog.Skipped)));
                var logPath = _logFor?.Invoke(spec);
                var model = new ModelPolicy(endpoint, _client, logPath is null ? null : new CallLog(logPath), spec.Name);
                LastModel = model;
                policy = model;
                break;
        }

        Seated?.Invoke(spec, policy);
        return policy;
    }
}
