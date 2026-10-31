using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>Page barriers (phase 3), without a browser: the adapter and the scripts.</summary>
public class PageBarrierAdapterTests
{
    [Fact]
    public void TheDemoCarriesThreePageBarriers()
    {
        var adapter = DemoFiles.Adapter();
        Assert.Equal((UxBarrierKind.Dodge, "Save", 1), (adapter.Barrier("save-dodges").Kind, adapter.Barrier("save-dodges").Target, adapter.Barrier("save-dodges").Times));
        Assert.True(adapter.Barrier("cookie-banner").OnPage);
        Assert.Equal(UxBarrierKind.DropLetter, adapter.Barrier("note-drops-letter").Kind);
        Assert.False(adapter.Barrier("save-lies").OnPage);
    }

    [Fact]
    public void APageBarrierNeedsATargetAndNoUrl()
    {
        var e = Assert.Throws<FormatException>(() => UxAdapter.Parse("""
            { "app": "x", "personas": [{"id":"p","description":"d"}], "environments": [{"id":"e","width":400,"height":800}],
              "tasks": [{"id":"t","prompt":"p","check":{"url":"/a","exists":true}}],
              "barriers": [{"id":"d","kind":"dodge"}, {"id":"c","kind":"cover"}, {"id":"l","kind":"lie"}] }
            """));
        Assert.Contains("barrier d: no target", e.Message);
        Assert.DoesNotContain("barrier c", e.Message);
        Assert.Contains("barrier l: no url pattern", e.Message);
    }

    [Fact]
    public void EachPageBarrierHasAScriptAndANetworkOneHasNone()
    {
        var adapter = DemoFiles.Adapter();
        Assert.Null(PageBarriers.Script(adapter.Barrier("save-lies")));
        var dodge = PageBarriers.Script(adapter.Barrier("save-dodges"))!;
        Assert.Contains("\"save-dodges\"", dodge);
        Assert.Contains("const target = \"save\"", dodge);
        Assert.Contains("const times = 1", dodge);
        Assert.Contains(PageBarriers.FiredFunction, PageBarriers.Script(adapter.Barrier("cookie-banner"))!);
    }

    [Fact]
    public void ACoveredControlSaysSo() =>
        Assert.EndsWith("(covered by something on top)", (ObservationTests.Screen().Controls[1] with { Covered = true }).Line());
}

