using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Creatura.Samples.AxiAgent;

namespace Creatura.Tests;

/// <summary>
/// AXI (#91, CRE-02): samples/AxiAgent against a fake Singular built from the shared vectors (axi-contract/, see
/// <see cref="AxiEnvelopeTests"/>): the proposal, goals and agent capabilities are a vector's context, every posted line
/// is checked against the copied axi/v1 schema, and the routes answer as Singular's do (AxiEndpoints, AxiService on
/// origin/main): 202 per item, only failure kinds kept and at most 2 per proposal, duplicate on a retry, 409 for a
/// proposal that is not accepted, 400 for an unknown capability, 429 with Retry-After for a poll under 10 s, 404
/// <c>proposal.unknown</c> for a gone proposal and for a failed read alike.
/// </summary>
public sealed class AxiAgentTests
{
    private const string Sentinel = "kc-sub-0f3a-SENTINEL"; // a person's subject; it must never reach the wire

    [Fact]
    public async Task Polls_at_most_once_every_10_s_and_honours_Retry_After()
    {
        var (fake, agent) = Make();
        fake.Throttles.Enqueue((30, false)); // the first poll is answered 429, Retry-After: 30
        fake.Throttles.Enqueue((45, true)); // the second, Retry-After as an HTTP date 45 s on
        for (var i = 0; i < 120; i++)
        {
            await agent.StepAsync();
            fake.Clock.Advance(TimeSpan.FromSeconds(1)); // called every second; the agent must hold back itself
        }

        Assert.Empty(fake.TooSoon);
        Assert.True(fake.Polls[1] - fake.Polls[0] >= TimeSpan.FromSeconds(30), "Retry-After was not honoured");
        Assert.True(fake.Polls[2] - fake.Polls[1] >= TimeSpan.FromSeconds(45), "a Retry-After date was not honoured");
        Assert.All(fake.Polls.Zip(fake.Polls.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromSeconds(10)));
        Assert.InRange(fake.Polls.Count, 6, 8); // 0, 30, 75, 85, ... 115
    }

    [Fact]
    public async Task Runs_suggest_a_group_and_posts_events_and_one_outcome_until_recorded()
    {
        var (fake, agent) = Make();
        await Cycles(fake, agent, 3);

        Assert.Contains($"proposal {fake.ProposalId}: recorded", fake.Log);
        Assert.Single(fake.Posts); // recorded after the first post; the next cycle's GET shows it and nothing is re-sent
        Assert.Equal(1, fake.Outcomes);
        var lines = fake.PostedLines().ToList();
        var outcome = Assert.Single(lines, l => (string?)l["type"] == "axi.outcome");
        Assert.Equal(fake.ProposalId, (string?)outcome["proposalId"]);
        Assert.Equal("succeeded", (string?)outcome["status"]);
        Assert.False(outcome.ContainsKey("confirmed"));
        Assert.False(((JsonObject)outcome["reported"]!).ContainsKey("agentConfirmed"));
        var refs = (JsonArray)outcome["reported"]!["suggested"]!;
        Assert.Equal(5, refs.Count); // the fake serves 7 public groups
        Assert.All(refs, r => Assert.Equal("groups", (string?)r!["collection"]));
        Assert.Contains(lines, l => (string?)l["kind"] == "tool.completed");
        Assert.Empty(fake.Refused);
    }

