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
    private static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Fixture("axi-v1.schema.json")));
    private static readonly Regex GoalId = new(@"^[a-z0-9-]+(\.[a-z0-9-]+)+(:[^\s]+)?$");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "axi-contract", name);

    private static bool Valid(JsonElement line) => Schema.Evaluate(line).IsValid;

    [Fact]
    public void The_copied_schema_agrees_with_the_shared_vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixture("axi-vectors.json")));
        var checkedCases = 0;
        foreach (var c in doc.RootElement.GetProperty("codec").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var expect = c.GetProperty("expect");
            var result = expect.GetProperty("result").GetString();
            if (input.ValueKind == JsonValueKind.Array) continue;           // batches: their items are cases of their own
            var reason = expect.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var name = c.GetProperty("name").GetString();
            if (result == "accepted") Assert.True(Valid(input), $"accepted vector fails the schema: {name}");
            // ponytail: only refusals the shape itself decides; envelope.at also covers calendar dates checked at intake.
            else if (reason is "type.unknown" or "kind.unknown" or "envelope.v" or "envelope.eventId" or "envelope.unknown")
                Assert.False(Valid(input), $"refused vector passes the schema: {name}");
            else continue;
            checkedCases++;
        }
        Assert.True(checkedCases >= 20, $"only {checkedCases} vectors checked");
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
            if (root.TryGetProperty("goalId", out var goal)) Assert.Matches(GoalId, goal.GetString()!);
            eventIds.Add(root.GetProperty("eventId").GetString()!);
            if (root.GetProperty("type").GetString() == "axi.proposal")
            {
                proposals.Add(root.GetProperty("id").GetString()!);
                Assert.Equal(JsonValueKind.Object, root.GetProperty("reason").ValueKind);   // a code, never a sentence
            }
        }
        Assert.True(proposals.Count >= 2, "no proposal emitted");
        Assert.Equal(proposals.Count, proposals.Distinct().Count());
        Assert.Equal(eventIds.Count, eventIds.Distinct().Count());
        var kinds = lines.Select(l => l.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null).ToHashSet();
        Assert.Contains("approval.requested", kinds);
        Assert.Contains("approval.accepted", kinds);
        Assert.Contains("approval.rejected", kinds);
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
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"samples/Axi {string.Join(' ', args)} exited {process.ExitCode}: {stderr}");
        return [.. stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l))];
    }
}
