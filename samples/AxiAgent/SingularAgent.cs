// AXI (#91, CRE-02; Singular ADR-0108 §7 steps 3-4): Creatura's agent client for one Singular tenant and app, over
// HTTP only. It polls the accepted proposals, runs the read-only capability `suggest-a-group` under Creatura's control
// layer, and posts axi/v1 events and one outcome per proposal. Only ids, codes and numbers go on the wire.
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Creatura.Worlds;

namespace Creatura.Samples.AxiAgent;

/// <summary>
/// One tenant's agent. The caller gives it an <see cref="HttpClient"/> already holding the tenant's service key
/// (<c>Authorization: Service …</c>), <c>X-App-Id</c> and <c>X-Tenant-Id</c>; this class never sees the key. Call
/// <see cref="StepAsync"/> in a loop and wait the time it returns.
/// </summary>
public sealed class SingularAgent(HttpClient http, TimeProvider clock, Action<string>? log = null)
{
    public const string Capability = "suggest-a-group";
    public const string Proposals = "api/app/experience/agent/proposals";
    public const string Intake = "api/app/experience/agent";
    public const string PublicGroups = "api/app/data/groups?visibility=eq.public&limit=5";

    /// <summary>Singular serves the list at most once per 10 s per tenant and app (429 otherwise).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // proposal id → its lines, built once and re-sent unchanged (same eventIds), so a retry is idempotent; null = not run yet.
    private readonly Dictionary<string, JsonArray?> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _done = new(StringComparer.Ordinal);
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    /// <summary>Proposals this agent is finished with, and why: <c>recorded</c>, or the refusal that stopped it.</summary>
    public IReadOnlyDictionary<string, string> Done => _done;

