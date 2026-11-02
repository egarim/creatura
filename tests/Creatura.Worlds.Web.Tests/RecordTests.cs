using System.Text.Json;
using System.Text.Json.Nodes;
using Creatura.Worlds.Web;
using Microsoft.Playwright;

namespace Creatura.Worlds.Web.Tests;

/// <summary>The recorder's own shape, with no browser: what a frame keeps and what it leaves out (W31).</summary>
public class RecordShapeTests
{
    private static Observation Screen(params RawControl[] controls) => Observation.From(new RawObservation
    {
        Url = "http://127.0.0.1:1/login", Title = "Sign in", Text = "Sign in\nUser\nPassword\nEnter", Controls = controls,
    });

    [Fact]
    public void ASecretFieldIsShownFilledOrEmptyNeverItsValue()
    {
        var user = new RawControl { Ref = 0, Role = "textbox", Kind = "type", Tag = "input", Name = "User", Value = "ana" };
        var secret = new RawControl { Ref = 1, Role = "textbox", Kind = "type", Tag = "input", Name = "Password", Value = "hunter2", Secret = true };
        var screen = Screen(user, secret);
        Assert.Equal("1. [textbox] User (contains \"ana\")", screen.Controls[0].Line());
        Assert.Equal("2. [textbox] Password (filled)", screen.Controls[1].Line());
        Assert.Equal("2. [textbox] Password (empty)", Screen(user, secret with { Value = "" }).Controls[1].Line());
        Assert.DoesNotContain("hunter2", screen.Render());
        Assert.Equal("hunter2", screen.Controls[1].Value);   // the world still judges typing by the field

        var frame = JsonSerializer.Serialize(UxRecorder.ScreenOf(screen));
        Assert.DoesNotContain("hunter2", frame);
        Assert.Contains("\"value\":\"(filled)\"", frame);
        Assert.Contains("\"secret\":true", frame);
        Assert.Contains("\"value\":\"ana\"", frame);
    }

    [Fact]
    public void TheRecorderWritesAFrameATickAndTheEndInOrderAndKeepsNoSecretTyped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creatura-rec-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var secret = new RawControl { Ref = 0, Role = "textbox", Kind = "type", Tag = "input", Name = "Password", Value = "", Secret = true };
            var screens = new Queue<Observation>([Screen(secret), Screen(secret with { Value = "hunter2" })]);
            var current = screens.Dequeue();
            var recorder = new UxRecorder(dir, () => current, () => [0xFF, 0xD8, 0xFF, 0xD9], () => new CheckResult(false, "no user yet"), () => [], () => "hunter2",
                new UxRecordingHeader("app", "sign-in", "ana", "phone", "script:x", "plain"));
            var trace = new List<TraceEvent>();
            var context = new TickContext { Agent = "t", World = null!, Task = new AgentTask("sign-in", "", _ => false, _ => 0, _ => 0, []), Tick = 1, MaxTicks = 5, Notes = [], Trace = trace };
            recorder.BeforeRun(context);
            trace.Add(new TraceEvent(1, "tissue", "obligations", 0, 0.2, "textbox-password", TraceKind.Acted, "type Password: the usual one"));
            current = screens.Dequeue();
            recorder.AfterAction(context, new Tool("textbox-password", [], [WebTools.Typed("textbox-password")], "type"), new ActionOutcome("textbox-password", true, [], "same page"), ActionVerdict.Landed);
            recorder.Finish(new UxCell { Task = "sign-in", Persona = "ana", Environment = "phone", Seat = "script:x", Agent = "plain loop", DataDone = false, CheckDetail = "no user yet", PageSaidDone = false, Ticks = 1, LiesCaught = 0, Escalations = 0, Run = new AgentRun("plain loop", false, 1, trace, []) });

