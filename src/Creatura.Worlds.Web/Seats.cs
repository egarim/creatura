using System.Globalization;
using System.Text;
using System.Text.Json;
using Creatura.Seats;

namespace Creatura.Worlds.Web;

/// <summary>
/// One action as a person says it (uxkit's answer set): <c>click N</c>, <c>type N</c> with the text, <c>choose N</c>
/// with the option, <c>back</c>, <c>wait</c>, <c>give up</c>. N is a control's number on the screen the person saw.
/// </summary>
public sealed record PersonAction(string Verb, int? Number, string? Text)
{
    public const string GiveUp = "give up";

    /// <summary>Reads <c>click 3</c>, <c>type 2</c>, <c>back</c>, <c>give up</c>; null when the words are none of those.</summary>
    public static PersonAction? Parse(string? action, string? text)
    {
        var words = (action ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var verb = words[0];
        if (verb is "back" or "wait" && words.Length == 1) return new PersonAction(verb, null, null);
        if (string.Join(' ', words) is GiveUp or "giveup" or "stop") return new PersonAction(GiveUp, null, null);
        if (verb is "click" or "tap" or "type" or "choose" or "select" && words.Length == 2
            && int.TryParse(words[1].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return new PersonAction(verb switch { "tap" => "click", "select" => "choose", _ => verb }, n, text);
        }

        return null;
    }

    /// <summary>The tool this action names on the screen seen, or why it names none.</summary>
    public (string? ToolId, string? Problem) Resolve(Observation screen)
    {
        switch (Verb)
        {
            case "back": return (WebTools.Back, null);
            case "wait": return (WebTools.Wait, null);
            case GiveUp: return (null, null);
        }

        var control = Number is { } n ? screen.ByNumber(n) : null;
        if (control is null) return (null, $"no control {Number} on screen");
        var wanted = Verb switch { "type" => ControlKind.Type, "choose" => ControlKind.Choose, _ => ControlKind.Click };
        if (control.Kind != wanted && !(wanted == ControlKind.Click && control.Kind == ControlKind.Choose))
            return (null, $"control {Number} cannot be used with {Verb}");
        if (wanted is ControlKind.Type or ControlKind.Choose && string.IsNullOrEmpty(Text)) return (null, $"{Verb} {Number} needs the text");
        return (control.Id, null);
    }
}

/// <summary>
/// The offline seat for a web world: a script of person actions by the control's visible name, one per tick, in the
/// order a person would take them. It is deterministic, costs nothing, and like every seat it can only take what the
/// architecture shows it: a control that is not on screen, or that the control layer has put out of sight, is not
/// taken. Lines: <c>click Save</c>, <c>type Note: buy milk</c>, <c>choose Colour: red</c>, <c>back</c>, <c>wait</c>,
/// <c>give up</c>; <c>#</c> starts a comment.
/// </summary>
public sealed class WebScriptPolicy : IPolicy
{
    private readonly IReadOnlyList<string> _steps;
    private readonly Func<Observation> _screen;
    private int _next;

    public WebScriptPolicy(IEnumerable<string> lines, Func<Observation> screen, string name = "script")
    {
        _steps = lines.Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList();
        _screen = screen;
        Name = name;
    }

    public static WebScriptPolicy FromFile(string path, Func<Observation> screen) => new(File.ReadAllLines(path), screen, "script:" + Path.GetFileName(path));

    public string Name { get; }

    public int Length => _steps.Count;

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Next(context));

    private PolicySuggestion Next(PolicyContext context)
    {
        if (_next >= _steps.Count) return new PolicySuggestion(null, "script exhausted");
        var step = _steps[_next++];
        var tag = $"script step {_next}: {step}";
        var parsed = ScriptStep.Parse(step);
        if (parsed is null) return new PolicySuggestion(null, $"{tag}: needs 'name: text'");
        var (verb, name, text) = parsed;
        switch (verb)
        {
            case "back": return Visible(WebTools.Back, tag, null, context);
            case "wait": return Visible(WebTools.Wait, tag, null, context);
            case "give": return new PolicySuggestion(null, tag);
        }

        var control = ScriptStep.Find(_screen(), verb, name);
        if (control is null) return new PolicySuggestion(null, $"{tag}: no {verb} control named '{name}' on screen");
        return Visible(control.Id, tag, control.Kind == ControlKind.Click ? null : text, context);
    }

    private static PolicySuggestion Visible(string id, string tag, string? content, PolicyContext context) =>
        context.Tools.Any(t => t.Id == id) ? new PolicySuggestion(id, tag, content) : new PolicySuggestion(null, $"{tag}: {id} is not offered");
}

/// <summary>One line of a script, as the offline seat and the declared sign-in read it: a verb, a control's visible name, a text.</summary>
public sealed record ScriptStep(string Verb, string Name, string Text)
{
    /// <summary><c>click Save</c>, <c>type Note: buy milk</c>, <c>choose Colour: red</c>, <c>back</c>, <c>wait</c>, <c>give up</c>; null when a type or choose has no text.</summary>
    public static ScriptStep? Parse(string line)
    {
        var step = line.Trim();
        var space = step.IndexOf(' ');
        var verb = (space < 0 ? step : step[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : step[(space + 1)..].Trim();
        if (verb is not ("type" or "choose")) return new ScriptStep(verb, rest, "");
        var colon = rest.IndexOf(':');
        return colon < 0 ? null : new ScriptStep(verb, rest[..colon].Trim(), rest[(colon + 1)..].Trim());
    }

    /// <summary>The control on screen the verb can use, by its visible name without case.</summary>
    public static Control? Find(Observation screen, string verb, string name)
    {
        var kind = verb switch { "type" => ControlKind.Type, "choose" => ControlKind.Choose, _ => ControlKind.Click };
        return screen.Controls.FirstOrDefault(c => c.Kind == kind && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What the persona seat spent and saw, for the report.</summary>
public sealed class PersonaTelemetry
{
    public int Calls { get; internal set; }

    public int Escalations { get; internal set; }

    public long PromptTokens { get; internal set; }

    public long CompletionTokens { get; internal set; }

    /// <summary>What the calls cost by the catalog's prices; null while any call's price is unknown.</summary>
    public decimal? Cost { get; internal set; } = 0m;

    /// <summary>The tick the person said they were done, if they did: what the model believed, beside what the data says.</summary>
    public int? BelievedDoneAt { get; internal set; }

    /// <summary>How many screenshots went to the vision model.</summary>
    public int Screenshots { get; internal set; }

    public List<string> EscalationReasons { get; } = [];
}

/// <summary>
/// A person in the decision seat (uxkit's per-step decision, docs/design-web-worlds.md). Each tick the model reads the
/// persona, what the person wants, the screen (only the visible text and the numbered controls) and the last steps,
/// and answers ONE action as JSON: <c>{"action": "click 3", "text": "...", "confidence": 0.8, "done": false, "why": "..."}</c>.
/// When the person is stuck (confidence under 0.4, the same action again on an unchanged screen, an answer that names
/// no control) the tick is asked again of the stronger seat, within the escalation budget. A cost limit stops the
/// seat. <c>done</c> is recorded as what the model believed; it never ends the run: the data does. A persona that remembers
/// (<see cref="UxMemory"/>) is shown its lessons from earlier cells before the screen.
/// </summary>
public sealed class PersonaPolicy : IPolicy
{
    public const string SystemPrompt = """
        You are a real person using an app on your {device}. Who you are: {persona}
        What you want to do: {goal}
        You see only what is on the screen: the visible text and a numbered list of the controls on it.
        Each turn, do ONE thing, as this person would:
          click N | type N (with "text") | choose N (with "text", the option) | back | wait | give up
        Say "done": true only when you believe what you wanted is done.
        Respond with JSON only, exactly:
        {"action": "<one of the above>", "text": "<what to type or choose, or omitted>", "confidence": <0 to 1>, "done": <true|false>, "why": "<one short sentence>"}
        """;

    private readonly Func<Observation> _screen;
    private readonly UxPersona _persona;
    private readonly UxEnvironment _environment;
    private readonly ModelEndpoint _decide;
    private readonly ModelEndpoint? _escalate;
    private readonly ModelClient _client;
    private readonly UxLimits _limits;
    private readonly CallLog _log;
    private readonly IReadOnlyList<string> _memories;
    private readonly ModelEndpoint? _vision;
    private readonly Func<byte[]?>? _screenshot;
    private readonly UxStuckSignal? _signal;
    private string? _lastAction;
    private string? _lastScreen;

    public PersonaPolicy(Func<Observation> screen, UxPersona persona, UxEnvironment environment, ModelEndpoint decide, ModelClient client,
        ModelEndpoint? escalate = null, UxLimits? limits = null, CallLog? log = null, IReadOnlyList<string>? memories = null,
        ModelEndpoint? vision = null, Func<byte[]?>? screenshot = null, UxStuckSignal? signal = null)
    {
        _memories = memories ?? [];
        _vision = vision;
        _screenshot = screenshot;
        _signal = signal;
        _screen = screen;
        _persona = persona;
        _environment = environment;
        _decide = decide;
        _escalate = escalate;
        _client = client;
        _limits = limits ?? new UxLimits();
        _log = log ?? CallLog.None;
    }

    public string Name => $"persona:{_decide.Name}" + (_escalate is null ? "" : $"+{_escalate.Name}") + (_vision is null ? "" : $"+vision:{_vision.Name}");

    public PersonaTelemetry Telemetry { get; } = new();

    /// <summary>The system prompt for a persona on a device, wanting a goal.</summary>
    public static string System(UxPersona persona, UxEnvironment environment, string goal) =>
        SystemPrompt.Replace("{device}", environment.Mobile ? (environment.Width >= 700 ? "tablet" : "phone") : "computer", StringComparison.Ordinal)
            .Replace("{persona}", persona.Description, StringComparison.Ordinal)
            .Replace("{goal}", goal, StringComparison.Ordinal);

    /// <summary>The user message: the tick, the screen, the person's last steps.</summary>
    public static string User(PolicyContext context, Observation screen, string? escalation = null, IReadOnlyList<string>? memories = null)
    {
        var sb = new StringBuilder();
        if (memories is { Count: > 0 })
        {
            sb.AppendLine("WHAT YOU REMEMBER FROM USING THIS APP BEFORE (oldest first):");
            foreach (var line in memories) sb.AppendLine($"  {line}");
        }

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"STEP {context.Tick} of {context.MaxTicks}."));
        if (context.Notes.Count > 0)
        {
            sb.AppendLine("NOTES:");
            foreach (var note in context.Notes) sb.AppendLine($"  {note}");
        }

        sb.Append(screen.Render());
        sb.AppendLine("WHAT YOU DID SO FAR (oldest first):");
        if (context.History.Count == 0) sb.AppendLine("  (nothing yet)");
        foreach (var line in context.History) sb.AppendLine($"  {line}");
        if (escalation is not null) sb.AppendLine($"YOU SEEM STUCK: {escalation}. Look again and choose carefully.");
        sb.Append("Your one action now, JSON only.");
        return sb.ToString();
    }

    public async Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        if (_limits.Cost is { } cap && Telemetry.Cost is { } spent && spent >= cap)
            return new PolicySuggestion(null, string.Create(CultureInfo.InvariantCulture, $"cost limit reached ({spent:0.####} of {cap:0.####})"));

        var screen = _screen();
        var stronger = _vision ?? _escalate;
        var canEscalate = stronger is not null && Telemetry.Escalations < _limits.Escalations;

        // The loop's alarm first: the screen has stopped changing, so the stronger seat is asked at once, without the fast one.
        var alarm = _signal?.Take();
        Answer answer;
        if (alarm is not null && canEscalate)
        {
            answer = await EscalateAsync(stronger!, context, screen, "alarm: " + alarm, alarm, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            answer = await AskAsync(_decide, context, screen, alarm, null, cancellationToken).ConfigureAwait(false);
            var stuck = Stuck(answer, screen);
            if (stuck is not null && stronger is not null && Telemetry.Escalations < _limits.Escalations)
                answer = await EscalateAsync(stronger, context, screen, stuck, stuck, cancellationToken).ConfigureAwait(false);
        }

        _lastAction = answer.Action;
        _lastScreen = screen.Render();
        if (answer.Done && Telemetry.BelievedDoneAt is null) Telemetry.BelievedDoneAt = context.Tick;
        if (answer.Error is not null) return new PolicySuggestion(null, answer.Error);
        if (answer.Done) return new PolicySuggestion(null, $"the person believes they are done: {answer.Why}");

        var action = PersonAction.Parse(answer.Action, answer.Text);
        if (action is null) return new PolicySuggestion(null, $"unreadable action '{answer.Action}'");
        if (action.Verb == PersonAction.GiveUp) return new PolicySuggestion(null, $"gave up: {answer.Why}");
        var (id, problem) = action.Resolve(screen);
        if (id is null) return new PolicySuggestion(null, $"{answer.Action}: {problem}");
        if (!context.Tools.Any(t => t.Id == id)) return new PolicySuggestion(null, $"{answer.Action}: {id} is not offered");
        return new PolicySuggestion(id, $"{answer.Action}: {answer.Why}", action.Text);
    }

    /// <summary>Why this answer means the person is stuck, or null: uxkit's triggers that can be read from one answer and the last.</summary>
    private string? Stuck(Answer answer, Observation screen)
    {
        if (answer.Error is not null) return "the answer could not be read";
        if (answer.Confidence is < 0.4) return string.Create(CultureInfo.InvariantCulture, $"confidence {answer.Confidence:0.00}");
        var action = PersonAction.Parse(answer.Action, answer.Text);
        if (action is null && !answer.Done) return $"'{answer.Action}' is not an action";
        if (action is not null && action.Resolve(screen).Problem is { } problem) return problem;
        if (answer.Action == _lastAction && screen.Render() == _lastScreen && !answer.Done) return "the same action again on an unchanged screen";
        return null;
    }

    /// <summary>The stronger seat, on a trigger: the vision model with a screenshot when there is one, else the escalation model.</summary>
    private async Task<Answer> EscalateAsync(ModelEndpoint endpoint, PolicyContext context, Observation screen, string reason, string escalation, CancellationToken cancellationToken)
    {
        Telemetry.Escalations++;
        Telemetry.EscalationReasons.Add($"tick {context.Tick}: {reason}");
        IReadOnlyList<ChatImage>? images = null;
        if (endpoint == _vision && _screenshot?.Invoke() is { Length: > 0 } shot)
        {
            images = [new ChatImage("image/jpeg", shot)];
            Telemetry.Screenshots++;
        }

        return await AskAsync(endpoint, context, screen, escalation, images, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Answer> AskAsync(ModelEndpoint endpoint, PolicyContext context, Observation screen, string? escalation, IReadOnlyList<ChatImage>? images, CancellationToken cancellationToken)
    {
        var text = User(context, screen, escalation, _memories) + (images is null ? "" : "\nA screenshot of the screen is attached.");
        var messages = new List<ChatMessage>
        {
            new("system", System(_persona, _environment, context.GoalText)),
            new("user", text, Images: images),
        };
        var reply = await _client.ChatAsync(endpoint, messages, null, jsonMode: true, maxTokens: 300, cancellationToken).ConfigureAwait(false);
        Telemetry.Calls++;
        Telemetry.PromptTokens += reply.PromptTokens ?? 0;
        Telemetry.CompletionTokens += reply.CompletionTokens ?? 0;
        var cost = endpoint.Cost(reply.PromptTokens ?? 0, reply.CompletionTokens ?? 0);
        Telemetry.Cost = cost is null || Telemetry.Cost is null ? null : Telemetry.Cost + cost;
        var answer = reply.Failed ? new Answer(null, null, null, false, "", $"{TraceText.ModelError}: {reply.Error}") : Answer.Parse(reply.Content);
        var note = images is null ? escalation : $"{escalation}; with a screenshot ({string.Create(CultureInfo.InvariantCulture, $"{images.Sum(i => i.Data.Length) / 1024}")} KB, not logged)";
        _log.Write(CallRecord.Of(endpoint, images is not null ? "persona-vision" : escalation is null ? "persona" : "persona-escalated", endpoint.Protocol, $"{_persona.Id} tick {context.Tick}", messages, [], reply, answer.Error is null, note));
        return answer;
    }

    private sealed record Answer(string? Action, string? Text, double? Confidence, bool Done, string Why, string? Error)
    {
        public static Answer Parse(string content)
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return new(null, null, null, false, "", "unparseable answer");
            try
            {
                using var doc = JsonDocument.Parse(content[start..(end + 1)]);
                var root = doc.RootElement;
                string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                double? confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
                var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
                return new(Str("action"), Str("text"), confidence, done, Str("why") ?? "", null);
            }
            catch (JsonException)
            {
                return new(null, null, null, false, "", "unparseable answer");
            }
        }
    }
}