    /// <summary>One cycle when it is due: poll, then settle each proposal. Returns how long to wait before the next call.</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (now < _next) return _next - now;
        _next = now + PollInterval;
        try
        {
            using (var poll = await http.GetAsync($"{Proposals}?state=accepted", ct))
            {
                if (poll.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var after = poll.Headers.RetryAfter?.Delta ?? PollInterval;
                    _next = now + (after > PollInterval ? after : PollInterval);
                    return _next - now;
                }
                if (poll.IsSuccessStatusCode)
                {
                    foreach (var p in (await poll.Content.ReadFromJsonAsync<ProposalList>(Web, ct))?.Proposals ?? [])
                        if (p.Id is { } id && p.CapabilityId == Capability && !_done.ContainsKey(id)) _pending.TryAdd(id, null);
                }
                else log?.Invoke($"poll {(int)poll.StatusCode}");
            }
            foreach (var id in _pending.Keys.ToList()) await SettleAsync(id, ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log?.Invoke($"cycle failed: {e.GetType().Name}"); // the type only: a message could carry a URL or a body
        }
        var wait = _next - clock.GetUtcNow();
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>Reads the proposal, runs it once, posts its lines; finished on <c>outcome: recorded</c> or a refusal.</summary>
    private async Task SettleAsync(string id, CancellationToken ct)
    {
        ProposalView? view;
        using (var get = await http.GetAsync($"{Proposals}/{Uri.EscapeDataString(id)}", ct))
        {
            // 404: expired, answered by an opt-out, or not this agent's; nothing left to report on.
            if (get.StatusCode == HttpStatusCode.NotFound) { Finish(id, "proposal.gone"); return; }
            if (!get.IsSuccessStatusCode) return;
            view = await get.Content.ReadFromJsonAsync<ProposalView>(Web, ct);
        }
        if (view?.Outcome == "recorded") { Finish(id, "recorded"); return; }
        if (view?.GoalId is not { } goal) { Finish(id, "proposal.no-goal"); return; }

        var batch = _pending[id] ??= await RunAsync(id, goal, ct);
        using var post = await http.PostAsync(Intake, new StringContent(batch.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var code = (int)post.StatusCode;
        if (code is >= 400 and < 500 and not 408 and not 429) { Finish(id, $"refused {code}"); return; }
        if (!post.IsSuccessStatusCode) return; // 5xx: the same lines next cycle
        var answer = await post.Content.ReadFromJsonAsync<IntakeAnswer>(Web, ct);
        // 409: the proposal is unknown or not accepted (§7.4); any other refused item means these lines never pass.
        if (answer?.Results?.FirstOrDefault(r => r.Status >= 400) is { } refused) Finish(id, $"refused {refused.Status} {refused.Reason}");
        // Otherwise the next cycle's GET says whether the outcome is recorded; until then the same lines are re-sent.
    }

    private void Finish(string id, string why)
    {
        _pending.Remove(id);
        _done[id] = why;
        log?.Invoke($"proposal {id}: {why}");
    }

    /// <summary>Runs <c>suggest-a-group</c> under Creatura's control layer, and returns its events and one outcome.</summary>
    private async Task<JsonArray> RunAsync(string proposalId, string goalId, CancellationToken ct)
    {
        var lines = new AxiLines(proposalId, goalId, clock);
        var world = new GroupsWorld(() => ReadGroups(ct));
        var agent = AgentCatalog.Make("orienting", new AgentBuild(new PlannerPolicy(), GroupsWorld.Task, AgentOptions.With(lines)));
        // The world's read is synchronous (IWorld.Act); off the caller's context so it cannot deadlock.
        await Task.Run(() => agent.RunAsync(world, maxTicks: 3, ct), ct);

        var outcome = lines.Line("axi.outcome", world.Ticks);
        outcome["proposalId"] = proposalId;
        outcome["status"] = world.Has(GroupsWorld.Suggested) ? "succeeded" : "failed";
        var reported = new JsonObject { ["lies"] = lines.Lies };
        if (world.Refs.Count > 0)
            reported["suggested"] = new JsonArray(world.Refs.Take(5).Select(k => (JsonNode)new JsonObject { ["collection"] = "groups", ["key"] = k }).ToArray());
        outcome["reported"] = reported;
        // No `confirmed`: Singular computes it as the person (§6); the agent's own success claim is never sent as truth.
        return new JsonArray([.. lines.Events, outcome]);
    }

    /// <summary>The capability's one read: member-wide `groups` where visibility = public. Only key and visibility are bound.</summary>
    private GroupsRead ReadGroups(CancellationToken ct)
    {
        try
        {
            using var r = http.GetAsync(PublicGroups, ct).GetAwaiter().GetResult();
            if (!r.IsSuccessStatusCode) return new(true, false, []);
            var page = r.Content.ReadFromJsonAsync<GroupsPage>(Web, ct).GetAwaiter().GetResult();
            var rows = page?.Items ?? [];
            // A row the filter should have kept out, or one without a usable key: the read claimed what it did not do.
            var leaked = rows.Any(g => g.Payload?.Visibility != "public" || g.Key is not { Length: > 0 and <= 100 });
            return new(false, leaked, leaked ? [] : rows.Select(g => g.Key!).ToList());
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(true, false, []);
        }
    }

    // The wire, bound field by field: nothing else is read (no subject, no owner, no payload but visibility).
    private sealed record ProposalList(List<ProposalView>? Proposals);
    private sealed record ProposalView(string? Id, string? CapabilityId, string? GoalId, string? Outcome);
    private sealed record IntakeAnswer(List<IntakeItem>? Results);
    private sealed record IntakeItem(int Status, string? Reason);
    private sealed record GroupsPage(List<GroupRow>? Items);
    private sealed record GroupRow(string? Key, GroupPayload? Payload);
    private sealed record GroupPayload(string? Visibility);
}

/// <summary>What the read came to: a failure (honest), a leak (a lie), or the keys of public groups.</summary>
public sealed record GroupsRead(bool Failed, bool Leaked, IReadOnlyList<string> Keys);

/// <summary>
/// The capability as a Creatura world: one read-kind tool that promises <see cref="Suggested"/>. The registry gains it
/// only when the read returned public groups, so the control layer judges the tool the usual way: a failed read is an
/// honest failure, a read that served a non-public row reports success the registry does not back (a lie, quarantined).
/// </summary>
public sealed class GroupsWorld(Func<GroupsRead> read)
    : CatalogueWorld([new Tool(SingularAgent.Capability, [], [Suggested], "read")], [])
{
    public const string Suggested = "group.suggested";

    public static readonly AgentTask Task = new(
        "suggest a group", "A public group to join is suggested.", f => f.Contains(Suggested),
        w => w.Has(Suggested) ? 1 : 0, w => w.Has(Suggested) ? 1 : 0, [SingularAgent.Capability]);

    public IReadOnlyList<string> Refs { get; private set; } = [];

    protected override ActionOutcome Land(Tool tool, string? observation)
    {
        var r = read();
        if (r.Leaked) return new(tool.Id, true, [], null);
        if (r.Failed || r.Keys.Count == 0) return new(tool.Id, false, [], null); // nothing to suggest is honest too
        Refs = r.Keys;
        return base.Land(tool, observation);
    }
}

/// <summary>Creatura's verdicts as axi/v1 events, all under the proposal's id as correlation id. Observer only.</summary>
public sealed class AxiLines(string proposalId, string goalId, TimeProvider clock) : IAgentExtension
{
    public string Name => "axi-lines";

    public List<JsonObject> Events { get; } = [];

    public int Lies { get; private set; }

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        if (verdict == ActionVerdict.Lied) Lies++;
        var e = Line("axi.event", context.Tick);
        e["kind"] = verdict switch { ActionVerdict.Landed => "tool.completed", ActionVerdict.Lied => "tool.lied", _ => "tool.failed" };
        e["capabilityId"] = tool.Id;
        Events.Add(e);
    }

    /// <summary>The envelope: no sessionId (Singular attributes by the proposal), never `detail`.</summary>
    public JsonObject Line(string type, int tick) => new()
    {
        ["v"] = "axi/v1",
        ["type"] = type,
        ["eventId"] = Guid.NewGuid().ToString(),
        ["correlationId"] = proposalId,
        ["at"] = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        ["tick"] = tick,
        ["goalId"] = goalId,
    };
}
