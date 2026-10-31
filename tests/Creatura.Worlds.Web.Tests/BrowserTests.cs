using Creatura.Seats;
using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>
/// A fact that needs a Chromium Playwright can launch. Without one it is skipped with the reason and the fix: Playwright
/// finds its own browsers through <c>PLAYWRIGHT_BROWSERS_PATH</c> or its default cache, or <c>CREATURA_CHROMIUM</c> names
/// one. The browser tests are also in the <c>Browser</c> category (<c>dotnet test --filter Category!=Browser</c> leaves them out).
/// </summary>
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (BrowserProbe.Missing is { } why) Skip = why;
    }
}

internal static class BrowserProbe
{
    public static string? Missing { get; } = Probe();

    private static string? Probe()
    {
        if (Environment.GetEnvironmentVariable("CREATURA_CHROMIUM") is { Length: > 0 } path)
            return File.Exists(path) ? null : $"CREATURA_CHROMIUM names {path}, which does not exist";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? "",
            Path.Combine(home, ".cache", "ms-playwright"),
            Path.Combine(home, "Library", "Caches", "ms-playwright"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright"),
        ];
        foreach (var root in roots.Where(r => r.Length > 0 && Directory.Exists(r)))
        {
            if (Directory.EnumerateDirectories(root, "chromium*").Any()) return null;
        }

        return "no Chromium for Playwright: run `pwsh tests/Creatura.Worlds.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`, "
            + "or set PLAYWRIGHT_BROWSERS_PATH to where one is installed, or CREATURA_CHROMIUM to a Chromium executable";
    }
}

/// <summary>One browser and one demo app for the class; each test opens its own world (a fresh browser context).</summary>
public sealed class DemoFixture : IAsyncLifetime
{
    public WebBrowser Browser { get; private set; } = null!;

    public DemoApp App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        App = DemoApp.Start();
        if (BrowserProbe.Missing is null) Browser = await WebBrowser.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        await App.DisposeAsync();
    }
}