    [Fact]
    public async Task A_retry_resends_the_same_lines_and_stores_nothing_twice()
    {
        var (fake, agent) = Make();
        fake.GroupsStatus = HttpStatusCode.InternalServerError; // tool.failed: a kind Singular keeps, so its dedupe matters
        fake.PostAnswers.Enqueue(HttpStatusCode.ServiceUnavailable); // refused before intake
        fake.PostAnswers.Enqueue(HttpStatusCode.NotFound); // agent.only: the caller check failed, nothing read
        fake.PostAnswers.Enqueue(HttpStatusCode.Accepted); // fail closed: 202 dropped, no results
        fake.PostAnswers.Enqueue(HttpStatusCode.InternalServerError); // stored, then the answer is lost
        fake.StaleReads = 1; // and the next read still shows no outcome, so the stored lines go once more
        await Cycles(fake, agent, 8);

        Assert.Contains($"proposal {fake.ProposalId}: recorded", fake.Log);
        Assert.Equal(5, fake.Posts.Count);
        Assert.All(fake.Posts, p => Assert.Equal(fake.Posts[0], p)); // same eventIds, same outcome: what Singular dedupes on
        Assert.Equal(1, fake.Outcomes);
        Assert.Equal(1, fake.Signals);
        Assert.Equal(2, fake.Duplicates); // the resent failure and outcome, both answered duplicate
        Assert.Equal(1, fake.GroupReads); // ran once: a retry re-sends, it does not re-run the capability
        Assert.Empty(fake.Refused);
    }

    [Fact]
    public async Task A_409_stops_the_retries()
    {
        var (fake, agent) = Make();
        fake.RefuseOutcome = true; // accepted when read, opted out before the post: proposal.not-accepted
        await Cycles(fake, agent, 6);

        Assert.Single(fake.Posts);
        Assert.Contains(fake.Log, l => l.StartsWith($"proposal {fake.ProposalId}: refused 409", StringComparison.Ordinal));
        Assert.Equal(0, fake.Outcomes);
    }

    [Fact]
    public async Task A_read_that_serves_a_private_group_is_reported_as_a_lie_and_suggests_nothing()
    {
        var (fake, agent) = Make();
        fake.Groups.Add(Group("g-secret", "members"));
        await Cycles(fake, agent, 2);

        var lines = fake.PostedLines().ToList();
        Assert.Contains(lines, l => (string?)l["kind"] == "tool.lied");
        Assert.DoesNotContain(lines, l => (string?)l["kind"] == "tool.completed");
        var outcome = Assert.Single(lines, l => (string?)l["type"] == "axi.outcome");
        Assert.Equal("failed", (string?)outcome["status"]);
        Assert.True((int)outcome["reported"]!["lies"]! >= 1);
        Assert.Null(outcome["reported"]!["suggested"]);
    }

    [Fact]
    public async Task A_failed_read_is_reported_as_tool_failed_never_as_success()
    {
        var (fake, agent) = Make();
        fake.GroupsStatus = HttpStatusCode.InternalServerError;
        await Cycles(fake, agent, 2);

        var lines = fake.PostedLines().ToList();
        Assert.Contains(lines, l => (string?)l["kind"] == "tool.failed");
        Assert.DoesNotContain(lines, l => (string?)l["kind"] is "tool.completed" or "tool.lied");
        Assert.Equal("failed", (string?)Assert.Single(lines, l => (string?)l["type"] == "axi.outcome")["status"]);
    }

    [Fact]
    public async Task No_subject_is_bound_and_no_free_text_goes_on_the_wire()
    {
        // What the replies are read into: no field for a subject, an owner, a session or text. (The bytes Singular sends
        // still reach the response buffer; they are skipped there, never bound.)
        var bound = typeof(SingularAgent).GetNestedTypes(BindingFlags.NonPublic).SelectMany(t => t.GetProperties()).Select(p => p.Name).ToList();
        Assert.NotEmpty(bound);
        Assert.DoesNotContain(bound, n => n is "Subject" or "Owner" or "Session" or "SessionId" or "Name" or "Description" or "Detail");

        var (fake, agent) = Make();
        await Cycles(fake, agent, 3);

        Assert.NotEmpty(fake.Posts);
        Assert.All(fake.Posts, p => Assert.DoesNotContain(Sentinel, p));
        var uuid = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        var at = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
        string[] words = ["axi/v1", "axi.event", "axi.outcome", "tool.completed", "tool.failed", "tool.lied", "succeeded", "failed", "groups", SingularAgent.Capability, fake.GoalId];
        var keys = fake.Groups.Select(g => (string)g!["key"]!).ToHashSet();
        foreach (var line in fake.PostedLines())
        {
            Assert.False(line.ContainsKey("detail") || line.ContainsKey("description"), $"free text: {line}");
            foreach (var s in Strings(line))
                Assert.True(words.Contains(s) || keys.Contains(s) || uuid.IsMatch(s) || at.IsMatch(s), $"not an id, code or time: '{s}'");
        }
    }

