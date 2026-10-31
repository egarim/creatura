using System.Text.Json.Nodes;
using Creatura.Seats;
using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>The honest success check: one request, one assertion on the app's own JSON. No browser.</summary>
public class DataCheckTests
{
    private static readonly JsonNode Notes = JsonNode.Parse("""{"items":[{"text":"Buy milk","id":1},{"text":"call mum","id":2}],"count":2,"owner":{"name":"Ana"}}""")!;

    [Fact]
    public void ContainsFindsAnElementWhoseFieldsInclude()
    {
        var check = new DataCheck { Url = "/api/notes", Path = "items", Contains = JsonNode.Parse("""{"text":"call mum"}""") };
        Assert.True(check.Evaluate(Notes).Done);
        Assert.False((check with { Contains = JsonNode.Parse("""{"text":"buy milk"}""") }).Evaluate(Notes).Done);
        Assert.True((check with { Contains = JsonNode.Parse("""{"text":"buy milk"}"""), IgnoreCase = true }).Evaluate(Notes).Done);
    }

    [Fact]
    public void EqualsCountExistsAndPaths()
    {
        Assert.True(new DataCheck { Url = "/x", Path = "owner.name", Is = JsonValue.Create("Ana") }.Evaluate(Notes).Done);
        Assert.True(new DataCheck { Url = "/x", Path = "items.1.id", Is = JsonValue.Create(2) }.Evaluate(Notes).Done);
        Assert.True(new DataCheck { Url = "/x", Path = "items", Count = 2 }.Evaluate(Notes).Done);
        Assert.False(new DataCheck { Url = "/x", Path = "items", MinCount = 3 }.Evaluate(Notes).Done);
        Assert.True(new DataCheck { Url = "/x", Path = "owner.email", Exists = false }.Evaluate(Notes).Done);
        var missing = new DataCheck { Url = "/x", Path = "items.9.text", Is = JsonValue.Create("x") }.Evaluate(Notes);
        Assert.False(missing.Done);
        Assert.Contains("absent", missing.Detail);
    }

    [Fact]
    public void ACheckNeedsExactlyOneAssertion()
    {
        Assert.NotNull(new DataCheck { Url = "/x" }.Problem());
        Assert.NotNull(new DataCheck { Url = "/x", Count = 1, Exists = true }.Problem());
        Assert.Null(new DataCheck { Url = "/x", Count = 1 }.Problem());
    }

