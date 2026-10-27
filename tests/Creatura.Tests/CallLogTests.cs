using System.Net;
using Creatura.Bench;
using Creatura.Seats;

namespace Creatura.Tests;

/// <summary>
/// The call log and the scoreboard (migration step 15, fourth commit): one JSON line per call with the prompt verbatim and
/// never a key, whichever field echoed it; a row keeps the log's path and never a prompt; the scoreboard writes the archive's
/// shape and reads it back.
/// </summary>
public class CallLogTests
{
    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), "creatura-log-" + Guid.NewGuid().ToString("N"), name);

    [Fact]
    public async Task EveryCallIsOneLineWithThePromptVerbatimAndTheKeyNeverInIt()
    {
        const string key = "sk" + "-0123456789abcdefghijklmnopqrstuv"; // two pieces: a grep for secrets over the repository stays clean
        var path = Temp("calls.jsonl");
        try
        {
            var handler = new SeatStubHandler(
                (HttpStatusCode.OK, SeatStubHandler.OpenAiReply("""{"tool":"inspect-logs","why":"a \"quoted\" reason — with unicode"}""")),
                (HttpStatusCode.BadRequest, $"{{\"error\":\"rejected Authorization: Bearer {key}\"}}"));
            using (var log = new CallLog(path))
            {
                var policy = new ModelPolicy(SeatFixtures.Json(), SeatFixtures.Client(handler, key), log, "test");
                await policy.NextAsync(SeatFixtures.Context(notes: ["a note the seat reads: ĉapelo"]));
                await policy.NextAsync(SeatFixtures.Context());
                Assert.Equal(2, log.Count);
                Assert.Equal(path, policy.Log.Path);
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.DoesNotContain(key, File.ReadAllText(path));
            Assert.Contains("Bearer ***", lines[1]);
            Assert.Contains("\"Purpose\":\"policy\"", lines[0]);
            Assert.Contains("\"Context\":\"test tick 1\"", lines[0]);
            Assert.Contains("a note the seat reads: \\u0109apelo", lines[0]);
            Assert.Contains("GOAL: ", lines[0]);
            Assert.Contains("\"Valid\":true", lines[0]);
            Assert.Contains("\"Valid\":false", lines[1]);
            Assert.Contains("\"Error\":", lines[1]);
            Assert.DoesNotContain("\"Error\":", lines[0]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ALogWithNoPathCountsAndWritesNothing()
    {
        var log = CallLog.None;
        log.Write(CallRecord.Of(SeatFixtures.Json(), "judge", "weights", "x", [], [], ModelReply.Failure(0, 0, "nothing"), false, null));
        Assert.Equal(1, log.Count);
        Assert.Null(log.Path);
    }

    [Fact]
    public async Task ARowKeepsTheLogsPathAndNeverAPrompt()
    {
        var path = Temp("seat.jsonl");
        try
        {
            using var log = new CallLog(path);
            var policy = new ModelPolicy(SeatFixtures.Json("stub-seat"), SeatFixtures.Client(SeatStubHandler.OpenAi("""{"tool":"inspect-logs","why":"the only sentence"}""")), log);
            await policy.NextAsync(SeatFixtures.Context());
            var at = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

            var row = Scoreboard.RowFor(at, policy, "seat", "demo-worlds", "permit", "bach", 2, OutcomeClass.Real, 7, 1, false, "barriers=all");
            Assert.Equal(path, row.CallLogPath);
            Assert.Equal(("stub-seat", "openai", "json", 1, 100L, 10L), (row.Seat, row.Provider, row.Protocol, row.Calls, row.PromptTokens, row.CompletionTokens));
            Assert.Equal(0.1m * 100 / 1_000_000 + 0.2m * 10 / 1_000_000, row.CostUsd);
            var line = row.ToLegacyCsv(LegacyShape.Scoreboard);
            Assert.StartsWith("2026-10-28T12:00:00Z,stub-seat,openai,json,seat,demo-worlds,permit,bach,2,real,7,1,0,0,1,no,100,10,0,0,yes,", line);
            Assert.DoesNotContain("the only sentence", line);
            Assert.DoesNotContain("GOAL", line);
            Assert.Equal(26, LegacyCsv.Split(line).Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheScoreboardAppendsTheArchivesShapeAndReadsItBack()
    {
        var path = Temp("scoreboard.csv");
        try
        {
            var board = new Scoreboard(path);
            var t = new SeatTelemetry();
            t.Add(new ModelReply("x", null, [], 120, 30, 5, 0, 2.5, null, null, 1.5, null, "stop", 200, null), true);
            t.Add(ModelReply.Failure(1.0, 503, "down"), false);
            var endpoint = ModelCatalog.Compile(_ => null).TryFind("deepseek-chat")!;
            board.Append(Scoreboard.RowFor(DateTimeOffset.UtcNow, endpoint, "tools", "seat", "demo-worlds", "permit", "levin", 1, OutcomeClass.Claimed, 9, t, 0, true, "note, with a comma"));
            board.Append(Scoreboard.RowFor(DateTimeOffset.UtcNow, endpoint, "tools", "seat", "demo-worlds", "permit", "bach", 1, OutcomeClass.Real, 6, t, 2, false, "plain"));

            var lines = File.ReadAllLines(path);
            Assert.Equal(Scoreboard.Header, lines[0]);
            Assert.Equal(3, lines.Length);
            var rows = board.ReadAll();
            Assert.Equal(2, rows.Count);
            Assert.Equal("note, with a comma", rows[0].Note);
            Assert.Equal((2, 1, 1, 120L, 30L), (rows[0].Calls, rows[0].Failed, rows[0].Invalid, rows[0].PromptTokens, rows[0].CompletionTokens));
            Assert.True(rows[0].ShortcutTaken);
            var summary = Scoreboard.Summary(rows);
            Assert.Contains("| deepseek-chat | tools | 2 | 50% | 50% | 0% | 0% |", summary);
            Assert.Equal("", Scoreboard.Summary(rows, "none").Split('\n')[2]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