    [Fact]
    public async Task A_404_ends_a_proposal_only_when_the_list_has_dropped_it_too()
    {
        var (fake, agent) = Make();
        fake.ReadFails = 2; // Singular's own read fails: 404 proposal.unknown while the list still serves it
        fake.ReadError = "agent.only";
        await Cycles(fake, agent, 2);
        Assert.Empty(fake.Posts);
        Assert.DoesNotContain(fake.Log, l => l.Contains("gone", StringComparison.Ordinal));

        fake.ReadError = "proposal.unknown";
        fake.ReadFails = 1;
        await Cycles(fake, agent, 3); // the outage ends: run, post, recorded
        Assert.Contains($"proposal {fake.ProposalId}: recorded", fake.Log);

        var (gone, goneAgent) = Make();
        gone.Gone = true; // expired or opted out: off the list, and 404 proposal.unknown by id
        await Cycles(gone, goneAgent, 1); // first sight: listed, so 404 alone does not end it
        gone.ServeGone = false;
        await Cycles(gone, goneAgent, 1);
        Assert.Contains($"proposal {gone.ProposalId}: proposal.gone", gone.Log);
        Assert.Empty(gone.Posts);
    }

    [Fact]
    public async Task A_proposal_that_throws_does_not_hold_up_the_others()
    {
        var (fake, agent) = Make();
        fake.Broken.Add("00000000-0000-4000-8000-000000000001"); // listed first; its read answers 200 with no JSON
        await Cycles(fake, agent, 3);

        Assert.Contains($"proposal {fake.ProposalId}: recorded", fake.Log);
        Assert.Contains("proposal 00000000-0000-4000-8000-000000000001: JsonException", fake.Log);
    }

    [Fact]
    public void Each_tenant_has_its_own_key_variable_and_headers()
    {
        Assert.Equal("AXI_KEY_ACME_CO", SingularAgent.KeyVariable("acme-co"));
        using var http = SingularAgent.Client(new Uri("https://singular.test/"), "app-test", "tenant-test", "test-only-key");
        Assert.Equal("Service test-only-key", http.DefaultRequestHeaders.Authorization?.ToString());
        Assert.Equal("app-test", Assert.Single(http.DefaultRequestHeaders.GetValues("X-App-Id")));
        Assert.Equal("tenant-test", Assert.Single(http.DefaultRequestHeaders.GetValues("X-Tenant-Id")));
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };

    private static async Task Cycles(FakeSingular fake, SingularAgent agent, int n)
    {
        for (var i = 0; i < n; i++)
        {
            fake.Clock.Advance(await agent.StepAsync());
        }
    }

    private static (FakeSingular, SingularAgent) Make()
    {
        var fake = new FakeSingular();
        var http = SingularAgent.Client(new Uri("https://singular.test/"), "app-test", "tenant-test", "test-only-key", fake);
        return (fake, new SingularAgent(http, fake.Clock, fake.Log.Add));
    }

    private static JsonObject Group(string key, string visibility) => new()
    {
        ["key"] = key, ["collection"] = "groups", ["owner"] = Sentinel, ["revision"] = 1,
        ["payload"] = new JsonObject { ["name"] = "A group anyone can join", ["visibility"] = visibility },
    };

    public sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Singular's agent routes as the vectors pin them, in memory.</summary>
    private sealed class FakeSingular : HttpMessageHandler
    {
        // Singular's route literals (AxiEndpoints, the data API), not the client's constants.
        private const string ListRoute = "/api/app/experience/agent/proposals?state=accepted";
        private const string ByIdRoute = "/api/app/experience/agent/proposals/";
        private const string IntakeRoute = "/api/app/experience/agent";
        private const string GroupsRoute = "/api/app/data/groups?visibility=eq.public&limit=5";
        private static readonly HashSet<string> SignalKinds = ["tool.failed", "tool.lied", "approval.rejected", "task.cancelled"];
        private readonly HashSet<string> _eventIds = [];
        private readonly HashSet<string> _capabilities;
        private DateTimeOffset? _lastPoll;
        private bool _recorded;