    [Fact]
    public async Task ARequestThatFailsIsACheckThatDoesNotHold()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));
        var adapter = DemoFiles.Adapter() with { BaseUrl = "http://app.test/" };
        var result = await new DataCheck { Url = "/api/notes", Path = "items", MinCount = 1 }.RunAsync(http, adapter);
        Assert.False(result.Done);
        Assert.Contains("500", result.Detail);
    }

    [Fact]
    public async Task TheCheckReadsTheAnswerAtTheResolvedUrl()
    {
        string? asked = null;
        using var http = new HttpClient(new StubHandler(r =>
        {
            asked = r.RequestUri!.ToString();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"items":[{"text":"buy milk"}]}""") };
        }));
        var adapter = DemoFiles.Adapter() with { BaseUrl = "http://app.test/base/" };
        var result = await adapter.Task(null).Check!.RunAsync(http, adapter);
        Assert.True(result.Done, result.Detail);
        Assert.Equal("http://app.test/base/api/notes", asked);
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
}

/// <summary>The adapter file: the demo's parses; a broken one says everything wrong at once.</summary>
public class AdapterTests
{
    [Fact]
    public void TheDemoAdapterParses()
    {
        var adapter = DemoFiles.Adapter();
        Assert.Equal("notes-demo", adapter.App);
        Assert.Equal(["hurried", "careful"], adapter.Personas.Select(p => p.Id));
        Assert.True(adapter.Environment("phone").Mobile);
        Assert.Equal(UxBarrierKind.Lie, adapter.Barrier("save-lies").Kind);
        Assert.Equal("done:remember-milk", adapter.Task("remember-milk").DoneFact);
        Assert.DoesNotContain("Save", adapter.Task(null).Prompt);   // what the person knows, never the screen's words
    }

    [Fact]
    public void ABrokenAdapterNamesEveryProblem()
    {
        var e = Assert.Throws<FormatException>(() => UxAdapter.Parse("""
            { "app": "x", "personas": [], "environments": [{"id":"e","width":100,"height":100}],
              "tasks": [{"id":"t","prompt":"p"}, {"id":"t","prompt":"q","check":{"url":"/a"}}] }
            """));
        Assert.Contains("no personas", e.Message);
        Assert.Contains("too small", e.Message);
        Assert.Contains("task t twice", e.Message);
        Assert.Contains("no check", e.Message);
        Assert.Contains("exactly one assertion", e.Message);
    }
}

/// <summary>What the persona sees: ids, numbers, lines. The page script's half is in BrowserTests.</summary>
public class ObservationTests
{
    internal static Observation Screen() => Observation.From(new RawObservation
    {
        Url = "http://127.0.0.1:5000/notes?x=1", Title = "Notes", Text = "Notes\nThings to remember",
        Controls =
        [
            new RawControl { Ref = 0, Role = "textbox", Kind = "type", Name = "Note", Value = "" },
            new RawControl { Ref = 1, Role = "button", Kind = "click", Name = "Save" },
            new RawControl { Ref = 2, Role = "button", Kind = "click", Name = "Save" },
            new RawControl { Ref = 3, Role = "combobox", Kind = "choose", Name = "Colour", Value = "Red", Options = ["Red", "Blue"] },
            new RawControl { Ref = 4, Role = "link", Kind = "click", Name = "" , InViewport = false },
        ],
    });

    [Fact]
    public void IdsAreSlugsOfRoleAndNameAndNumbersFollowTheScreen()
    {
        var screen = Screen();
        Assert.Equal(["textbox-note", "button-save", "button-save-2", "combobox-colour", "link-5"], screen.Controls.Select(c => c.Id));
        Assert.Equal([1, 2, 3, 4, 5], screen.Controls.Select(c => c.Number));
        Assert.Equal("/notes", screen.Path);
    }

    [Fact]
    public void TheSameControlKeepsItsIdWhenTheScreenAroundItChanges()
    {
        var before = Screen();
        var after = Observation.From(new RawObservation { Controls = [new RawControl { Ref = 6, Role = "link", Name = "Help" }, new RawControl { Ref = 7, Role = "button", Name = "Close" }, new RawControl { Ref = 8, Role = "button", Name = "Save" }] });
        Assert.Equal(before.Controls[1].Id, after.Controls[2].Id);
        Assert.NotEqual(before.Controls[1].Number, after.Controls[2].Number);
    }

    [Fact]
    public void TheRenderedScreenIsTheVisibleTextAndTheNumberedControlsOnly()
    {
        var text = Screen().Render();
        Assert.Contains("1. [textbox] Note (empty)", text);
        Assert.Contains("2. [button] Save", text);
        Assert.Contains("4. [combobox] Colour (chosen: \"Red\"; options: Red | Blue)", text);
        Assert.Contains("5. [link] (no label) (below, scroll to reach)", text);
        Assert.Contains("Things to remember", text);
        Assert.DoesNotContain("textbox-note", text);   // ids are the architecture's, numbers are the person's
        Assert.DoesNotContain("127.0.0.1", text);
    }
}

/// <summary>Controls to tools: one per control, a commit promises the done fact, typing and choosing need a payload.</summary>
public class WebToolsTests
{
    [Fact]
    public void EachControlIsAToolAndACommitPromisesTheDoneFact()
    {
        var task = DemoFiles.Adapter().Task(null);
        var tools = WebTools.For(ObservationTests.Screen(), task);
        Assert.Equal(["textbox-note", "button-save", "button-save-2", "combobox-colour", "link-5", "back", "wait"], tools.Select(t => t.Id));

        var note = tools[0];
        Assert.Equal("type", note.Kind);
        Assert.True(note.NeedsPayload);
        Assert.Equal(["typed:textbox-note"], note.Grants);   // the field shows what was typed: the page can be read for it

        var save = tools[1];
        Assert.Equal(WebTools.SubmitKind, save.Kind);
        Assert.Equal([task.DoneFact], save.Grants);
        Assert.Empty(save.Requires);

        Assert.Equal("choose", tools[3].Kind);
        Assert.Contains("Red | Blue", tools[3].PayloadHint);
        Assert.Equal("click", tools[4].Kind);
        Assert.Empty(tools[4].Grants);
    }

    [Fact]
    public void ACommitIsNotTheReleaseKindTheStageInterlockGates()
    {
        Assert.NotEqual(WebTools.SubmitKind, WebTask.For(DemoFiles.Adapter().Task(null)).ReleaseKind);
    }

    [Fact]
    public void TheTaskIsDoneOnlyByTheDoneFact()
    {
        var task = WebTask.For(DemoFiles.Adapter().Task(null));
        Assert.False(task.Goal(new HashSet<string> { "page:/" }));
        Assert.True(task.Goal(new HashSet<string> { "page:/", "done:remember-milk" }));
    }
}

/// <summary>The two seats: the offline script and the persona over a model (a stub transport here).</summary>
public class SeatTests
{
    private static PolicyContext Context(Observation screen, UxTask task, int tick = 1, IReadOnlyList<Tool>? tools = null) => new()
    {
        Facts = new HashSet<string>(), Tools = tools ?? WebTools.For(screen, task), Goal = _ => false, GoalText = task.Prompt,
        Tick = tick, MaxTicks = 10, Freedom = 0.5,
    };

    [Fact]
    public async Task TheScriptTakesControlsByTheirVisibleNames()
    {
        var screen = ObservationTests.Screen();
        var task = DemoFiles.Adapter().Task(null);
        var script = new WebScriptPolicy(["# a comment", "type Note: buy milk", "choose colour: Blue", "click Save", "click Nowhere", "back"], () => screen);
        var ctx = Context(screen, task);
        Assert.Equal(("textbox-note", "buy milk"), Pick(await script.NextAsync(ctx)));
        Assert.Equal(("combobox-colour", "Blue"), Pick(await script.NextAsync(ctx)));
        Assert.Equal(("button-save", null), Pick(await script.NextAsync(ctx)));
        var nowhere = await script.NextAsync(ctx);
        Assert.Null(nowhere.Tool);
        Assert.Contains("no click control named 'Nowhere'", nowhere.Rationale);
        Assert.Equal(("back", null), Pick(await script.NextAsync(ctx)));
        Assert.Equal("script exhausted", (await script.NextAsync(ctx)).Rationale);
    }

    [Fact]
    public async Task TheScriptCannotTakeAControlTheLayerHid()
    {
        var screen = ObservationTests.Screen();
        var task = DemoFiles.Adapter().Task(null);
        var script = new WebScriptPolicy(["click Save"], () => screen);
        var hidden = WebTools.For(screen, task).Where(t => t.Id != "button-save").ToList();
        var s = await script.NextAsync(Context(screen, task, tools: hidden));
        Assert.Null(s.Tool);
        Assert.Contains("not offered", s.Rationale);
    }

    private static (string?, string?) Pick(PolicySuggestion s) => (s.Tool, s.Content);

    [Theory]
    [InlineData("click 2", null, "click", 2)]
    [InlineData("Tap #3", null, "click", 3)]
    [InlineData("type 1", "milk", "type", 1)]
    [InlineData("select 4", "Blue", "choose", 4)]
    [InlineData("back", null, "back", null)]
    [InlineData("give up", null, "give up", null)]
    public void PersonActionsParse(string words, string? text, string verb, int? number)
    {
        var action = PersonAction.Parse(words, text);
        Assert.NotNull(action);
        Assert.Equal(verb, action.Verb);
        Assert.Equal(number, action.Number);
    }

    [Fact]
    public void AnActionResolvesOnlyToAControlOnTheScreenItWasChosenOn()
    {
        var screen = ObservationTests.Screen();
        Assert.Equal(("textbox-note", null), PersonAction.Parse("type 1", "x")!.Resolve(screen));
        Assert.Null(PersonAction.Parse("type 1", null)!.Resolve(screen).ToolId);
        Assert.Contains("no control 9", PersonAction.Parse("click 9", null)!.Resolve(screen).Problem);
        Assert.Contains("cannot be used", PersonAction.Parse("type 2", "x")!.Resolve(screen).Problem);
        Assert.Null(PersonAction.Parse("jump 2", null));
    }

    [Fact]
    public async Task ThePersonaSeesOnlyTheScreenAndAnswersOneAction()
    {
        var transport = new ScriptedTransport("""{"action":"type 1","text":"buy milk","confidence":0.9,"done":false,"why":"the note field"}""");
        var (persona, screen, task) = Fixture(transport, escalate: false);
        var s = await persona.NextAsync(Context(screen, task));
        Assert.Equal("textbox-note", s.Tool);
        Assert.Equal("buy milk", s.Content);
        var request = Assert.Single(transport.Requests);
        var user = request.Messages[1].Content;
        Assert.Contains("2. [button] Save", user);
        Assert.DoesNotContain("button-save", user);          // no tool ids, no requires and grants
        Assert.DoesNotContain("done:remember-milk", user);   // no facts: the person cannot see the data
        Assert.Contains("You are in a hurry", request.Messages[0].Content);
        Assert.Contains("phone", request.Messages[0].Content);
    }

    [Fact]
    public async Task LowConfidenceEscalatesToTheStrongerSeatWithinTheBudget()
    {
        var transport = new ScriptedTransport(
            """{"action":"click 5","confidence":0.2,"done":false,"why":"not sure"}""",
            """{"action":"click 2","confidence":0.8,"done":false,"why":"save it"}""",
            """{"action":"click 5","confidence":0.1,"done":false,"why":"not sure"}""");
        var (persona, screen, task) = Fixture(transport, escalate: true, budget: 1);
        var first = await persona.NextAsync(Context(screen, task));
        Assert.Equal("button-save", first.Tool);
        Assert.Equal(["fast", "strong"], transport.Requests.Select(r => r.Endpoint.Name));
        Assert.Contains("YOU SEEM STUCK: confidence 0.20", transport.Requests[1].Messages[1].Content);

        var second = await persona.NextAsync(Context(screen, task, tick: 2));   // the budget is spent: the fast seat's answer stands
        Assert.Equal("link-5", second.Tool);
        Assert.Equal(1, persona.Telemetry.Escalations);
        Assert.Equal(3, persona.Telemetry.Calls);
    }

    [Fact]
    public async Task DoneIsWhatThePersonBelievedNotTheResult()
    {
        var transport = new ScriptedTransport("""{"action":"wait","confidence":0.9,"done":true,"why":"it says saved"}""");
        var (persona, screen, task) = Fixture(transport, escalate: false);
        var s = await persona.NextAsync(Context(screen, task, tick: 4));
        Assert.Null(s.Tool);
        Assert.Equal(4, persona.Telemetry.BelievedDoneAt);
    }

    [Fact]
    public async Task TheCostLimitStopsTheSeat()
    {
        var transport = new ScriptedTransport("""{"action":"click 2","confidence":0.9,"done":false,"why":"x"}""") { PromptTokens = 1_000_000 };
        var (persona, screen, task) = Fixture(transport, escalate: false, cost: 0.1m);
        Assert.NotNull((await persona.NextAsync(Context(screen, task))).Tool);
        var stopped = await persona.NextAsync(Context(screen, task, tick: 2));
        Assert.Null(stopped.Tool);
        Assert.Contains("cost limit", stopped.Rationale);
        Assert.Single(transport.Requests);
    }

    private static (PersonaPolicy, Observation, UxTask) Fixture(ScriptedTransport transport, bool escalate, int budget = 3, decimal? cost = null)
    {
        var adapter = DemoFiles.Adapter();
        var screen = ObservationTests.Screen();
        var fast = new ModelEndpoint("fast", Provider.OpenAiCompatible, "http://model.test/v1", "fast", null, false, false, 1m, 1m);
        var strong = fast with { Name = "strong", Model = "strong" };
        var client = new ModelClient(transports: [transport]) { RetryDelay = TimeSpan.Zero };
        var persona = new PersonaPolicy(() => screen, adapter.Persona("hurried"), adapter.Environment("phone"), fast, client,
            escalate ? strong : null, adapter.Limits with { Escalations = budget, Cost = cost });
        return (persona, screen, adapter.Task(null));
    }

    internal sealed class ScriptedTransport(params string[] answers) : IModelTransport
    {
        private int _next;

        public List<ModelRequest> Requests { get; } = [];

        public long PromptTokens { get; init; } = 100;

        public Provider Provider => Provider.OpenAiCompatible;

        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var content = answers[Math.Min(_next++, answers.Length - 1)];
            return Task.FromResult(new ModelReply(content, null, [], PromptTokens, 20, null, null, 0.1, null, null, null, null, "stop", 200, null));
        }
    }
}
