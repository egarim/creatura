using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Creatura.Samples.AxiAgent;

namespace Creatura.Tests;

/// <summary>
/// AXI (#91, CRE-02): samples/AxiAgent against a fake Singular built from the shared vectors (axi-contract/, see
/// <see cref="AxiEnvelopeTests"/>): the proposal, goals and agent capabilities are a vector's context, every posted line
/// is checked against the copied axi/v1 schema, and intake answers as Singular's does (202 per item, duplicate on a
/// retry, 409 for a proposal that is not accepted, 429 with Retry-After for a poll under 10 s).
/// </summary>
public sealed class AxiAgentTests
{
    private const string Sentinel = "kc-sub-0f3a-SENTINEL"; // a person's subject; it must never reach the wire

    [Fact]
    public async Task Polls_at_most_once_every_10_s_and_honours_Retry_After()
    {
        var (fake, agent) = Make();
        fake.RetryAfterOnce = 30; // the first poll is answered 429, Retry-After: 30
        for (var i = 0; i < 120; i++)
        {
            await agent.StepAsync();
            fake.Clock.Advance(TimeSpan.FromSeconds(1)); // called every second; the agent must hold back itself
        }

        Assert.Empty(fake.TooSoon);
        Assert.True(fake.Polls[1] - fake.Polls[0] >= TimeSpan.FromSeconds(30), "Retry-After was not honoured");
        Assert.All(fake.Polls.Zip(fake.Polls.Skip(1)), p => Assert.True(p.Second - p.First >= TimeSpan.FromSeconds(10)));
        Assert.InRange(fake.Polls.Count, 8, 10); // 0, 30, 40, ... 110
    }

    [Fact]
    public async Task Runs_suggest_a_group_and_posts_events_and_one_outcome_until_recorded()
    {
        var (fake, agent) = Make();
        await Cycles(fake, agent, 3);

        Assert.Equal("recorded", agent.Done[fake.ProposalId]);
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
        fake.PostAnswers.Enqueue(HttpStatusCode.ServiceUnavailable); // refused before intake
        fake.PostAnswers.Enqueue(HttpStatusCode.InternalServerError); // stored, then the answer is lost
        await Cycles(fake, agent, 6);

        Assert.Equal("recorded", agent.Done[fake.ProposalId]); // the next GET shows outcome: recorded, so no third post
        Assert.Equal(2, fake.Posts.Count);
        Assert.Equal(fake.Posts[0], fake.Posts[1]); // same eventIds, same outcome: what Singular dedupes on
        Assert.Equal(1, fake.Outcomes);
        Assert.Equal(1, fake.GroupReads); // ran once: a retry re-sends, it does not re-run the capability
    }

    [Fact]
    public async Task A_409_stops_the_retries()
    {
        var (fake, agent) = Make();
        fake.RefuseOutcome = true; // accepted when read, opted out before the post: proposal.not-accepted
        await Cycles(fake, agent, 6);

        Assert.Single(fake.Posts);
        Assert.StartsWith("refused 409", agent.Done[fake.ProposalId]);
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
    public async Task No_subject_is_read_and_no_free_text_goes_on_the_wire()
    {
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
        var http = new HttpClient(fake) { BaseAddress = new Uri("https://singular.test/") };
        http.DefaultRequestHeaders.Add("Authorization", "Service test-only-key");
        return (fake, new SingularAgent(http, fake.Clock));
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

        public int RetryAfterOnce { get; set; }

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
            var path = request.RequestUri!.PathAndQuery;
            var now = Clock.GetUtcNow();
            if (request.Method == HttpMethod.Get && path == "/" + SingularAgent.Proposals + "?state=accepted")
            {
                Polls.Add(now);
                if (_lastPoll is { } last && now - last < SingularAgent.PollInterval) TooSoon.Add(now);
                _lastPoll = now;
                if (RetryAfterOnce > 0)
                {
                    var tooSoon = Json(HttpStatusCode.TooManyRequests, new JsonObject { ["error"] = "agent.poll.too-soon", ["retryAfter"] = RetryAfterOnce });
                    tooSoon.Headers.RetryAfter = new(TimeSpan.FromSeconds(RetryAfterOnce));
                    RetryAfterOnce = 0;
                    return tooSoon;
                }
                return Json(HttpStatusCode.OK, new JsonObject { ["proposals"] = _recorded ? new JsonArray() : new JsonArray(View()) });
            }
            if (request.Method == HttpMethod.Get && path == $"/{SingularAgent.Proposals}/{ProposalId}") return Json(HttpStatusCode.OK, View());
            if (request.Method == HttpMethod.Get && path == "/" + SingularAgent.PublicGroups && ++GroupReads > 0)
                return Json(GroupsStatus, new JsonObject { ["items"] = Groups.DeepClone(), ["total"] = Groups.Count, ["limit"] = 5, ["offset"] = 0 });
            if (request.Method == HttpMethod.Post && path == "/" + SingularAgent.Intake)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Posts.Add(body);
                if (PostAnswers.TryPeek(out var early) && early == HttpStatusCode.ServiceUnavailable) return new(PostAnswers.Dequeue());
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
            if (!_capabilities.Contains((string?)line["capabilityId"] ?? "")) return Refuse(409, "capability.unknown", line);
            return _eventIds.Add((string)line["eventId"]!) ? new JsonObject { ["status"] = 202 } : new JsonObject { ["status"] = 202, ["duplicate"] = true };
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
