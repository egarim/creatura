using System.Text.Json.Nodes;
using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>The two app properties a world reads (docs/design-web-worlds.md, the judge as a web world): no browser.</summary>
public class LiveShapeTests
{
    [Fact]
    public void TheCheckKnowsTheTextsItAsserts()
    {
        Assert.Equal(["buy milk"], new DataCheck { Url = "/x", Path = "items", Contains = JsonNode.Parse("""{"text":"buy milk","bought":true}""") }.Texts());
        Assert.Equal(["Ana"], new DataCheck { Url = "/x", Path = "owner.name", Is = JsonValue.Create("Ana") }.Texts());
        Assert.Empty(new DataCheck { Url = "/x", Path = "items", Count = 2 }.Texts());
    }

    [Fact]
    public void TheRulesFindNotLiveAndLostOnRestartOnlyWhenAsked()
    {
        var done = ReportTests.Cell("a", done: true);
        Assert.Equal([UxFinding.Ok], UxJudge.Rules(done).Findings);
        Assert.Equal([UxFinding.NotLive], UxJudge.Rules(done with { Live = false, LiveDetail = "no" }).Findings);
        Assert.Equal([UxFinding.LostOnRestart], UxJudge.Rules(done with { Survived = false }).Findings);
        Assert.Equal([UxFinding.Ok], UxJudge.Rules(done with { Live = true, Survived = true }).Findings);
        Assert.Equal([UxFinding.NotLive, UxFinding.LostOnRestart], UxJudge.Rules(done with { Live = false, Survived = false }).Findings);
        Assert.Contains("Not live: no.", UxJudge.Rules(done with { Live = false, LiveDetail = "no" }).Why);
        Assert.Contains("NOT live", (done with { Live = false }).Summary());
        Assert.Contains("survived a restart", (done with { Survived = true }).Summary());
        var csv = new UxReport { App = "x", Cells = [done with { Live = true, Survived = false }] }.CsvLines().Single().Split(',');
        Assert.Equal(UxReport.CsvHeader.Split(',').Length, csv.Length);
        Assert.Equal(("1", "0"), (csv[^2], csv[^1]));
    }
}

/// <summary>W33 and W34 on the demo, in a real Chromium.</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class LiveTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    private Task<UxCell> Run(Func<UxTask, UxTask> change)
    {
        var adapter = demo.App.Adapter();
        var task = change(adapter.Task(null));
        return UxRunner.RunAsync(demo.Browser, adapter, task, adapter.Persona("hurried"), adapter.Environment("desktop"),
            w => new WebScriptPolicy(DemoFiles.Script, () => w.Current), null, new WebWorldOptions());
    }

    [BrowserFact]
    public async Task W33_APageThatDoesNotRefreshIsNotLiveAndOneThatPollsIs()
    {
        var still = await Run(t => t with { Live = new UxLive { WithinMs = 2500 } });
        Assert.True(still.DataDone);
        Assert.False(still.Live);
        Assert.Contains("did not show \"buy milk\" within 2500 ms", still.LiveDetail);
        Assert.Equal([UxFinding.NotLive], UxJudge.Rules(still).Findings);

        var polling = await Run(t => t with { Start = "/?live=1", Live = new UxLive() });
        Assert.True(polling.DataDone);
        Assert.True(polling.Live);
        Assert.Contains("with no reload", polling.LiveDetail);
        Assert.Equal([UxFinding.Ok], UxJudge.Rules(polling).Findings);
    }

    [BrowserFact]
    public async Task W34_TheDataSurvivesARestartThatKeepsItAndNotOneThatForgets()
    {
        var kept = await Run(t => t with { Survives = new UxSurvives { Restart = new HttpCall { Method = "POST", Url = "/api/restart" }, PatienceMs = 5000 } });
        Assert.True(kept.DataDone);
        Assert.True(kept.Survived);
        Assert.Contains("still shows it", kept.SurvivedDetail);
        Assert.Equal([UxFinding.Ok], UxJudge.Rules(kept).Findings);

        var lost = await Run(t => t with { Survives = new UxSurvives { Restart = new HttpCall { Method = "POST", Url = "/api/restart?forget=1" }, PatienceMs = 3000 } });
        Assert.True(lost.DataDone);
        Assert.False(lost.Survived);
        Assert.Contains("no longer shows it", lost.SurvivedDetail);
        Assert.Equal([UxFinding.LostOnRestart], UxJudge.Rules(lost).Findings);
        Assert.Contains("LOST on restart", lost.Summary());

        // Asked for, but the data never said done: nothing is restarted and nothing is judged about it.
        var adapter = demo.App.Adapter();
        var task = adapter.Task(null) with { Survives = new UxSurvives { Restart = new HttpCall { Method = "POST", Url = "/api/restart?forget=1" } } };
        var cell = await UxRunner.RunAsync(demo.Browser, adapter, task, adapter.Persona("hurried"), adapter.Environment("desktop"),
            w => new WebScriptPolicy(DemoFiles.Script, () => w.Current), null, new WebWorldOptions { Barriers = ["save-lies"] });
        Assert.False(cell.DataDone);
        Assert.Null(cell.Survived);
        Assert.DoesNotContain(UxFinding.LostOnRestart, UxJudge.Rules(cell).Findings);
    }
}
