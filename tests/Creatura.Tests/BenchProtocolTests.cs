using Creatura.Bench;
using Creatura.Orienting;

namespace Creatura.Tests;

/// <summary>
/// The protocol (migration step 14, second commit): the legacy CSV lines round-trip byte for byte in both shapes, a file
/// is only appended to and a changed header gets a new name, a report never touches a README, the closed research
/// hypotheses carry their recorded verdicts with no scorer, and the Sokolov battery scores what its rows can show.
/// </summary>
public class BenchProtocolTests
{
    // Fixture lines read from the research archive (csharp/runs/batteries/{bach,hybrid}/results.csv, batteries-models/bach/scoreboard.csv,
    // models/cloud/scoreboard.csv) on 2026-10-27: the header and one data row of each shape, with and without a quoted free-text field.
    private const string ResultsHeader = "timestamp,battery,test,seat,world,agent,extensions,arm,case,repeat,outcome,ticks,calls,invalid,hand_off,deploy_blocked,lies_caught,freedom_before,freedom_after,releases,note";
    private const string ResultsPlain = "2026-10-26T11:11:13Z,bach,learning-across-cases,bfs,licence,levin,none,no record,1,1,real,4,0,0,no,0,0,0.00,0.00,0,\"\"";
    private const string ResultsNoted = "2026-10-26T11:11:32Z,hybrid,incoherence-alone,bfs,flood,levin,none,lie met,1,1,real,6,0,0,no,0,1,0.73,0.73,0,\"freedom before lie 0.73, after 0.73; replanned to clear-road\"";
    private const string ScoreboardHeader = "timestamp,model,provider,protocol,battery,test,world,agent,repeat,outcome,ticks,calls,failed,invalid,lies_caught,shortcut,prompt_tokens,completion_tokens,reasoning_tokens,cached_tokens,tokens_known,seconds,median_call_seconds,tokens_per_second,cost_usd,notes";
    private const string ScoreboardPlain = "2026-10-26T11:11:37Z,gpt-4.1-mini,openai,tools,bach,learning,learning/no record/levin/c1/r1,levin,1,real,4,4,0,0,0,no,1936,105,0,0,yes,3.6,0.81,29.1,0.0009,no extensions";
    private const string ScoreboardQuoted = "2026-10-26T08:38:01Z,deepseek-chat,openai,tools,seat,demo-worlds,permit,bach,1,real,7,7,0,0,0,no,9931,386,0,8064,yes,8.7,1.22,44.2,0.0029,\"barriers=none; consolidated: check-documents 100%, zoning-quick 100%, pay-fee 100%, schedule-inspection 100%, inspect 100%, issue-permit 100%, notify-citizen 100% | coherence 100% (0 contradictions)\"";

    private static string Temp() => Path.Combine(Path.GetTempPath(), "creatura-bench-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ResultsPlain)]
    [InlineData(ResultsNoted)]
    public void ABatteryResultsLineRoundTripsByteForByte(string line)
    {
        Assert.Equal(ResultsHeader, LegacyCsv.Header(LegacyShape.BatteryResults));
        var row = Row.ParseLegacyCsv(line, LegacyShape.BatteryResults);
        Assert.Equal(line, row.ToLegacyCsv(LegacyShape.BatteryResults));
        Assert.Equal(OutcomeClass.Real, row.Outcome);
        Assert.Equal("levin", row.Agent);
    }

    [Theory]
    [InlineData(ScoreboardPlain)]
    [InlineData(ScoreboardQuoted)]
    public void AScoreboardLineRoundTripsByteForByte(string line)
    {
        Assert.Equal(ScoreboardHeader, LegacyCsv.Header(LegacyShape.Scoreboard));
        var row = Row.ParseLegacyCsv(line, LegacyShape.Scoreboard);
        Assert.Equal(line, row.ToLegacyCsv(LegacyShape.Scoreboard));
        Assert.True(row.TokensKnown);
        Assert.NotNull(row.CostUsd);
    }

    [Fact]
    public void TheTwoShapesWriteTheSameRowInTheirOwnQuoting()
    {
        var row = new Row
        {
            At = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), Battery = "sokolov", Test = "panic", Seat = "bfs", World = "permit", Agent = "orienting",
            Arm = "panic", Outcome = OutcomeClass.HandedOff, Ticks = 9, LiesCaught = 2, Note = "said \"no\", twice",
        };
        var results = row.ToLegacyCsv(LegacyShape.BatteryResults);
        var scoreboard = row.ToLegacyCsv(LegacyShape.Scoreboard);
        Assert.EndsWith(",yes,0,2,0.00,0.00,0,\"said 'no', twice\"", results);
        Assert.EndsWith(",\"said \"\"no\"\", twice\"", scoreboard);
        Assert.Equal(21, LegacyCsv.Split(results).Count);
        Assert.Equal(26, LegacyCsv.Split(scoreboard).Count);
        Assert.Equal(row with { Note = "said 'no', twice" }, Row.ParseLegacyCsv(results, LegacyShape.BatteryResults));
    }

