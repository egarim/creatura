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
        Assert.Contains("creatura ux run --demo", err);
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
}
