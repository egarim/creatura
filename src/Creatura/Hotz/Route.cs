using System.Text.Json;
using System.Text.Json.Serialization;
using Creatura;

namespace Creatura.Hotz;

/// <summary>What the citizen did with a flag: waved it away (dismissed) or let it stand.</summary>
public sealed record CitizenResponse(int Tick, bool Dismissed);

/// <summary>
/// One recorded run: every claim the office's agent made, the registry after each, what the citizen's agent
/// decided and what the citizen did. The archive of routes is the test: a change to the agent is judged by
/// replaying it. comma's process replay (02:15:50), with the case file as the route.
/// </summary>
public sealed record Route(
    string World,
    string OfficeAgent,
    IReadOnlyDictionary<string, bool> Barriers,
    IReadOnlyList<Claim> Claims,
    IReadOnlyList<Judgement> Judgements,
    IReadOnlyList<CitizenResponse> Responses)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Route FromJson(string json) => JsonSerializer.Deserialize<Route>(json, Json) ?? throw new InvalidDataException("not a route");
}

/// <summary>
/// Wraps the world the office's agent acts on and records every <see cref="IWorld.Act"/> as a <see cref="Claim"/>,
/// with the registry right after. The office's agent cannot tell; the citizen's agent reads the claims live.
/// Tick is the world's action count, so one claim lines up with one effect event in any agent's trace.
/// </summary>
public sealed class RouteRecorder : IWorld
{
    private readonly IWorld _inner;
    private readonly List<Claim> _claims = [];

    public RouteRecorder(IWorld inner) => _inner = inner;

    public IWorld Inner => _inner;

    public IReadOnlyList<Claim> Claims => _claims;

    /// <summary>Called with each claim as it is made. The citizen's agent hooks in here.</summary>
    public Action<Claim>? OnClaim { get; set; }

    public IReadOnlySet<string> Facts => _inner.Facts;

    public IReadOnlyList<Tool> AvailableTools => _inner.AvailableTools;

    public IReadOnlyList<Tool> AllTools => _inner.AllTools;

    public int Ticks => _inner.Ticks;

    public bool Has(string fact) => _inner.Has(fact);

    public Tool? Tool(string id) => _inner.Tool(id);

    public bool EffectsLanded(Tool tool) => _inner.EffectsLanded(tool);

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        var tool = _inner.Tool(toolId);
        var outcome = _inner.Act(toolId, payload);
        var claim = new Claim(
            _claims.Count + 1,
            toolId,
            outcome.Succeeded,
            outcome.Observation,
            tool?.Grants ?? [],
            _inner.Facts.Order(StringComparer.Ordinal).ToList());
        _claims.Add(claim);
        OnClaim?.Invoke(claim);
        return outcome;
    }
}

/// <summary>One line of a replay diff: the same claim, judged differently by two versions of the agent.</summary>
public sealed record ReplayDifference(int Tick, string Tool, string Before, string After);

/// <summary>
/// Replays recorded routes through a configuration of the citizen's agent and diffs the decisions. A change
/// to the agent, a threshold, a check, a judge, is accepted only after the owner has looked at this diff.
/// Hotz asks why software should run only in its owner's data centre and not on a laptop (Lex Fridman Podcast #387, 02:17:03);
/// this runs anywhere.
/// </summary>
public static class Replay
{
    /// <summary>Judge every claim of a recorded route again with the given agent configuration.</summary>
    public static IReadOnlyList<Judgement> Run(Route route, HotzAgent agent)
    {
        var fresh = agent.Configure();
        fresh.Prepare(new ReplayWorld(route));
        foreach (var claim in route.Claims)
        {
            fresh.Observe(claim);
            var response = route.Responses.FirstOrDefault(r => r.Tick == claim.Tick);
            if (response is { Dismissed: true })
            {
                fresh.Dismiss(claim.Tick);
            }
        }

        fresh.CloseAfterReplay();
        return fresh.Judgements;
    }

    public static IReadOnlyList<ReplayDifference> Diff(IReadOnlyList<Judgement> before, IReadOnlyList<Judgement> after)
    {
        var diffs = new List<ReplayDifference>();
        var byTick = after.ToDictionary(j => (j.Tick, j.Tool));
        foreach (var b in before)
        {
            if (!byTick.TryGetValue((b.Tick, b.Tool), out var a))
            {
                diffs.Add(new ReplayDifference(b.Tick, b.Tool, Describe(b), "(absent)"));
                continue;
            }

            if (a.Verdict != b.Verdict || a.Shown != b.Shown)
            {
                diffs.Add(new ReplayDifference(b.Tick, b.Tool, Describe(b), Describe(a)));
            }
        }

        foreach (var a in after.Where(a => before.All(b => (b.Tick, b.Tool) != (a.Tick, a.Tool))))
        {
            diffs.Add(new ReplayDifference(a.Tick, a.Tool, "(absent)", Describe(a)));
        }

        return diffs.OrderBy(d => d.Tick).ToList();
    }

    private static string Describe(Judgement j) => $"{j.Verdict}{(j.Shown ? ", shown" : ", logged")}";

    /// <summary>
    /// A world rebuilt from a route's final registry and the tools its claims named: enough for the check map.
    /// Tools are reconstructed from the claims' promises, so a replayed check is exactly the recorded promise.
    /// </summary>
    private sealed class ReplayWorld : IWorld
    {
        private readonly IReadOnlySet<string> _facts;
        private readonly IReadOnlyList<Tool> _tools;

        public ReplayWorld(Route route)
        {
            _facts = route.Claims.Count == 0 ? new HashSet<string>() : route.Claims[^1].Registry.ToHashSet(StringComparer.Ordinal);
            _tools = route.Claims
                .Where(c => !c.IsClosure)
                .GroupBy(c => c.Tool, StringComparer.Ordinal)
                .Select(g => new Tool(g.Key, [], g.First().Promised, "recorded"))
                .ToList();
        }

        public IReadOnlySet<string> Facts => _facts;

        public IReadOnlyList<Tool> AvailableTools => _tools;

        public IReadOnlyList<Tool> AllTools => _tools;

        public int Ticks => 0;

        public bool Has(string fact) => _facts.Contains(fact);

        public Tool? Tool(string id) => _tools.FirstOrDefault(t => t.Id == id);

        public bool EffectsLanded(Tool tool) => tool.Grants.All(_facts.Contains);

        public ActionOutcome Act(string toolId, string? payload = null) => new(toolId, false, [], "replay world: read only");
    }
}