            var text = File.ReadAllText(recorder.Path);
            Assert.DoesNotContain("hunter2", text);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
            Assert.Equal(["header", "frame", "frame", "tick", "row", "end"], lines.Select(l => (string)l["type"]!));
            Assert.Equal("creatura.web-replay/1", (string)lines[0]["format"]!);
            Assert.Equal((0, "tick-0.jpg"), ((int)lines[1]["frame"]!, (string)lines[1]["image"]!));
            Assert.Equal("(filled)", (string)lines[2]["screen"]!["controls"]![0]!["value"]!);
            var tick = lines[3];
            Assert.Equal((1, 0, 1), ((int)tick["tick"]!, (int)tick["saw"]!, (int)tick["now"]!));
            Assert.Equal("type Password: the usual one", (string)tick["answer"]!);
            Assert.Null(tick["action"]!["content"]);
            Assert.Equal("landed", (string)tick["verdict"]!);
            Assert.Equal((2, 2, 1, false), ((int)lines[5]["frames"]!, (int)lines[5]["images"]!, (int)lines[5]["ticks"]!, (bool)lines[5]["dataDone"]!));
            Assert.True(File.Exists(Path.Combine(dir, "tick-1.jpg")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ACellsFolderIsItsKeySlugged() =>
        Assert.Equal(Path.Combine("rec", "remember-milk-hurried-phone-script-demo-plain"), UxRecorder.FolderFor("rec", "remember-milk", "hurried", "phone", "script:demo", "plain"));
}

/// <summary>A demo run recorded and watched back, in a real Chromium (W30, W32).</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class RecordTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "creatura-rec-" + Guid.NewGuid().ToString("N")[..8]);

    private Task<UxCell> Run(string? recordTo, string[]? barriers = null)
    {
        var adapter = demo.App.Adapter();
        var task = adapter.Task(null);
        var record = recordTo is null ? null : new UxRecordTo(UxRecorder.FolderFor(recordTo, task.Id, "hurried", "phone", "script:demo", "plain"), new UxRecordingHeader(adapter.App, task.Id, "hurried", "phone", "script:demo", "plain"));
        return UxRunner.RunAsync(demo.Browser, adapter, task, adapter.Persona("hurried"), adapter.Environment("phone"),
            w => new WebScriptPolicy(DemoFiles.Script, () => w.Current), (p, t, extra) => UxDesigns.Make("plain", p, t, extensions: extra),
            new WebWorldOptions { Barriers = barriers ?? [] }, null, stuckAlarm: true, record: record);
    }

    [BrowserFact]
    public async Task ARecordedRunHasAFrameForEveryScreenATickForEveryActionAndTheCellAtTheEnd()
    {
        var dir = TempDir();
        try
        {
            var cell = await Run(dir);
            Assert.True(cell.DataDone);
            Assert.Equal(Path.Combine(dir, "remember-milk-hurried-phone-script-demo-plain"), cell.Recording);
            var lines = File.ReadAllLines(Path.Combine(cell.Recording!, UxRecorder.FileName)).Select(l => JsonNode.Parse(l)!).ToList();
            var frames = lines.Where(l => (string)l["type"]! == "frame").ToList();
            var ticks = lines.Where(l => (string)l["type"]! == "tick").ToList();
            Assert.Equal(3, frames.Count);                               // the start page, after typing, after saving
            Assert.Equal(2, ticks.Count);
            Assert.Equal("Notes", (string)frames[0]["screen"]!["title"]!);
            Assert.Contains("Nothing yet.", (string)frames[0]["screen"]!["text"]!);
            Assert.False((bool)frames[0]["check"]!["done"]!);
            Assert.True((bool)frames[2]["check"]!["done"]!);
            Assert.Contains("buy milk", (string)frames[2]["screen"]!["text"]!);

            var typed = ticks[0];
            Assert.Equal(("textbox-note", "buy milk", "landed"), ((string)typed["action"]!["tool"]!, (string)typed["action"]!["content"]!, (string)typed["verdict"]!));
            Assert.Equal("script step 1: type Note: buy milk", (string)typed["answer"]!);
            Assert.Equal((0, 1), ((int)typed["saw"]!, (int)typed["now"]!));
            var saved = ticks[1];
            Assert.Equal(("button-save", "landed", false), ((string)saved["action"]!["tool"]!, (string)saved["verdict"]!, (bool)saved["lie"]!));
            Assert.Contains("Saved.", (string)saved["outcome"]!["observation"]!);

            var end = lines[^1];
            Assert.Equal("end", (string)end["type"]!);
            Assert.Equal((3, 3, 2, true), ((int)end["frames"]!, (int)end["images"]!, (int)end["ticks"]!, (bool)end["dataDone"]!));
            foreach (var n in Enumerable.Range(0, 3))
            {
                var image = File.ReadAllBytes(Path.Combine(cell.Recording!, $"tick-{n}.jpg"));
                Assert.Equal((0xFF, 0xD8), (image[0], image[1]));           // a JPEG
            }

            Assert.Contains(lines, l => (string)l["type"]! == "row" && (string)l["detail"]! == "effect landed");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [BrowserFact]
    public async Task ALieIsMarkedOnTheTickItWasToldAndNothingIsWrittenWhenNotAsked()
    {
        var dir = TempDir();
        try
        {
            var lied = await Run(dir, ["save-lies"]);
            Assert.False(lied.DataDone);
            var ticks = File.ReadAllLines(Path.Combine(lied.Recording!, UxRecorder.FileName)).Select(l => JsonNode.Parse(l)!).Where(l => (string)l["type"]! == "tick").ToList();
            Assert.Equal(2, ticks.Count);
            Assert.True((bool)ticks[1]["lie"]!);
            Assert.Equal("lied", (string)ticks[1]["verdict"]!);

            var plain = await Run(null);
            Assert.Null(plain.Recording);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [BrowserFact]
    public async Task TheViewerReadsARecordingAndStepsThroughIt()
    {
        var dir = TempDir();
        try
        {
            var cell = await Run(dir);
            var viewer = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "web-viewer", "index.html"));
            Assert.True(File.Exists(viewer), $"the viewer is not at {viewer}");
            var page = await demo.Browser.Browser.NewPageAsync();
            try
            {
                await page.GotoAsync(new Uri(viewer).AbsoluteUri);
                await page.SetInputFilesAsync("#files", Directory.GetFiles(cell.Recording!));
                await page.WaitForFunctionAsync("() => document.getElementById('where').textContent.includes('/ 2')");
                Assert.Contains("notes-demo: remember-milk × hurried × phone", await page.InnerTextAsync("#who"));
                Assert.Contains("Nothing yet.", await page.InnerTextAsync("#text"));
                await page.ClickAsync("#next");
                Assert.Contains("script step 1: type Note: buy milk", await page.InnerTextAsync("#tick"));
                Assert.Contains("landed", await page.InnerTextAsync("#tick"));
                await page.ClickAsync("#next");
                Assert.Contains("Tick 2 of 2", await page.TextContentAsync("#tick-title"));
                Assert.Contains("DONE", await page.InnerTextAsync("#check"));
                Assert.Contains("the data says DONE", await page.InnerTextAsync("#end"));
                var src = await page.GetAttributeAsync("#screen img", "src");
                Assert.StartsWith("blob:", src);
                Assert.True(await page.EvaluateAsync<bool>("() => { const i = document.querySelector('#screen img'); return i.complete && i.naturalWidth > 0; }"));
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
