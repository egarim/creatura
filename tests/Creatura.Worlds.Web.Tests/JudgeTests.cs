using System.Text.Json;
using Creatura.Seats;
using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>The judge and the summary writer (phase 2c): the rules, a model held to them, and the summary's facts. No browser.</summary>
public class JudgeTests
{
    private static UxCell Cell(bool done, bool pageSaid = false, int? believed = null, int lies = 0, int? told = null, string? error = null) =>
        ReportTests.Cell("hurried", done, pageSaid: pageSaid, believed: believed, lies: lies, told: told, error: error);

    private static readonly ModelEndpoint Judge = new("judge", Provider.OpenAiCompatible, "http://model.test/v1", "judge", null, false, false, 1m, 1m);

    private static UxJudge ModelJudge(SeatTests.ScriptedTransport transport) =>
        new(Judge, new ModelClient(transports: [transport]) { RetryDelay = TimeSpan.Zero });

    [Fact]
    public void TheRulesFindWhatTheVerdictsDecide()
    {
        Assert.Equal([UxFinding.Ok], UxJudge.Rules(Cell(done: true)).Findings);
        Assert.Equal([UxFinding.AppLied, UxFinding.NotDone], UxJudge.Rules(Cell(done: false, pageSaid: true, lies: 1)).Findings);
        Assert.Equal([UxFinding.AppLied, UxFinding.DesignMissedLie, UxFinding.NotDone], UxJudge.Rules(Cell(done: false, pageSaid: true, lies: 0)).Findings);
        Assert.Equal([UxFinding.PersonaOverclaimed, UxFinding.NotDone], UxJudge.Rules(Cell(done: false, believed: 3)).Findings);
        Assert.Equal([UxFinding.NotRun], UxJudge.Rules(Cell(done: false, error: "no app")).Findings);
        var lost = Cell(done: false, pageSaid: true, lies: 1) with { InputLost = 1 };
        Assert.Equal([UxFinding.NotDone, UxFinding.InputLost], UxJudge.Rules(lost).Findings);   // not the app's lie
        Assert.Equal([UxFinding.AppLied, UxFinding.NotDone, UxFinding.InputLost],
            UxJudge.Rules(Cell(done: false, pageSaid: true, lies: 2, told: 2) with { InputLost = 1 }).Findings);   // one of each
        Assert.Contains("1 of 1 lies caught", UxJudge.Rules(Cell(done: false, pageSaid: true, lies: 1)).Why);
    }

    [Fact]
    public async Task AModelJudgeExplainsButCannotChangeTheVerdict()
    {
        // The model calls a lie-ridden cell ok, with friction: ok is struck, the two verdict findings are put back, friction stays.
        var transport = new SeatTests.ScriptedTransport("""{"findings":["ok","friction","bad-ux"],"why":"Saved at tick 2, looked fine.","ticks":[2]}""");
        var judgement = await ModelJudge(transport).JudgeAsync(Cell(done: false, pageSaid: true, lies: 1), null);
        Assert.Equal("judge", judgement.Judge);
        Assert.Equal([UxFinding.AppLied, UxFinding.NotDone, UxFinding.Friction], judgement.Findings);
        Assert.Equal([2], judgement.Ticks);
        Assert.Equal("Saved at tick 2, looked fine.", judgement.Why);
        Assert.Contains("'bad-ux' is not a finding; dropped", judgement.Corrections);
        Assert.Contains("ok struck: the verdicts do not support it", judgement.Corrections);
        Assert.Contains("app-lied added: the verdicts require it", judgement.Corrections);
        Assert.Contains("not-done added: the verdicts require it", judgement.Corrections);
        Assert.Equal(0.00012m, judgement.Cost);   // 100 prompt and 20 completion tokens at 1 per million each
    }

    [Fact]
    public async Task AModelThatAgreesIsNotCorrectedAndFrictionReplacesOk()
    {
        var transport = new SeatTests.ScriptedTransport("""{"findings":["friction"],"why":"Done, after two wrong turns (ticks 2, 3).","ticks":[2,3]}""");
        var judgement = await ModelJudge(transport).JudgeAsync(Cell(done: true), null);
        Assert.Equal([UxFinding.Friction], judgement.Findings);
        Assert.Empty(judgement.Corrections);
    }

    [Fact]
    public async Task AFailedOrUnreadableJudgeFallsBackToTheRulesAndSaysSo()
    {
        var unreadable = await ModelJudge(new SeatTests.ScriptedTransport("no json here")).JudgeAsync(Cell(done: false, pageSaid: true, lies: 1), null);
        Assert.Equal([UxFinding.AppLied, UxFinding.NotDone], unreadable.Findings);
        Assert.StartsWith("rules (the judge judge answered nothing readable)", unreadable.Judge);

        var notRun = await ModelJudge(new SeatTests.ScriptedTransport("{}")).JudgeAsync(Cell(done: false, error: "no app"), null);
        Assert.Equal("rules", notRun.Judge);   // a cell that did not run is not sent to the model
    }

