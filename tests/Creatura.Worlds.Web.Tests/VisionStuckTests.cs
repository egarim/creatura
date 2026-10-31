using System.Text.Json;
using System.Text.Json.Nodes;
using Creatura.Seats;
using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>Vision on the triggers and the stuck alarm (phase 2c): the parts that need no browser.</summary>
public class VisionStuckTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static ModelEndpoint Endpoint(string name, Provider provider = Provider.OpenAiCompatible) =>
        new(name, provider, "http://model.test/v1", name, null, false, false, 1m, 1m);

    [Fact]
    public async Task AnImageGoesAsAContentPartOnTheOpenAiShapeAndAsImagesOnOllamas()
    {
        string? body = null;
        using var http = new HttpClient(new DataCheckTests.StubHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{}"}}],"message":{"content":"{}"},"done":true}"""),
            };
        }));
        var message = new ChatMessage("user", "what do you see?", Images: [new ChatImage("image/jpeg", Jpeg)]);

        await new OpenAiCompatibleTransport(http).CompleteAsync(new ModelRequest { Endpoint = Endpoint("v"), Messages = [message], MaxTokens = 10 }, default);
        var parts = JsonNode.Parse(body!)!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("what do you see?", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(Jpeg), parts[1]!["image_url"]!["url"]!.GetValue<string>());

        await new OllamaTransport(http).CompleteAsync(new ModelRequest { Endpoint = Endpoint("o", Provider.Ollama), Messages = [message], MaxTokens = 10 }, default);
        var ollama = JsonNode.Parse(body!)!["messages"]![0]!;
        Assert.Equal("what do you see?", ollama["content"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Jpeg), ollama["images"]![0]!.GetValue<string>());

        // A message without images is sent as before: content is a string.
        await new OpenAiCompatibleTransport(http).CompleteAsync(new ModelRequest { Endpoint = Endpoint("v"), Messages = [new ChatMessage("user", "hi")], MaxTokens = 10 }, default);
        Assert.Equal("hi", JsonNode.Parse(body!)!["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void AnImageIsNeverWrittenToTheCallLog()
    {
        var record = CallRecord.Of(Endpoint("v"), "persona-vision", "json", "x", [new ChatMessage("user", "look", Images: [new ChatImage("image/jpeg", Jpeg)])], [],
            new ModelReply("{}", null, [], 1, 1, null, null, 0.1, null, null, null, null, "stop", 200, null), true, "with a screenshot (0 KB, not logged)");
        var json = JsonSerializer.Serialize(record);
        Assert.DoesNotContain(Convert.ToBase64String(Jpeg), json);
        Assert.DoesNotContain("images", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not logged", json);
    }

    private static PolicyContext Context(Observation screen, IReadOnlyList<string>? notes = null) => new()
    {
        Facts = new HashSet<string>(), Tools = WebTools.For(screen, DemoFiles.Adapter().Task(null)), Goal = _ => false, GoalText = "x",
        Tick = 3, MaxTicks = 10, Freedom = 0.5, Notes = notes ?? [],
    };

    [Fact]
    public async Task WhenTheAlarmIsRaisedTheVisionModelIsAskedAtOnceWithAScreenshot()
    {
        var transport = new SeatTests.ScriptedTransport("""{"action":"type 1","text":"buy milk","confidence":0.9,"done":false,"why":"the field"}""");
        var client = new ModelClient(transports: [transport]) { RetryDelay = TimeSpan.Zero };
        var adapter = DemoFiles.Adapter();
        var screen = ObservationTests.Screen();
        var signal = new UxStuckSignal();
        var persona = new PersonaPolicy(() => screen, adapter.Persona("hurried"), adapter.Environment("phone"), Endpoint("fast"), client,
            Endpoint("strong"), adapter.Limits, vision: Endpoint("eyes"), screenshot: () => Jpeg, signal: signal);
        signal.Raise("the screen has not changed after the last 2 steps");

        var s = await persona.NextAsync(Context(screen, ["The screen has not changed after your last 2 steps."]));
        Assert.Equal("textbox-note", s.Tool);
        var request = Assert.Single(transport.Requests);                       // the fast seat was never asked
        Assert.Equal("eyes", request.Endpoint.Name);
        Assert.Equal(Jpeg, Assert.Single(request.Messages[1].Images!).Data);
        Assert.Contains("YOU SEEM STUCK: the screen has not changed after the last 2 steps", request.Messages[1].Content);
        Assert.Contains("NOTES:\n  The screen has not changed after your last 2 steps.", request.Messages[1].Content.Replace("\r\n", "\n"));
        Assert.Equal((1, 1), (persona.Telemetry.Escalations, persona.Telemetry.Screenshots));
        Assert.Null(signal.Take());                                            // taken once
        Assert.Contains("+vision:eyes", persona.Name);
    }

    [Fact]
    public async Task WithNoVisionTheStrongerModelIsAskedWithNoImageAndPastTheBudgetTheFastOneIsTold()
    {
        var transport = new SeatTests.ScriptedTransport("""{"action":"click 2","confidence":0.9,"done":false,"why":"save"}""");
        var client = new ModelClient(transports: [transport]) { RetryDelay = TimeSpan.Zero };
        var adapter = DemoFiles.Adapter();
        var screen = ObservationTests.Screen();
        var signal = new UxStuckSignal();
        var persona = new PersonaPolicy(() => screen, adapter.Persona("hurried"), adapter.Environment("phone"), Endpoint("fast"), client,
            Endpoint("strong"), adapter.Limits with { Escalations = 1 }, signal: signal);

        signal.Raise("stuck once");
        await persona.NextAsync(Context(screen));
        Assert.Equal("strong", transport.Requests[0].Endpoint.Name);
        Assert.Null(transport.Requests[0].Messages[1].Images);

        signal.Raise("stuck twice");                                           // the budget is spent: the fast seat is asked, and told
        await persona.NextAsync(Context(screen));
        Assert.Equal("fast", transport.Requests[1].Endpoint.Name);
        Assert.Contains("YOU SEEM STUCK: stuck twice", transport.Requests[1].Messages[1].Content);
        Assert.Equal(1, persona.Telemetry.Escalations);
    }

    private static TickContext Tick(int tick, List<TraceEvent> trace) => new()
    {
        Agent = "a", World = RestoreServiceDomain.NewWorld(), Task = RestoreServiceDomain.Task, Tick = tick, MaxTicks = 20, Notes = [], Trace = trace,
    };

    [Fact]
    public void TheAlarmFiresAfterItsPatienceAndTakesTheControlsAtItsLimit()
    {
        var screen = "A";
        var signal = new UxStuckSignal();
        var alarm = new UxStuckAlarm(() => screen, signal, patience: 2, limit: 4);
        var trace = new List<TraceEvent>();
        var tool = new Tool("button-clear", [], [], "click");
        var outcome = new ActionOutcome("button-clear", true, [], "same page");
        alarm.BeforeRun(Tick(0, trace));

        alarm.AfterAction(Tick(1, trace), tool, outcome, ActionVerdict.Landed);
        Assert.Null(signal.Take());
        alarm.AfterAction(Tick(2, trace), tool, outcome, ActionVerdict.Landed);
        Assert.Equal("the screen has not changed after the last 2 steps", signal.Take());
        Assert.Equal(["The screen has not changed after your last 2 steps."], alarm.PolicyNotes(Tick(3, trace)));
        Assert.Single(trace);

        screen = "B";                                                         // the screen moved: the streak starts again
        alarm.AfterAction(Tick(3, trace), tool, outcome, ActionVerdict.Landed);
        Assert.Equal(0, alarm.Stale);
        Assert.Empty(alarm.PolicyNotes(Tick(4, trace)));

        for (var t = 4; t <= 7; t++) alarm.AfterAction(Tick(t, trace), tool, outcome, ActionVerdict.Landed);
        Assert.True(alarm.TookControls);
        Assert.Equal(2, alarm.Fired);
        Assert.Empty(alarm.VisibleTools(Tick(8, trace), [tool]));
        Assert.Contains(trace, e => e.Detail.Contains("the controls are taken away", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new UxStuckAlarm(() => "", signal, patience: 3, limit: 3));
    }

    [Fact]
    public void ASeatsVisionModelIsCheckedBeforeAnyBrowserStarts()
    {
        var seats = new UxSeats(ModelCatalog.Compile(_ => "set")) { Vision = "nobody" };
        Assert.Contains("no vision model 'nobody'", seats.Check("model:deepseek-chat", DemoFiles.Adapter()));
    }
}

/// <summary>The stuck alarm and vision in a real browser.</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class VisionStuckBrowserTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    [BrowserFact]
    public async Task AScreenshotIsTheViewportAsJpeg()
    {
        var adapter = demo.App.Adapter();
        await using var world = await WebWorld.OpenAsync(demo.Browser, adapter, adapter.Task(null), adapter.Environment("phone"));
        var shot = world.Screenshot();
        Assert.NotNull(shot);
        Assert.Equal([0xFF, 0xD8], shot![..2]);
    }

    [BrowserFact]
    public async Task APersonaPressingSomethingThatChangesNothingIsStoppedByTheAlarmAndEachDesignHandsOff()
    {
        var adapter = demo.App.Adapter() with { Limits = demo.App.Adapter().Limits with { StuckPatience = 2, StuckLimit = 4 } };
        foreach (var design in new[] { "plain", "bach" })
        {
            var cell = await UxRunner.RunAsync(demo.Browser, adapter, adapter.Task(null), adapter.Persona("hurried"), adapter.Environment("desktop"),
                w => new WebScriptPolicy(Enumerable.Repeat("click Clear", 10), () => w.Current),
                (p, t, extra) => UxDesigns.Make(design, p, t, extensions: extra), new WebWorldOptions(), 20);
            Assert.False(cell.DataDone);
            Assert.Equal((1, true), (cell.StuckAlarms, cell.ControlsTaken));
            Assert.True(cell.Ticks < 12, $"{design} ran {cell.Ticks} ticks");             // stopped well before the script or the step budget ran out
            Assert.Contains(cell.Run!.Trace, e => e.Action == "stuck alarm" && e.Tick == 2);
            Assert.Contains(cell.Run.Trace, e => e.Kind == TraceKind.Escalated);           // the design's own hand-off
            Assert.Contains("stuck alarm ×1, controls taken", cell.Summary());
        }
    }

    [BrowserFact]
    public async Task AStuckPersonaSeesTheScreenAndFinds()
    {
        // The fast model keeps pressing Clear; the alarm fires; the vision model, shown a screenshot, types and saves.
        var transport = new StuckThenSeeingTransport();
        var fast = new ModelEndpoint("fast", Provider.OpenAiCompatible, "http://model.test/v1", "fast", null, false, false, null, null);
        var eyes = fast with { Name = "eyes", Model = "eyes" };
        var client = new ModelClient(transports: [transport]) { RetryDelay = TimeSpan.Zero };
        var adapter = demo.App.Adapter();
        var cell = await UxRunner.RunAsync(demo.Browser, adapter, adapter.Task(null), adapter.Persona("careful"), adapter.Environment("desktop"),
            w => new PersonaPolicy(() => w.Current, adapter.Persona("careful"), adapter.Environment("desktop"), fast, client, null, adapter.Limits,
                vision: eyes, screenshot: w.Screenshot, signal: w.Stuck),
            (p, t, extra) => UxDesigns.Make("plain", p, t, extensions: extra), new WebWorldOptions(), 12);
        Assert.True(cell.DataDone, cell.Summary());
        Assert.Equal(1, cell.StuckAlarms);
        Assert.Equal(1, cell.Screenshots);
        Assert.False(cell.ControlsTaken);
        Assert.Contains(transport.Seen, r => r.Endpoint.Name == "eyes" && r.Messages[1].Images is { Count: 1 });
    }

    private sealed class StuckThenSeeingTransport : IModelTransport
    {
        public List<ModelRequest> Seen { get; } = [];

        public Provider Provider => Provider.OpenAiCompatible;

        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            var user = request.Messages[1].Content;
            string Number(string label) => user.Split('\n').Select(l => l.Trim()).First(l => l.Contains(label, StringComparison.Ordinal)).Split('.')[0];
            // The fast model cannot find the field (it presses Clear, in words that vary, so the seat's own same-answer trigger does
            // not fire and only the loop's alarm can see that nothing changes) but can save once the note is there; only the model that sees types.
            var answer = user.Contains("(contains \"buy milk\")", StringComparison.Ordinal)
                ? $$"""{"action":"click {{Number("[button] Save")}}","confidence":0.9,"done":false,"why":"save"}"""
                : request.Endpoint.Name == "fast"
                    ? $$"""{"action":"{{(Seen.Count % 2 == 0 ? "click" : "tap")}} {{Number("[button] Clear")}}","confidence":0.9,"done":false,"why":"clear it"}"""
                    : $$"""{"action":"type {{Number("[textbox] Note")}}","text":"buy milk","confidence":0.9,"done":false,"why":"I can see the field"}""";
            return Task.FromResult(new ModelReply(answer, null, [], 10, 10, null, null, 0.01, null, null, null, null, "stop", 200, null));
        }
    }
}
