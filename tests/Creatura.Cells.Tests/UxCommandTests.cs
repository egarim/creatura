using Creatura.Cli;
using Creatura.Worlds.Web;

namespace Creatura.Cells.Tests;

/// <summary>
/// <c>creatura ux</c> (docs/design-web-worlds.md): the parts that need no browser. A full <c>ux run --demo</c> with a
/// browser is in Creatura.Worlds.Web.Tests (<c>BrowserTests</c>), through the same runner.
/// </summary>
public sealed class UxCommandTests
{
    private static async Task<(int Code, string Out, string Err)> Ux(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await UxCommand.Run(["ux", .. args], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task WithNoVerbItPrintsTheUsageAndFails()
    {
        var (code, _, err) = await Ux();
        Assert.Equal(2, code);
        Assert.Contains("creatura ux matrix (--demo | --adapter <file.json>)", err);
    }

    [Fact]
    public async Task TheDemoAdapterIsPrintedAndParses()
    {
        var (code, output, _) = await Ux("adapter");
        Assert.Equal(0, code);
        Assert.Equal("notes-demo", UxAdapter.Parse(output).App);
    }

    [Fact]
    public async Task ListShowsTheDemosCellsAndBarriers()
    {
        var (code, output, err) = await Ux("list", "--demo");
        Assert.True(code == 0, err);
        Assert.Contains("remember-milk", output);
        Assert.Contains("save-lies", output);
        Assert.Contains("390x844, mobile", output);
    }

    [Theory]
    [InlineData(new string[] { "run" }, "name one app")]
    [InlineData(new string[] { "run", "--demo", "--adapter", "x.json" }, "name one app")]
    [InlineData(new string[] { "run", "--demo", "--barriers", "nope" }, "no barrier 'nope'")]
    [InlineData(new string[] { "run", "--demo", "--persona", "nobody" }, "no persona 'nobody'")]
    [InlineData(new string[] { "run", "--demo", "--design", "nobody" }, "unknown design 'nobody'")]
    [InlineData(new string[] { "run", "--demo", "--ticks", "0" }, "--ticks")]
    [InlineData(new string[] { "run", "--demo", "--seat", "model:no-such-model" }, "no model 'no-such-model'")]
    [InlineData(new string[] { "run", "--adapter", "/no/such/adapter.json" }, "could not be read")]
    [InlineData(new string[] { "run", "--demo", "--wat", "1" }, "unknown option")]
    public async Task UsageErrorsSayWhatIsWrongBeforeAnyBrowserStarts(string[] args, string message)
    {
        var (code, _, err) = await Ux(args);
        Assert.Equal(2, code);
        Assert.Contains(message, err);
    }

    [Fact]
    public async Task AnAdapterRunNeedsItsSeatNamedBecauseAModelCostsMoney()
    {
        using var temp = new TempHome();
        var path = Path.Combine(temp.Path, "app.json");
        File.WriteAllText(path, DemoFiles.AdapterJson);
        var (code, _, err) = await Ux("run", "--adapter", path);
        Assert.Equal(2, code);
        Assert.Contains("--seat model:<name>", err);
    }

    private static string WriteReport(TempHome temp, string name, params bool[] done)
    {
        var cells = done.Select((d, i) => new UxCell
        {
            Task = "remember-milk", Persona = $"p{i}", Environment = "phone", Seat = "script:demo", Tester = "script:demo", Agent = "plain loop",
            DataDone = d, CheckDetail = "", PageSaidDone = true, Ticks = 2, LiesCaught = d ? 0 : 1, Escalations = 0, Cost = 0m,
        }).ToList();
        var report = new UxReport { App = "notes-demo", Cells = cells, Gate = new UxGate { MinDone = 1.0 } };
        return report.Write(Path.Combine(temp.Path, name));
    }

    [Fact]
    public async Task CompareReadsTwoReportsCellByCell()
    {
        using var temp = new TempHome();
        var before = WriteReport(temp, "before", true, true);
        var after = WriteReport(temp, "after", true, false);
        var (code, output, err) = await Ux("compare", before, Path.GetDirectoryName(after)!);
        Assert.True(code == 0, err);
        Assert.Contains("## Regressed (done before, not after): 1", output);
        Assert.Contains("remember-milk|p1|phone|script:demo|plain", output);
    }

    [Fact]
    public async Task TheGateExitsOneWhenAThresholdBreaksAndFlagsOverrideTheReportsGate()
    {
        using var temp = new TempHome();
        var held = WriteReport(temp, "held", true, true);
        var broken = WriteReport(temp, "broken", true, false);
        Assert.Equal(0, (await Ux("gate", held)).Code);

        var (code, output, _) = await Ux("gate", broken);
        Assert.Equal(1, code);
        Assert.Contains("gate: broken", output);
        Assert.Contains("done 50% < 100%", output);

        Assert.Equal(0, (await Ux("gate", broken, "--min-done", "0.5")).Code);
        var regressed = await Ux("gate", broken, "--min-done", "0", "--baseline", held);
        Assert.Equal(1, regressed.Code);
        Assert.Contains("regressions: 1 > 0", regressed.Out);
        Assert.Equal(0, (await Ux("gate", broken, "--min-done", "0", "--baseline", held, "--max-regressions", "1")).Code);
    }

    [Fact]
    public async Task JudgeWritesTheFindingsAndTheSummaryBesideTheReport()
    {
        using var temp = new TempHome();
        var report = WriteReport(temp, "r", true, false);
        var (code, output, err) = await Ux("judge", report);
        Assert.True(code == 0, err);
        Assert.Contains("judged by rules: ok 1, app-lied 1, not-done 1", output);
        var folder = Path.GetDirectoryName(report)!;
        Assert.Contains("- app-lied: 1 cell", File.ReadAllText(Path.Combine(folder, "summary.md")));
        Assert.True(File.Exists(Path.Combine(folder, "judgements.json")));
    }

    [Theory]
    [InlineData(new string[] { "judge", "x", "--judge", "model:nobody" }, "no model 'nobody'")]
    [InlineData(new string[] { "judge", "x", "--summary", "rules" }, "--summary takes model:<name>")]
    [InlineData(new string[] { "judge" }, "ux judge takes one report")]
    [InlineData(new string[] { "matrix", "--demo", "--judge", "model:nobody" }, "no model 'nobody'")]
    [InlineData(new string[] { "run", "--demo", "--seat", "model:deepseek-chat", "--vision", "nobody" }, "no vision model 'nobody'")]
    [InlineData(new string[] { "compare", "one" }, "two reports")]
    [InlineData(new string[] { "gate" }, "one report")]
    [InlineData(new string[] { "gate", "/no/such/report" }, "could not be read")]
    [InlineData(new string[] { "gate", "x", "--min-done", "2" }, "--min-done takes a number from 0 to 1")]
    [InlineData(new string[] { "matrix", "--demo", "--testers", "script:/no/such/script" }, "does not exist")]
    [InlineData(new string[] { "matrix", "--demo", "--envs", "watch" }, "no environment 'watch'")]
    [InlineData(new string[] { "matrix", "--demo", "--max-cost", "lots" }, "--max-cost takes a number")]
    [InlineData(new string[] { "matrix", "--demo", "--seat", "x" }, "unknown option for ux matrix")]
    [InlineData(new string[] { "matrix", "--demo", "--baseline", "/no/such/report" }, "could not be read")]
    [InlineData(new string[] { "matrix", "--demo", "--designs", "bach,nobody" }, "unknown design 'nobody'")]
    [InlineData(new string[] { "matrix", "--demo", "--design", "bach", "--designs", "levin" }, "name the designs once")]
    [InlineData(new string[] { "matrix", "--demo", "--memory", "forever", "--records", "x" }, "--memory takes carry or read")]
    [InlineData(new string[] { "matrix", "--demo", "--memory", "carry" }, "--memory needs --records")]
    [InlineData(new string[] { "matrix", "--demo", "--records", "x" }, "--records needs --memory")]
    [InlineData(new string[] { "run", "--demo", "--designs", "bach,levin" }, "unknown option for ux run: --designs")]
    public async Task MatrixCompareAndGateUsageErrors(string[] args, string message)
    {
        var (code, _, err) = await Ux(args);
        Assert.Equal(2, code);
        Assert.Contains(message, err);
    }
}
