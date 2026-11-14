using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Creatura.Tests;

/// <summary>
/// AXI (#90, CRE-01): samples/Axi emits axi/v1 lines exactly as Singular's contract defines them. The schema and the
/// golden vectors under axi-contract/ are copied as data, unchanged, from egarim/singular-framework at
/// c9c28e7ab76cbee2fd0ebf215d02217a89ad0380 (docs/axi/axi-v1.schema.json, sdk/singular-js/test/axi-vectors.json).
/// No Singular package is referenced; refresh both files together when the contract moves.
/// </summary>
public sealed class AxiEnvelopeTests
{
    // Loaded once per process: JsonSchema.Net registers the schema by its $id and refuses a second load (AxiAgentTests uses this one).
    internal static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Fixture("axi-v1.schema.json")));
    private static readonly Regex GoalId = new(@"^[a-z0-9-]+(\.[a-z0-9-]+)+(:[^\s]+)?$");
    private static readonly Regex Uuid = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");

    private static readonly HashSet<string> SchemaRefusals =
    [
        "type.unknown", "kind.unknown", "envelope.v", "envelope.eventId", "envelope.unknown", "envelope.tick", "envelope.detail",
        "envelope.requiresConfirmation", "envelope.status", "envelope.capabilityId", "envelope.goalId", "envelope.proposalId",
    ];

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "axi-contract", name);

    private static bool Valid(JsonElement line) => Schema.Evaluate(line).IsValid;

    [Fact]
    public void The_copied_schema_agrees_with_the_shared_vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixture("axi-vectors.json")));
        var checkedCases = 0;
        var wrong = new List<string>();
        foreach (var c in doc.RootElement.GetProperty("codec").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var input = c.GetProperty("input");
            var expect = c.GetProperty("expect");
            // A batch's items are cases of their own, each with its own expectation.
            var cases = input.ValueKind == JsonValueKind.Array
                ? input.EnumerateArray().Zip(expect.GetProperty("items").EnumerateArray(), (i, e) => (i, e)).ToList()
                : [(input, expect)];
            foreach (var (line, e) in cases)
            {
                var result = e.GetProperty("result").GetString();
                var reason = e.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                if (result == "accepted" && line.ValueKind == JsonValueKind.Object)
                {
                    if (!Valid(line)) wrong.Add($"accepted vector fails the schema: {name}");
                }
                // ponytail: only refusals the shape itself decides; envelope.at also covers calendar dates checked at intake.
                else if (result == "refused" && SchemaRefusals.Contains(reason))
                {
                    if (Valid(line)) wrong.Add($"refused vector passes the schema: {name} ({reason})");
                }
                else continue;
                checkedCases++;
            }
        }
        Assert.Empty(wrong);
        Assert.True(checkedCases >= 40, $"only {checkedCases} vectors checked");
    }

    [Fact]
    public void Every_line_the_sample_emits_is_axi_v1_with_stable_ids_and_no_free_text()
    {
        string[][] runs = [["--user", "learner"], ["--user", "muted-commuter"], ["--user", "muted-commuter", "--answer", "no"], ["--user", "noisy"]];
        var lines = runs.SelectMany(Run).ToList();
        var proposals = new List<string>();
        var eventIds = new List<string>();
        foreach (var line in lines)
        {
            var root = line.RootElement;
            Assert.True(Valid(root), $"not axi/v1: {root}");
            Assert.Equal("axi/v1", root.GetProperty("v").GetString());
            Assert.False(root.TryGetProperty("detail", out _), $"detail without --debug: {root}");
            Assert.False(root.TryGetProperty("description", out _), $"description without --debug: {root}");
            // The schema leaves the envelope optional; the sample promises all of it, and a goal on every line.
            foreach (var key in new[] { "sessionId", "correlationId", "at", "tick", "goalId" })
                Assert.True(root.TryGetProperty(key, out _), $"no {key}: {root}");
            Assert.Matches(GoalId, root.GetProperty("goalId").GetString()!);
            Assert.Matches(Uuid, root.GetProperty("correlationId").GetString()!);
            eventIds.Add(root.GetProperty("eventId").GetString()!);
            if (root.GetProperty("type").GetString() == "axi.proposal")
            {
                var id = root.GetProperty("id").GetString()!;
                Assert.Matches(Uuid, id);
                proposals.Add(id);
                // A code with numbers, never a sentence: no string anywhere in args, and no parameters.
                var reason = root.GetProperty("reason");
                Assert.Equal("friction.high", reason.GetProperty("code").GetString());
                Assert.All(reason.GetProperty("args").EnumerateObject(), a => Assert.Equal(JsonValueKind.Number, a.Value.ValueKind));
                Assert.Empty(root.GetProperty("parameters").EnumerateObject());
            }
        }
        Assert.True(proposals.Count >= 2, "no proposal emitted");
        Assert.Equal(proposals.Count, proposals.Distinct().Count());
        Assert.Equal(eventIds.Count, eventIds.Distinct().Count());
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public void A_proposal_is_asked_then_answered_and_only_an_accepted_one_runs_and_gets_an_outcome(string answer)
    {
        var lines = Run(["--user", "muted-commuter", "--answer", answer]).Select(l => l.RootElement).ToList();
        var p = lines.FindIndex(l => l.GetProperty("type").GetString() == "axi.proposal");
        Assert.True(p >= 0, "muted-commuter gets no proposal");
        var id = lines[p].GetProperty("id").GetString();
        var goal = lines[p].GetProperty("goalId").GetString();
        var about = lines.Skip(p + 1).Where(l => l.GetProperty("correlationId").GetString() == id).ToList();
        string? Kind(JsonElement l) => l.TryGetProperty("kind", out var k) ? k.GetString() : l.GetProperty("type").GetString();
        Assert.Equal("approval.requested", Kind(about[0]));
        Assert.Equal(answer == "yes" ? "approval.accepted" : "approval.rejected", Kind(about[1]));
        Assert.All(about, l => Assert.Equal(goal, l.GetProperty("goalId").GetString()));   // the proposal's goal, declared by the app
        var outcomes = lines.Where(l => l.GetProperty("type").GetString() == "axi.outcome").ToList();
        var withProposal = outcomes.Where(o => o.TryGetProperty("proposalId", out _)).ToList();
        if (answer == "yes")
        {
            Assert.True(about.Count > 3, "the accepted proposal did not run");
            Assert.Equal("axi.outcome", Kind(about[^1]));
            var o = Assert.Single(withProposal);
            Assert.Equal(id, o.GetProperty("proposalId").GetString());
            Assert.True(o.GetProperty("accepted").GetBoolean());
        }
        else
        {
            Assert.Equal(2, about.Count);   // rejected: nothing ran
            Assert.Empty(withProposal);     // Singular answers 409 proposal.not-accepted
        }
        Assert.Equal(withProposal.Count + 1, outcomes.Count);   // plus the main goal's own outcome
    }

    [Fact]
    public void Debug_brings_the_free_text_back_and_it_still_validates()
    {
        var lines = Run(["--user", "muted-commuter", "--debug", "true"]);
        Assert.Contains(lines, l => l.RootElement.TryGetProperty("detail", out _));
        Assert.All(lines, l => Assert.True(Valid(l.RootElement), l.RootElement.ToString()));
    }

    private static List<JsonDocument> Run(string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Axi.dll"));
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"samples/Axi {string.Join(' ', args)} did not exit in 2 minutes");
        }
        Assert.True(process.ExitCode == 0, $"samples/Axi {string.Join(' ', args)} exited {process.ExitCode}: {stderr.Result}");
        return [.. stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l))];
    }
}