    [Fact]
    public void AResultsFileIsOnlyAppendedToAndAChangedHeaderGetsANewName()
    {
        var dir = Temp();
        var path = Path.Combine(dir, "results.csv");
        var row = Row.ParseLegacyCsv(ResultsPlain, LegacyShape.BatteryResults);
        try
        {
            Assert.Equal(path, LegacyCsv.Append(path, LegacyShape.BatteryResults, row));
            Assert.Equal(path, LegacyCsv.Append(path, LegacyShape.BatteryResults, row));
            Assert.Equal([ResultsHeader, ResultsPlain, ResultsPlain], File.ReadAllLines(path));
            Assert.Equal(ResultsHeader + "\n" + ResultsPlain + "\n" + ResultsPlain + "\n", File.ReadAllText(path));

            // The shape changed: the archive is not rewritten, the new line goes to the next free name.
            var moved = AppendOnlyCsv.Append(path, ResultsHeader + ",tick_unit", ResultsPlain + ",loop");
            Assert.Equal(Path.Combine(dir, "results-2.csv"), moved);
            Assert.Equal(3, File.ReadAllLines(path).Length);
            Assert.Equal(2, File.ReadAllLines(moved).Length);
            Assert.Equal(moved, AppendOnlyCsv.Resolve(path, ResultsHeader + ",tick_unit"));
            Assert.Equal(path, AppendOnlyCsv.Resolve(path, ResultsHeader));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AReportIsWrittenBesideAReadmeAndNeverOverIt()
    {
        var dir = Temp();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "README.md"), "# hand-written\n");
            File.WriteAllText(Path.Combine(dir, "report.md"), "# somebody's notes\n");
            var report = BatteryReport.Score(ClosedBatteries.Hybrid, []);
            Assert.Throws<ArgumentException>(() => report.Write(dir, "README.md"));
            var written = report.Write(dir);
            Assert.Equal(Path.Combine(dir, "report-2.md"), written);
            Assert.Equal("# hand-written\n", File.ReadAllText(Path.Combine(dir, "README.md")));
            Assert.Equal("# somebody's notes\n", File.ReadAllText(Path.Combine(dir, "report.md")));
            var text = File.ReadAllText(written);
            Assert.StartsWith(BatteryReport.Marker, text);
            Assert.Contains("| H10 |", text);
            Assert.Contains("**held**", text);
            Assert.Contains("**failed**", text);
            // A second write lands on the file this wrote.
            Assert.Equal(written, report.Write(dir, "report-2.md"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheClosedHypothesesCarryTheirRecordedVerdictsAndNoScorer()
    {
        var ids = ClosedBatteries.Hypotheses.Select(h => h.Id).ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(n => "H" + n).Concat(Enumerable.Range(1, 4).Select(n => "P" + n)), ids);
        Assert.All(ClosedBatteries.Hypotheses, h => Assert.Null(h.Score));
        Assert.Equal(HypothesisVerdict.Failed, ClosedBatteries.Levin.Find("H1").Judge([]));
        Assert.Equal(HypothesisVerdict.Held, ClosedBatteries.Bach.Find("H5").Judge([]));
        Assert.Equal(HypothesisVerdict.Mixed, ClosedBatteries.Bach.Find("H8").Judge([]));
        Assert.Equal(HypothesisVerdict.NotRun, ClosedBatteries.Biology.Find("H14").Judge([]));
        Assert.Equal(HypothesisVerdict.NotRun, ClosedBatteries.Dreams.Find("H19").Judge([]));
        Assert.Equal(HypothesisVerdict.Held, ClosedBatteries.Peterson.Find("P4").Judge([]));
        Assert.All(ClosedBatteries.All, b => Assert.NotEmpty(b.Arms));
        Assert.All(ClosedBatteries.All.SelectMany(b => b.Arms), a => Assert.NotNull(Creatura.Worlds.WorldCatalog.Find(a.World)));
    }

    [Fact]
    public void TheSokolovBatteryScoresWhatItsRowsCanShow()
    {
        var battery = SokolovBattery.Battery;
        Assert.Equal(SokolovHypotheses.All.Select(h => h.Id), battery.Hypotheses.Select(h => h.Id));
        Assert.All(battery.Hypotheses, h => Assert.Equal(HypothesisVerdict.NotRun, h.Judge([])));
        Assert.Null(battery.Find("S5").Score);

        Row Panic(string agent, OutcomeClass outcome, int releases) => new() { Test = "panic", Agent = agent, Arm = "panic", Outcome = outcome, Releases = releases };
        Assert.Equal(HypothesisVerdict.Held, battery.Find("S3").Judge([Panic("orienting", OutcomeClass.HandedOff, 0), Panic("levin", OutcomeClass.Real, 1)]));
        Assert.Equal(HypothesisVerdict.Failed, battery.Find("S3").Judge([Panic("orienting", OutcomeClass.Real, 1)]));
        Assert.Equal(HypothesisVerdict.Mixed, battery.Find("S3").Judge([Panic("orienting", OutcomeClass.HandedOff, 0), Panic("orienting", OutcomeClass.Real, 1)]));

        Row Series(OutcomeClass outcome, int tries) => new() { Test = "ingenuity", Agent = "orienting", Arm = "opaque", Outcome = outcome, ExploratoryTries = tries };
        Assert.Equal(HypothesisVerdict.Held, battery.Find("S1").Judge([Series(OutcomeClass.Real, 20), Series(OutcomeClass.Real, 30), Series(OutcomeClass.HandedOff, 10)]));
        Assert.Equal(HypothesisVerdict.Mixed, battery.Find("S1").Judge([Series(OutcomeClass.Real, 50), Series(OutcomeClass.Real, 40)]));
        Assert.Equal(HypothesisVerdict.Failed, battery.Find("S1").Judge([Series(OutcomeClass.HandedOff, 50)]));

        Row Drift(string agent, int @case, OutcomeClass outcome) => new() { Test = "drifting-tools", Agent = agent, Arm = "lying", Case = @case, Outcome = outcome };
        var rows = Enumerable.Range(1, 12).SelectMany(c => new[]
        {
            Drift("orienting", c, c is 5 or 6 ? OutcomeClass.HandedOff : OutcomeClass.Real),
            Drift("behaviourist", c, c is >= 5 and <= 10 ? OutcomeClass.HandedOff : OutcomeClass.Real),
        }).ToList();
        Assert.Equal(HypothesisVerdict.Held, battery.Find("S4").Judge(rows));

        var report = BatteryReport.Score(battery, rows);
        Assert.Equal(HypothesisVerdict.Held, report["S4"]);
        Assert.Equal(HypothesisVerdict.NotRun, report["S1"]);
        Assert.Contains("| S4 |", report.ToMarkdown());
    }
}
