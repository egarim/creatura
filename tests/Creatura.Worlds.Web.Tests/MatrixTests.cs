using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>The report, compare and gate over cells made by hand: no browser.</summary>
public class ReportTests
{
    internal static UxCell Cell(string persona, bool done, string env = "phone", string tester = "script:demo", bool pageSaid = false, int? believed = null,
        int lies = 0, int ticks = 2, decimal? cost = 0m, string? error = null) =>
        error is not null ? UxCell.Failed("remember-milk", persona, env, tester, error) : new UxCell
        {
            Task = "remember-milk", Persona = persona, Environment = env, Seat = tester, Tester = tester, Agent = "plain loop",
            DataDone = done, CheckDetail = done ? "items holds it" : "items holds no it", PageSaidDone = pageSaid, BelievedDoneAt = believed,
            Ticks = ticks, LiesCaught = lies, Escalations = 0, Cost = cost,
        };

    internal static UxReport Report(params UxCell[] cells) => new() { App = "notes-demo", At = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero), Cells = cells };

    [Fact]
    public void TheTotalsCountTheThreeGapsApart()
    {
        var report = Report(
            Cell("a", done: true, pageSaid: true),
            Cell("b", done: false, pageSaid: true, lies: 1),          // the page lied and the run caught it
            Cell("c", done: false, pageSaid: true),                    // the page lied and nothing noticed
            Cell("d", done: false, believed: 3),                       // the model believed it was done
            Cell("e", done: false, error: "the app did not open"));
        Assert.Equal((4, 1, 1), (report.Ran, report.Done, report.Errors));
        Assert.Equal(0.25, report.DoneRate);
        Assert.Equal((1, 2, 1), (report.BelievedNotDone, report.PageSaidNotDone, report.UncaughtLies));
        Assert.Contains("1 of 4 cells done (25%)", report.Totals());
    }

    [Fact]
    public void AnUnpricedModelMakesTheCostUnknownNotFree()
    {
        Assert.Equal(0.3m, Report(Cell("a", true, cost: 0.1m), Cell("b", true, cost: 0.2m)).Cost);
        Assert.Null(Report(Cell("a", true, cost: 0.1m), Cell("b", true, tester: "model:x", cost: null)).Cost);
        Assert.Equal(0.1m, Report(Cell("a", true, cost: 0.1m), Cell("b", false, error: "not run")).Cost);
    }

    [Fact]
    public void TheReportWritesItsFolderAndReadsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creatura-ux-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var report = Report(Cell("hurried", true), Cell("careful", false, pageSaid: true, lies: 1)) with { Gate = new UxGate { MinDone = 1 }, Barriers = ["save-lies"] };
            var json = report.Write(dir);
            Assert.Equal(Path.Combine(dir, UxReport.File), json);
            var back = UxReport.Load(dir);
            Assert.Equal(report.Cells.Select(c => c.Key), back.Cells.Select(c => c.Key));
            Assert.Equal(report.Totals(), back.Totals());
            Assert.Equal(1, back.Gate!.MinDone);
            Assert.Equal(["save-lies"], back.Barriers);

            var csv = File.ReadAllLines(Path.Combine(dir, "cells.csv"));
            Assert.Equal(UxReport.CsvHeader, csv[0]);
            Assert.Equal(3, csv.Length);
            Assert.Contains("| careful | phone | script:demo | not done | yes |", File.ReadAllText(Path.Combine(dir, "report.md")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CompareJoinsCellsByTheirKey()
    {
        var before = Report(Cell("a", true), Cell("b", false), Cell("c", true, ticks: 2), Cell("d", true), Cell("old", true));
        var after = Report(Cell("a", false, pageSaid: true), Cell("b", true), Cell("c", true, ticks: 5), Cell("d", true), Cell("new", true));
        var diff = UxComparison.Of(before, after);
        Assert.Equal(["remember-milk|a|phone|script:demo"], diff.Regressed);
        Assert.Equal(["remember-milk|b|phone|script:demo"], diff.Fixed);
        Assert.Equal(["remember-milk|a|phone|script:demo (lie not caught, page said done)"], diff.NewGaps);
        Assert.Equal(["remember-milk|c|phone|script:demo (2 → 5 ticks)"], diff.Slower);
        Assert.Equal(["remember-milk|old|phone|script:demo"], diff.OnlyBefore);
        Assert.Equal(["remember-milk|new|phone|script:demo"], diff.OnlyAfter);
        Assert.Contains("## Regressed (done before, not after): 1", diff.ToMarkdown());
    }

    [Fact]
    public void TheSameCellOnAnotherTesterIsAnotherCell()
    {
        var diff = UxComparison.Of(Report(Cell("a", true, tester: "model:x")), Report(Cell("a", false, tester: "model:y")));
        Assert.Empty(diff.Regressed);
        Assert.Single(diff.OnlyBefore);
    }

    [Fact]
    public void TheGateNamesEveryThresholdBroken()
    {
        var report = Report(Cell("a", true), Cell("b", false, pageSaid: true), Cell("c", false, believed: 2), Cell("d", false, error: "no app"));
        var gate = new UxGate { MinDone = 0.5, MaxBelievedNotDone = 0, MaxPageSaidNotDone = 0, MaxUncaughtLies = 0, MaxCost = 1m };
        var breaches = gate.Breaches(report);
        Assert.Equal(5, breaches.Count);
        Assert.Contains("done 33% < 50%", breaches);
        Assert.Contains("believed done but not: 1 > 0", breaches);
        Assert.Contains("lies not caught: 1 > 0", breaches);
        Assert.Contains("cells not run: 1 > 0", breaches);   // left out of the gate, still checked: a cell that did not run is no pass
        Assert.Empty(new UxGate().Breaches(Report(Cell("a", true))));
    }

    [Fact]
    public void RegressionsAreGatedOnlyAgainstABaseline()
    {
        var before = Report(Cell("a", true), Cell("b", true));
        var after = Report(Cell("a", true), Cell("b", false));
        var gate = new UxGate();
        Assert.Empty(gate.Breaches(after));
        Assert.Contains(gate.Breaches(after, before), b => b.StartsWith("regressions: 1 > 0", StringComparison.Ordinal));
        Assert.Empty((gate with { MaxRegressions = 1 }).Breaches(after, before));
    }

    [Fact]
    public void AnUnknownCostBreaksAMaxCost()
    {
        var breaches = new UxGate { MaxCost = 5m }.Breaches(Report(Cell("a", true, tester: "model:x", cost: null)));
        Assert.Equal(["cost unknown, with a maxCost set"], breaches);
    }

    [Fact]
    public void NoCellRunIsNeverAPass()
    {
        Assert.Contains("no cell ran", new UxGate { MaxErrors = 9 }.Breaches(Report(Cell("a", false, error: "no app"))));
    }

    [Fact]
    public void TheMatrixIsEveryTaskPersonaEnvironmentAndTester()
    {
        var adapter = DemoFiles.Adapter();
        var all = UxMatrix.Cells(adapter, new UxMatrixSpec());
        Assert.Equal(4, all.Count);   // 1 task × 2 personas × 2 environments × the adapter's 1 tester
        Assert.Equal(["hurried/phone", "hurried/desktop", "careful/phone", "careful/desktop"], all.Select(c => $"{c.Persona.Id}/{c.Environment.Id}"));
        var some = UxMatrix.Cells(adapter, new UxMatrixSpec { Environments = ["phone"], Testers = ["script:demo", "model:x"] });
        Assert.Equal(4, some.Count);
        Assert.Throws<ArgumentException>(() => UxMatrix.Cells(adapter, new UxMatrixSpec { Personas = ["nobody"] }));
        Assert.Throws<ArgumentException>(() => UxMatrix.Cells(adapter with { Limits = adapter.Limits with { Testers = [] } }, new UxMatrixSpec()));
    }

    [Fact]
    public void TheDemoAdapterCarriesItsTestersAndGate()
    {
        var adapter = DemoFiles.Adapter();
        Assert.Equal([UxSeats.BundledScript], adapter.Limits.Testers);
        Assert.Equal(1.0, adapter.Gate!.MinDone);
        Assert.Equal(0, adapter.Gate.MaxUncaughtLies);
    }

    [Fact]
    public void ASeatIsCheckedBeforeAnyBrowserStarts()
    {
        var seats = new UxSeats(new Creatura.Seats.ModelCatalog([], [], "test"));
        var adapter = DemoFiles.Adapter();
        Assert.Null(seats.Check(UxSeats.BundledScript, adapter));
        Assert.Contains("does not exist", seats.Check("script:/no/such/file", adapter));
        Assert.Contains("no model 'nobody'", seats.Check("model:nobody", adapter));
    }
}

/// <summary>The demo's matrix in a real browser, clean and with the lie, and the gate on both.</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class MatrixBrowserTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    [BrowserFact]
    public async Task TheDemoMatrixRunsEveryCellAndHoldsItsGate()
    {
        var adapter = demo.App.Adapter();
        var seen = new List<UxCell>();
        var report = await UxMatrix.RunAsync(demo.Browser, adapter, new UxMatrixSpec(), new UxSeats(), progress: seen.Add);
        Assert.Equal(4, report.Cells.Count);
        Assert.Equal(report.Cells, seen);
        Assert.All(report.Cells, c => Assert.True(c.DataDone, c.Summary()));
        Assert.Equal(["phone", "desktop", "phone", "desktop"], report.Cells.Select(c => c.Environment));
        Assert.Empty(adapter.Gate!.Breaches(report));
    }

    [BrowserFact]
    public async Task UnderTheLieTheGateBreaksAndCompareNamesEveryRegression()
    {
        var adapter = demo.App.Adapter();
        var spec = new UxMatrixSpec { Environments = ["desktop"] };
        var clean = await UxMatrix.RunAsync(demo.Browser, adapter, spec, new UxSeats());
        var lie = await UxMatrix.RunAsync(demo.Browser, adapter, spec with { Barriers = ["save-lies"] }, new UxSeats(),
            (policy, task) => AgentCatalog.Make("bach", policy, task), "bach");
        Assert.Equal((0, 2, 0, 2), (lie.Done, lie.PageSaidNotDone, lie.UncaughtLies, lie.LiesCaught));
        Assert.Equal("bach", lie.Design);
        var diff = UxComparison.Of(clean, lie);
        Assert.Equal(2, diff.Regressed.Count);
        var breaches = adapter.Gate!.Breaches(lie, clean);
        Assert.Contains("done 0% < 100%", breaches);
        Assert.Contains(breaches, b => b.StartsWith("regressions: 2 > 0", StringComparison.Ordinal));
        Assert.DoesNotContain(breaches, b => b.StartsWith("lies not caught", StringComparison.Ordinal));   // the design caught each one
    }

    [BrowserFact]
    public async Task ACellThatCannotOpenIsRecordedAndTheMatrixGoesOn()
    {
        var adapter = demo.App.Adapter() with { BaseUrl = "http://127.0.0.1:1/" };   // nothing listens there
        var report = await UxMatrix.RunAsync(demo.Browser, adapter, new UxMatrixSpec { Personas = ["hurried"] }, new UxSeats());
        Assert.Equal(2, report.Errors);
        Assert.All(report.Cells, c => Assert.NotNull(c.Error));
        Assert.Contains("no cell ran", new UxGate().Breaches(report));
    }
}