        public FakeSingular()
        {
            // The vector of an accepted proposal's outcome: its proposal, the app's goals and its agent capabilities.
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "axi-contract", "axi-vectors.json")));
            var vector = doc.RootElement.GetProperty("codec").EnumerateArray()
                .First(c => c.GetProperty("name").GetString()!.StartsWith("the same outcome with a mapped goal id", StringComparison.Ordinal));
            var context = vector.GetProperty("context");
            ProposalId = context.GetProperty("proposals").EnumerateObject().Single(p => p.Value.GetProperty("state").GetString() == "accepted").Name;
            GoalId = context.GetProperty("goals")[0].GetString()!;
            _capabilities = context.GetProperty("agentCapabilities").EnumerateArray().Select(c => c.GetString()!).ToHashSet();
            Assert.Contains(SingularAgent.Capability, _capabilities);
            for (var i = 1; i <= 7; i++) Groups.Add(Group($"g-{i}", "public"));
        }

        public FakeClock Clock { get; } = new();

        public string ProposalId { get; }

        public string GoalId { get; }

        public JsonArray Groups { get; } = [];

        public HttpStatusCode GroupsStatus { get; set; } = HttpStatusCode.OK;

        public Queue<(int Seconds, bool AsDate)> Throttles { get; } = new();

        public List<string> Log { get; } = [];

        public int StaleReads { get; set; }

        public int ReadFails { get; set; }

        public string ReadError { get; set; } = "proposal.unknown";

        public bool Gone { get; set; }

        public bool ServeGone { get; set; } = true;

        public List<string> Broken { get; } = [];

        public int Signals { get; private set; }

        public bool RefuseOutcome { get; set; }

        public Queue<HttpStatusCode> PostAnswers { get; } = new();

        public List<DateTimeOffset> Polls { get; } = [];

        public List<DateTimeOffset> TooSoon { get; } = [];

        public List<string> Posts { get; } = [];

        public List<string> Refused { get; } = [];

        public int Outcomes { get; private set; }

        public int Duplicates { get; private set; }

        public int GroupReads { get; private set; }

        public IEnumerable<JsonObject> PostedLines() => Posts.SelectMany(p => JsonNode.Parse(p)!.AsArray()).Cast<JsonObject>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Service test-only-key", request.Headers.Authorization?.ToString());
            Assert.Equal("app-test", Assert.Single(request.Headers.GetValues("X-App-Id")));
            Assert.Equal("tenant-test", Assert.Single(request.Headers.GetValues("X-Tenant-Id")));
            var path = request.RequestUri!.PathAndQuery;
            var now = Clock.GetUtcNow();
            if (request.Method == HttpMethod.Get && path == ListRoute)
            {
                Polls.Add(now);
                var early = _lastPoll is { } last && now - last < SingularAgent.PollInterval;
                if (early) TooSoon.Add(now);
                else _lastPoll = now;
                var throttle = (Seconds: 10, AsDate: false);
                if (early || Throttles.TryDequeue(out throttle))
                {
                    var seconds = throttle.Seconds;
                    var tooSoon = Json(HttpStatusCode.TooManyRequests, new JsonObject { ["error"] = "agent.poll.too-soon", ["retryAfter"] = 10 });
                    tooSoon.Headers.RetryAfter = throttle.AsDate ? new(now.AddSeconds(seconds)) : new(TimeSpan.FromSeconds(seconds));
                    return tooSoon;
                }
                var listed = new JsonArray(Broken.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["capabilityId"] = SingularAgent.Capability }).ToArray());
                if (!_recorded && (!Gone || ServeGone)) listed.Add(View());
                return Json(HttpStatusCode.OK, new JsonObject { ["proposals"] = listed });
            }
            if (request.Method == HttpMethod.Get && Broken.Any(id => path == ByIdRoute + id))
                return new(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Get && path == ByIdRoute + ProposalId)
            {
                if (Gone) return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "proposal.unknown" });
                if (ReadFails > 0 && ReadFails-- > 0) return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = ReadError });
                var view = View();
                if (_recorded && StaleReads > 0 && StaleReads-- > 0) view["outcome"] = null;
                return Json(HttpStatusCode.OK, view);
            }
            if (request.Method == HttpMethod.Get && path == GroupsRoute && ++GroupReads > 0)
                return Json(GroupsStatus, new JsonObject { ["items"] = Groups.DeepClone(), ["total"] = Groups.Count, ["limit"] = 5, ["offset"] = 0 });
            if (request.Method == HttpMethod.Post && path == IntakeRoute)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Posts.Add(body);
                // Before intake: unavailable, the caller check failed (agent.only), or fail closed (202 dropped, no results).
                if (PostAnswers.TryPeek(out var early) && early != HttpStatusCode.InternalServerError)
                    return early == HttpStatusCode.Accepted
                        ? Json(PostAnswers.Dequeue(), new JsonObject { ["kept"] = 0, ["dropped"] = true, ["reason"] = "error" })
                        : Json(PostAnswers.Dequeue(), new JsonObject { ["error"] = "agent.only" });
                var results = new JsonArray(JsonNode.Parse(body)!.AsArray().Select(l => (JsonNode)Intake((JsonObject)l!)).ToArray());
                if (PostAnswers.TryDequeue(out var late)) return new(late);
                return Json(HttpStatusCode.Accepted, new JsonObject { ["kept"] = 0, ["results"] = results });
            }
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "proposal.unknown" });
        }

        private JsonObject Intake(JsonObject line)
        {
            if (!AxiEnvelopeTests.Schema.Evaluate(JsonDocument.Parse(line.ToJsonString()).RootElement).IsValid) return Refuse(400, "schema", line);
            if ((string?)line["goalId"] != GoalId) return Refuse(400, "goal.unknown", line);
            if (line["capabilityId"] is { } cap && !_capabilities.Contains((string?)cap ?? "")) return Refuse(400, "capability.unknown", line);
            if ((string?)line["type"] == "axi.outcome")
            {
                if ((string?)line["proposalId"] != ProposalId) return Refuse(409, "proposal.unknown", line);
                if (RefuseOutcome) return Refuse(409, "proposal.not-accepted", line);
                if (_recorded) { Duplicates++; return new JsonObject { ["status"] = 202, ["duplicate"] = true }; }
                _recorded = true;
                Outcomes++;
                return new JsonObject { ["status"] = 202 };
            }
            if ((string?)line["correlationId"] != ProposalId) return Refuse(409, "proposal.unknown", line);
            if (RefuseOutcome) return Refuse(409, "proposal.not-accepted", line);
            // Only failures are kept, at most 2 per proposal; anything else, or past the cap, is accepted and dropped.
            if (!SignalKinds.Contains((string?)line["kind"] ?? "") || Signals >= 2) return new JsonObject { ["status"] = 202 };
            if (!_eventIds.Add((string)line["eventId"]!)) { Duplicates++; return new JsonObject { ["status"] = 202, ["duplicate"] = true }; }
            Signals++;
            return new JsonObject { ["status"] = 202 };
        }

        private JsonObject Refuse(int status, string reason, JsonObject line)
        {
            if (status != 409) Refused.Add($"{reason}: {line.ToJsonString()}");
            return new JsonObject { ["status"] = status, ["reason"] = reason };
        }

        // The agent view (§7.3), plus a stray subject: a client that bound it would carry it somewhere.
        private JsonObject View() => new()
        {
            ["id"] = ProposalId, ["capabilityId"] = SingularAgent.Capability, ["goalId"] = GoalId,
            ["reason"] = new JsonObject { ["code"] = "friction.high" }, ["parameters"] = new JsonObject(),
            ["state"] = "accepted", ["outcome"] = _recorded ? "recorded" : null, ["subject"] = Sentinel,
        };

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