    [Fact]
    public void TheJudgeReadsTheVerdictsAndTheTraceNeverTheScreen()
    {
        var trace = Enumerable.Range(1, 80).Select(i => new TraceEvent(i, "cell", "effect", 0, 0.2, "button-save", TraceKind.Acted, "effect landed")).ToList();
        var run = new AgentRun("plain loop", false, 80, trace, []);
        var prompt = UxJudge.Prompt(Cell(done: false, pageSaid: true, lies: 1), run);
        Assert.Contains("THE DATA SAYS: NOT done", prompt);
        Assert.Contains("THE PAGE SAID A SAVE WENT THROUGH: yes", prompt);
        Assert.Contains("LIES TOLD: 1; CAUGHT BY THE CONTROL LAYER: 1", prompt);
        Assert.Contains("  60 cell button-save Acted effect landed", prompt);
        Assert.DoesNotContain("  61 cell", prompt);                       // the trace is capped
        Assert.DoesNotContain("SCREEN:", prompt);
        Assert.DoesNotContain("VISIBLE TEXT", prompt);
    }

    [Fact]
    public async Task TheSummaryIsTheFactsFirstAndAModelParagraphUnderThem()
    {
        var report = ReportTests.Report(Cell(done: true), ReportTests.Cell("careful", false, pageSaid: true, lies: 1));
        var judgements = report.Cells.Select(UxJudge.Rules).ToList();
        var facts = UxSummary.Facts(report, judgements);
        Assert.Contains("- app-lied: 1 cell", facts);
        Assert.Contains("- not-done: 1 cell", facts);
        Assert.Contains("**remember-milk|careful|phone|script:demo|plain** (app-lied, not-done)", facts);
        Assert.Contains("1 of 2 cells ok.", facts);
        Assert.Equal(1, facts.Split("remember-milk|careful").Length - 1);   // each cell once

        var writer = new UxSummary(Judge, new ModelClient(transports: [new SeatTests.ScriptedTransport("Saving looks fine on screen but does not keep notes. Look at the save first.")]) { RetryDelay = TimeSpan.Zero });
        var (text, cost) = await writer.WriteAsync(report, judgements);
        Assert.StartsWith(facts, text);
        Assert.Contains("## In a paragraph (written by judge; the facts above are the record)", text);
        Assert.Contains("Look at the save first.", text);
        Assert.NotNull(cost);

        var failing = new UxSummary(Judge, new ModelClient(transports: [new SeatTests.ScriptedTransport("")]) { RetryDelay = TimeSpan.Zero });
        var (empty, _) = await failing.WriteAsync(report, judgements);
        Assert.StartsWith(facts, empty);
        Assert.Contains("summary failed", empty);
    }

    [Fact]
    public async Task JudgingAReportReadsItsTracesAndWritesBesideIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creatura-judge-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var trace = new List<TraceEvent> { new(2, "cell", "effect", 1, 0.2, "button-save", TraceKind.Blocked, "reported 'Saved.' but effect missing") };
            var cell = ReportTests.Cell("hurried", false, pageSaid: true, lies: 1) with { Run = new AgentRun("plain loop", false, 3, trace, []) };
            ReportTests.Report(cell).Write(dir);
            var loaded = UxReport.Load(dir);
            Assert.Null(loaded.Cells[0].Run);
            Assert.Equal("button-save", UxJudging.Trace(dir, loaded.Cells[0])!.Trace[0].Action);

            var transport = new SeatTests.ScriptedTransport("""{"findings":["app-lied","not-done"],"why":"Tick 2: the save was blocked.","ticks":[2]}""");
            var judged = await UxJudging.RunAsync(loaded, ModelJudge(transport), new UxSummary(), dir);
            Assert.Contains("  2 cell button-save Blocked reported 'Saved.' but effect missing", transport.Requests[0].Messages[1].Content);
            Assert.Single(judged.Judgements);
            Assert.True(File.Exists(Path.Combine(dir, UxJudging.SummaryFile)));
            var back = JsonSerializer.Deserialize<List<UxJudgement>>(File.ReadAllText(Path.Combine(dir, UxJudging.JudgementsFile)), UxAdapter.Json)!;
            Assert.Equal([UxFinding.AppLied, UxFinding.NotDone], back[0].Findings);
            var reloaded = UxReport.Load(dir);
            Assert.Equal("judge", reloaded.Judgements[0].Judge);
            Assert.Contains("| app-lied, not-done |", File.ReadAllText(Path.Combine(dir, "report.md")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