[Trait("Category", "Browser")]
[Collection("browser")]
public class BrowserTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    private UxAdapter Adapter => demo.App.Adapter();

    private Task<WebWorld> Open(string env = "desktop", params string[] barriers)
    {
        var adapter = Adapter;
        return WebWorld.OpenAsync(demo.Browser, adapter, adapter.Task(null), adapter.Environment(env), new WebWorldOptions { Barriers = barriers });
    }

    private Task<UxCell> Run(string env = "desktop", string persona = "hurried", string[]? barriers = null, Func<WebWorld, IPolicy>? seat = null, Func<IPolicy, AgentTask, IAgent>? agent = null)
    {
        var adapter = Adapter;
        return UxRunner.RunAsync(demo.Browser, adapter, adapter.Task(null), adapter.Persona(persona), adapter.Environment(env),
            seat ?? (w => new WebScriptPolicy(DemoFiles.Script, () => w.Current)), agent, new WebWorldOptions { Barriers = barriers ?? [] });
    }

    [BrowserFact]
    public async Task TheObservationIsTheVisibleTextAndTheControlsOnScreen()
    {
        await using var world = await Open();
        var screen = world.Current;
        Assert.Equal("Notes", screen.Title);
        Assert.Contains("Things to remember, kept for you.", screen.Text);
        Assert.Contains("Nothing yet.", screen.Text);
        Assert.DoesNotContain("Creatura's web-world demo", screen.Text);   // hidden: not seen, not shown
        Assert.Equal(["textbox-note", "button-save", "button-clear", "link-about"], screen.Controls.Select(c => c.Id));
        var note = screen.Controls[0];
        Assert.Equal((ControlKind.Type, "Note", 1), (note.Kind, note.Name, note.Number));   // named by its label
        Assert.Equal(ControlKind.Click, screen.ById("button-clear")!.Kind);                // a cursor:pointer leaf is a control
        Assert.Equal(["page:/"], world.Facts);
        Assert.False(world.LastCheck.Done);
    }

    [BrowserFact]
    public async Task ActingMovesThePageAndTheToolsFollowTheScreen()
    {
        await using var world = await Open();
        var typed = world.Act("textbox-note", "buy milk");
        Assert.True(typed.Succeeded);
        Assert.Contains("(contains \"buy milk\")", world.Current.Controls[0].Line());

        var about = world.Act("link-about");
        Assert.True(about.Succeeded);
        Assert.Contains("Creatura's web-world demo", world.Current.Text);
        Assert.Equal(2, world.Ticks);

        var nothing = world.Act("button-nowhere");
        Assert.False(nothing.Succeeded);
        Assert.Equal("unknown tool", nothing.Observation);
    }

    [BrowserFact]
    public async Task AnActionThatTimesOutIsAFailedActionNotACrash()
    {
        // Playwright reports a timeout as System.TimeoutException, not a PlaywrightException: the world must catch both.
        var adapter = Adapter;
        await using var world = await WebWorld.OpenAsync(demo.Browser, adapter, adapter.Task(null), adapter.Environment("desktop"), new WebWorldOptions { ActionTimeoutMs = 300 });
        await world.Page.EvaluateAsync("() => { document.querySelector('[data-creatura-ref]').style.visibility = 'hidden'; }");
        var outcome = world.Act("textbox-note", "buy milk");
        Assert.False(outcome.Succeeded);
        Assert.Contains("did not go through", outcome.Observation);
    }

    [BrowserFact]
    public async Task ASaveTheDataBacksHasLanded()
    {
        await using var world = await Open();
        world.Act("textbox-note", "buy milk");
        var save = world.Tool("button-save")!;
        var outcome = world.Act(save.Id);
        Assert.True(outcome.Succeeded);
        Assert.Contains("Saved.", outcome.Observation);
        Assert.True(world.EffectsLanded(save));
        Assert.Equal(ActionVerdict.Landed, ((IWorldView)world).Verdict(save, outcome));
        Assert.Equal(["buy milk"], demo.App.Notes);
    }

    [BrowserFact]
    public async Task TheLieBarrierIsJudgedALie()
    {
        await using var world = await Open("desktop", "save-lies");
        world.Act("textbox-note", "buy milk");
        var save = world.Tool("button-save")!;
        var outcome = world.Act(save.Id);
        Assert.True(outcome.Succeeded);                       // the page says saved, and shows the note
        Assert.Contains("Saved.", world.Current.Text);
        Assert.Contains("buy milk", world.Current.Text);
        Assert.Empty(demo.App.Notes);                         // the app never heard of it
        Assert.False(world.Has("done:remember-milk"));
        Assert.Equal(ActionVerdict.Lied, ((IWorldView)world).Verdict(save, outcome));
        Assert.Equal(1, world.BarrierFires["save-lies"]);
    }

    [BrowserFact]
    public async Task ASaveRefusedAgainIsStillRefusedThoughTheMessageIsNotNew()
    {
        // Save on an empty note twice: the page says "Write something first." both times. The second time the text is not
        // new, and before this rule the world read the second press as a success the data did not back: a lie it never told.
        await using var world = await Open();
        var save = world.Tool("button-save")!;
        Assert.False(world.Act(save.Id).Succeeded);
        var again = world.Act(save.Id);
        Assert.False(again.Succeeded);
        Assert.Contains("Write something first", again.Observation);
        Assert.Empty(world.Lies);
    }

    [BrowserFact]
    public async Task AFailureThePageShowsIsAnHonestFailure()
    {
        await using var world = await Open("desktop", "save-fails");
        world.Act("textbox-note", "buy milk");
        var save = world.Tool("button-save")!;
        var outcome = world.Act(save.Id);
        Assert.False(outcome.Succeeded);
        Assert.Contains("could not be saved", outcome.Observation);
        Assert.Equal(ActionVerdict.HonestFailure, ((IWorldView)world).Verdict(save, outcome));
    }

    [BrowserFact]
    public async Task TheOfflineDemoRunsToTheGoalTheDataConfirms()
    {
        var cell = await Run();
        Assert.True(cell.DataDone, cell.CheckDetail);
        Assert.True(cell.Run!.GoalReached);
        Assert.Equal(2, cell.Ticks);
        Assert.Equal(0, cell.LiesCaught);
        Assert.False(cell.FalseDone);
        Assert.Contains("the data says DONE", cell.Summary());
    }

    [BrowserFact]
    public async Task TheOfflineDemoOnAPhone()
    {
        var cell = await Run(env: "phone");
        Assert.True(cell.DataDone, cell.CheckDetail);
        Assert.Equal("phone", cell.Environment);
    }

    [BrowserFact]
    public async Task UnderTheLieTheRunCatchesItAndNeverCountsTheGoal()
    {
        var cell = await Run(barriers: ["save-lies"]);
        Assert.False(cell.DataDone);
        Assert.False(cell.Run!.GoalReached);
        Assert.True(cell.PageSaidDone);
        Assert.True(cell.FalseDone);
        Assert.Equal(1, cell.LiesCaught);
        var lie = Assert.Single(cell.Run.Trace, Creatura.Bench.Measures.IsCaughtLie);
        Assert.Equal("button-save", lie.Action);
        Assert.Equal(2, lie.Tick);
        Assert.Contains("the page said it was saved", cell.Summary());
    }

    [BrowserFact]
    public async Task TheAppsOwnLieIsCaughtTheSameWay()
    {
        demo.App.DropWrites = true;
        try
        {
            var cell = await Run();
            Assert.False(cell.DataDone);
            Assert.Equal(1, cell.LiesCaught);
            Assert.Empty(cell.BarrierFires);
        }
        finally
        {
            demo.App.DropWrites = false;
        }
    }

    [BrowserFact]
    public async Task ADesignFromTheCatalogRunsOnTheWebWorld()
    {
        var cell = await Run(agent: (policy, task) => AgentCatalog.Make("bach", policy, task));
        Assert.True(cell.DataDone, cell.CheckDetail);
        Assert.Equal("Bach agent", cell.Agent);
    }

    [BrowserFact]
    public async Task ADesignCatchesTheLieInItsOwnWords()
    {
        var cell = await Run(barriers: ["save-lies"], agent: (policy, task) => AgentCatalog.Make("bach", policy, task));
        Assert.False(cell.DataDone);
        Assert.False(cell.Run!.GoalReached);
        Assert.Equal(1, cell.LiesCaught);
        Assert.Contains(cell.Run.Trace, e => e.Action == "button-save" && e.Detail.StartsWith("contradiction", StringComparison.Ordinal));
    }

    [BrowserFact]
    public async Task APersonaOverAModelRunsTheDemo()
    {
        // A stub model that reads the screen it is shown, as a person would: type into the field labelled Note, then save, then say done.
        var transport = new ReadingTransport();
        var fast = new ModelEndpoint("fast", Provider.OpenAiCompatible, "http://model.test/v1", "fast", null, false, false, null, null);
        var client = new ModelClient(transports: [transport]) { RetryDelay = TimeSpan.Zero };
        var adapter = Adapter;
        var cell = await Run(seat: w => new PersonaPolicy(() => w.Current, adapter.Persona("careful"), adapter.Environment("desktop"), fast, client));
        Assert.True(cell.DataDone, cell.CheckDetail);
        Assert.Equal(2, cell.Ticks);
        Assert.Contains("[textbox] Note", transport.Prompts[0]);
    }

    private sealed class ReadingTransport : IModelTransport
    {
        public List<string> Prompts { get; } = [];

        public Provider Provider => Provider.OpenAiCompatible;

        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var user = request.Messages[1].Content;
            Prompts.Add(user);
            string Number(string label) => user.Split('\n').Select(l => l.Trim()).First(l => l.Contains(label, StringComparison.Ordinal)).Split('.')[0];
            var answer = user.Contains("(contains \"buy milk\")", StringComparison.Ordinal)
                ? $$"""{"action":"click {{Number("[button] Save")}}","confidence":0.9,"done":false,"why":"save it"}"""
                : user.Contains("Saved.", StringComparison.Ordinal)
                    ? """{"action":"wait","confidence":0.9,"done":true,"why":"it says saved"}"""
                    : $$"""{"action":"type {{Number("[textbox] Note")}}","text":"buy milk","confidence":0.9,"done":false,"why":"write it"}""";
            return Task.FromResult(new ModelReply(answer, null, [], 10, 10, null, null, 0.01, null, null, null, null, "stop", 200, null));
        }
    }
}