/// <summary>Page barriers in a real browser: what the world reports, and what a run comes to.</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class PageBarrierTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    private Task<WebWorld> Open(params string[] barriers)
    {
        var adapter = demo.App.Adapter();
        return WebWorld.OpenAsync(demo.Browser, adapter, adapter.Task(null), adapter.Environment("desktop"), new WebWorldOptions { Barriers = barriers });
    }

    private Task<UxCell> Run(string[] barriers, params string[] script)
    {
        var adapter = demo.App.Adapter();
        return UxRunner.RunAsync(demo.Browser, adapter, adapter.Task(null), adapter.Persona("careful"), adapter.Environment("desktop"),
            w => new WebScriptPolicy(script, () => w.Current), (p, t, extra) => UxDesigns.Make("bach", p, t, extensions: extra),
            new WebWorldOptions { Barriers = barriers }, 15);
    }

    [BrowserFact]
    public async Task ACoveredControlIsSeenCoveredAndPressingItFailsAtOnce()
    {
        await using var world = await Open("cookie-banner");
        var screen = world.Current;
        Assert.Contains("We use cookies to keep your notes.", screen.Text);
        Assert.True(screen.ById("button-save")!.Covered);
        Assert.Contains("(covered by something on top)", screen.ById("button-save")!.Line());
        Assert.False(screen.ById("button-accept")!.Covered);
        Assert.Equal(1, world.BarrierFires["cookie-banner"]);

        var started = DateTime.UtcNow;
        var press = world.Act("button-save");
        Assert.False(press.Succeeded);
        Assert.Contains("is covered by something on top of it", press.Observation);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));       // not the action's 5 s timeout

        Assert.True(world.Act("button-accept").Succeeded);
        Assert.False(world.Current.ById("button-save")!.Covered);
        Assert.Null(world.Current.ById("button-accept"));
    }

    [BrowserFact]
    public async Task ASaveThatDodgesIsAnHonestFailureNotALie()
    {
        await using var world = await Open("save-dodges");
        world.Act("textbox-note", "buy milk");
        var save = world.Page.Locator("button:text-is('Save')");
        var before = await save.BoundingBoxAsync();
        var dodged = world.Act("button-save");
        Assert.False(dodged.Succeeded);
        Assert.Contains("the page did not respond", dodged.Observation);
        Assert.Empty(world.Lies);                                               // the page claimed nothing
        Assert.Empty(demo.App.Notes);
        Assert.NotEqual(before!.X, (await save.BoundingBoxAsync())!.X);       // it moved, and kept its id
        Assert.NotNull(world.Current.ById("button-save"));

        var again = world.Act("button-save");                                  // the barrier fires once
        Assert.True(again.Succeeded);
        Assert.True(world.Has("done:remember-milk"));
        Assert.Equal(1, world.BarrierFires["save-dodges"]);
    }

    [BrowserFact]
    public async Task TypingThatLosesALetterIsReportedWithWhatTheFieldShows()
    {
        await using var world = await Open("note-drops-letter");
        var typed = world.Act("textbox-note", "buy milk");
        Assert.False(typed.Succeeded);
        Assert.Equal("the field shows \"uy milk\", not what was typed".Replace("uy", "by"), typed.Observation);
        Assert.True(world.Act("textbox-note", "buy milk").Succeeded);
        Assert.Equal(1, world.BarrierFires["note-drops-letter"]);
    }

    [BrowserFact]
    public async Task UnderEachPageBarrierAPersistentPersonaGetsItDoneWithNoLieTold()
    {
        // After an honest failure the control layer hides the tool for 3 ticks: a persistent persona waits it out.
        var cover = await Run(["cookie-banner"], "click Save", "click Accept", "type Note: buy milk", "wait", "click Save");
        var dodge = await Run(["save-dodges"], "type Note: buy milk", "click Save", "wait", "wait", "wait", "click Save");
        var drop = await Run(["note-drops-letter"], "type Note: buy milk", "wait", "wait", "wait", "type Note: buy milk", "click Save");
        foreach (var cell in new[] { cover, dodge, drop })
        {
            Assert.True(cell.DataDone, cell.Summary());
            Assert.Equal(0, cell.LiesTold);
            Assert.Contains(cell.Run!.Trace, e => e.Level == "cell" && e.Kind == TraceKind.Blocked);   // the honest failure the design saw
        }
    }

    [BrowserFact]
    public async Task APersonaThatTrustsWhatItTypedSavesTheWrongNoteAndTheDataSaysSo()
    {
        // One type, then save: the field held "by milk", the page said saved, the data has no "buy milk". The type step was
        // reported failed, so a design that reads its honest failures had its warning a step before the save.
        // The judge does not call it the app's lie: the app saved what the field held. It is input lost.
        var cell = await Run(["note-drops-letter"], "type Note: buy milk", "click Save");
        Assert.False(cell.DataDone);
        Assert.Contains("by milk", string.Join(" ", demo.App.Notes));
        Assert.Contains(cell.Run!.Trace, e => e.Action == "textbox-note" && e.Level == "cell" && e.Kind == TraceKind.Blocked);
        Assert.Equal((1, 1), (cell.LiesTold, cell.InputLost));
        Assert.Equal([UxFinding.NotDone, UxFinding.InputLost], UxJudge.Rules(cell).Findings);
        Assert.Contains("the page saved what the field held, not what was typed", cell.Summary());
    }
}
