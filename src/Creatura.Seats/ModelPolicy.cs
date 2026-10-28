using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Seats;

/// <summary>
/// Any catalogued model in the decision seat of a creatura. The model chooses one action per tick from the tools the
/// architecture makes visible; with native tool calling the tools are functions and the choice is a call, otherwise
/// the choice is a JSON object of the one shape <c>{tool, why, content}</c>. Everything it proposes is validated here:
/// only a visible tool id or no action is an answer, a hidden tool named is refused and never obeyed. Every call is
/// logged with its telemetry, and which protocol was used is recorded, because that difference decided the hard
/// benchmark module. A failed call (after the client's one retry) is a <c>model error</c> suggestion with no tool.
/// </summary>
public sealed class ModelPolicy : IPolicy
{
    public const string SystemPrompt = """
        You are the decision component of an autonomous agent acting on a case toward a goal.
        You are called once per tick. Choose exactly ONE action that moves the case toward the goal.

        Rules:
        1. Only choose actions from the list you are given. An action that is not listed does not exist for you.
        2. An action can run only when all of its "requires" facts are already true.
        3. Do not repeat an action whose "grants" facts are already true.
        4. Read the HISTORY. If an action reported success but its effect is not among the facts, that action
           lies: do not choose it again.
        5. If no listed action can make progress, choose no action.
        6. If an action needs a payload (marked NEEDS PAYLOAD), supply it as "content".
        """;

    public const string JsonInstruction = """
        Respond with JSON only, exactly: {"tool": "<action id or null>", "why": "<one short sentence>", "content": "<payload, or omitted>"}
        """;

    /// <summary>The function a native-tools seat calls to choose nothing; the JSON protocol writes <c>null</c>.</summary>
    public const string NoAction = "no_action";

    private readonly ModelEndpoint _endpoint;
    private readonly ModelClient _client;
    private readonly CallLog _log;
    private readonly string _contextTag;

    public ModelPolicy(ModelEndpoint endpoint, ModelClient client, CallLog? log = null, string contextTag = "")
    {
        _endpoint = endpoint;
        _client = client;
        _log = log ?? CallLog.None;
        _contextTag = contextTag;
    }

    public string Name => _endpoint.Name;

    public ModelEndpoint Endpoint => _endpoint;

    public string Protocol => _endpoint.Protocol;

    public SeatTelemetry Telemetry { get; } = new();

    /// <summary>The call log this seat writes to; its path, when it has one, is what a row keeps.</summary>
    public CallLog Log => _log;

    /// <summary>
    /// A set point for how much the seat is shown, in tokens (estimated at three characters each, a conservative count against the tokenizers measured here, tools included).
    /// Null means no budget. When a prompt would exceed it, sections are trimmed in order of least value for the
    /// decision: history to the last three lines, then the record (biology) to its first two lines, then history to
    /// one line, then the self-model to its first four lines. Facts, tools and the goal are never trimmed.
    /// </summary>
    public int? PromptBudgetTokens { get; set; }

    /// <summary>How many prompts the budget trimmed in this policy's life.</summary>
    public int PromptsTrimmed { get; private set; }

    /// <summary>
    /// The system and user messages this seat sends for a context, exactly as <see cref="NextAsync"/> builds them
    /// (with no prompt budget): what a dataset renders a decision in, so a model trained on it sees the seat's own prompt.
    /// </summary>
    public static (string System, string User) RenderPrompt(PolicyContext context, bool nativeTools) =>
        (nativeTools ? SystemPrompt : SystemPrompt + "\n" + JsonInstruction, BuildUserPrompt(context, nativeTools));

    public async Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var system = RenderPrompt(context, _endpoint.NativeTools).System;
        var (tools, ids) = _endpoint.NativeTools ? BuildTools(context.Tools) : (null, null);
        var toolChars = tools is null ? 0 : tools.Sum(t => t.Name.Length + t.Description.Length + t.Parameters.ToJsonString().Length);
        var trim = 0;
        var user = BuildUserPrompt(context, _endpoint.NativeTools, trim);
        if (PromptBudgetTokens is { } budget)
        {
            while (trim < 4 && (system.Length + user.Length + toolChars) / 3 > budget)
            {
                user = BuildUserPrompt(context, _endpoint.NativeTools, ++trim);
            }

            if (trim > 0) PromptsTrimmed++;
        }

        var messages = new List<ChatMessage>
        {
            new("system", system),
            new("user", user),
        };
        var maxTokens = context.Tools.Any(t => t.NeedsPayload) ? 4096 : 300;

        var reply = await _client.ChatAsync(_endpoint, messages, tools, jsonMode: !_endpoint.NativeTools, maxTokens, cancellationToken).ConfigureAwait(false);

        string? tool;
        string why;
        string? content;
        string? note = null;
        if (reply.Failed)
        {
            (tool, why, content) = (null, $"{TraceText.ModelError}: {reply.Error}", null);
        }
        else if (_endpoint.NativeTools && reply.ToolCalls.Count > 0)
        {
            var call = reply.ToolCalls[0];
            var args = ParseArgs(call.ArgumentsJson);
            tool = call.Name == NoAction ? null : ids!.GetValueOrDefault(call.Name, call.Name);
            why = args.TryGetValue("why", out var w) ? w : (string.IsNullOrWhiteSpace(reply.Content) ? "model choice" : reply.Content.Trim());
            content = args.GetValueOrDefault("content");
            if (reply.ToolCalls.Count > 1) note = $"{reply.ToolCalls.Count} tool calls returned; first taken";
        }
        else if (_endpoint.NativeTools)
        {
            // No call came back: the model answered in prose. Accept a prose answer only if it names exactly one visible action.
            var named = context.Tools.Where(t => reply.Content.Contains(t.Id, StringComparison.Ordinal)).Select(t => t.Id).Distinct().ToList();
            tool = named.Count == 1 ? named[0] : null;
            why = Truncate(reply.Content);
            content = null;
            note = named.Count == 1 ? "prose answer, one action named" : "prose answer, no tool call";
        }
        else
        {
            (tool, why, content) = ParseJson(reply.Content);
            if (why.StartsWith("unparseable answer", StringComparison.Ordinal)) note = "unparseable answer";
        }

        // Valid means the model gave an answer the schedule can use: a visible action, or an explicit no action.
        // An empty or unparseable answer is not a decision, even though the agent treats it as "no action".
        var valid = !reply.Failed && note is not "unparseable answer" && note is not "prose answer, no tool call"
            && (tool is null || context.Tools.Any(t => string.Equals(t.Id, tool, StringComparison.Ordinal)));
        var refusedNote = valid ? null : tool is null ? null : $"proposed '{tool}', not visible: refused";
        if (tool is not null && !valid)
        {
            why = $"proposed unavailable action '{tool}': {why}";
            tool = null;
        }

        var needsPayload = tool is not null && context.Tools.Any(t => t.Id == tool && t.NeedsPayload);
        if (needsPayload && string.IsNullOrWhiteSpace(content))
        {
            why = $"{tool} needs a payload and none was supplied";
            tool = null;
            valid = false;
            refusedNote = "payload missing";
        }

        Telemetry.Add(reply, valid);
        _log.Write(CallRecord.Of(_endpoint, "policy", Protocol, $"{_contextTag} tick {context.Tick}", messages, tools?.Select(t => t.Name).ToList() ?? [], reply, valid, note ?? refusedNote));

        return new PolicySuggestion(tool, string.IsNullOrWhiteSpace(why) ? "model choice" : why, content);
    }

    /// <summary>
    /// A tool id as a function name the providers accept: OpenAI, DeepSeek and Azure allow only letters, digits, '_' and
    /// '-', up to 64 characters, so every other character becomes '_' (<c>drive:r01:j1</c> is offered as
    /// <c>drive_r01_j1</c>). Ollama takes any name, which is why ids with colons worked there first.
    /// </summary>
    public static string FunctionName(string id)
    {
        var name = new string(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return name.Length <= 64 ? name : name[..64];
    }

    /// <summary>The functions for the visible tools, and which tool id each function name stands for.</summary>
    private static (List<ToolSpec> Specs, Dictionary<string, string> Ids) BuildTools(IReadOnlyList<Tool> tools)
    {
        var specs = new List<ToolSpec>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in tools.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            // Two ids that differ only in a replaced character get a numbered name, so no call is ambiguous.
            var name = FunctionName(t.Id);
            for (var n = 2; ids.ContainsKey(name) || name == NoAction; n++) name = $"{FunctionName(t.Id)[..Math.Min(FunctionName(t.Id).Length, 60)]}_{n}";
            ids[name] = t.Id;
            var requires = t.Requires.Count == 0 ? "nothing" : string.Join(", ", t.Requires);
            var description = (name == t.Id ? "" : $"action {t.Id}; ") + $"requires [{requires}]; grants [{string.Join(", ", t.Grants)}]; kind {t.Kind}." + (t.NeedsPayload ? $" NEEDS PAYLOAD: {t.PayloadHint}" : "");
            var props = new JsonObject { ["why"] = new JsonObject { ["type"] = "string", ["description"] = "one short sentence" } };
            var required = new JsonArray();
            if (t.NeedsPayload)
            {
                props["content"] = new JsonObject { ["type"] = "string", ["description"] = t.PayloadHint ?? "the payload" };
                required.Add("content");
            }

            specs.Add(new ToolSpec(name, description, new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required }));
        }

        specs.Add(new ToolSpec(NoAction, "No listed action can make progress right now.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["why"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray() }));
        return (specs, ids);
    }

    private static string BuildUserPrompt(PolicyContext context, bool nativeTools, int trim = 0)
    {
        // trim 0: everything. 1: history to the last three lines. 2: notes to their first two lines. 3: history to one
        // line. 4: self-model to its first four lines.
        var history = trim >= 3 ? context.History.TakeLast(1).ToList() : trim >= 1 ? context.History.TakeLast(3).ToList() : context.History.ToList();
        var notes = trim >= 2 ? context.Notes.Take(2).ToList() : context.Notes.ToList();
        var selfModel = context.SelfModel is null ? null : trim >= 4 ? string.Join('\n', context.SelfModel.Split('\n').Take(4)) : context.SelfModel;

        var sb = new StringBuilder();
        sb.AppendLine($"GOAL: {context.GoalText}");
        sb.AppendLine($"TICK: {context.Tick} of {context.MaxTicks}. Freedom: {context.Freedom:F2} (how much improvisation you are allowed).");
        if (!string.IsNullOrWhiteSpace(context.Stage)) sb.AppendLine($"STAGE: {context.Stage}");
        if (!string.IsNullOrWhiteSpace(selfModel))
        {
            sb.AppendLine("SELF-MODEL (your own record, written by previous ticks):");
            foreach (var line in selfModel.Split('\n')) sb.AppendLine($"  {line.TrimEnd()}");
        }

        if (notes.Count > 0)
        {
            sb.AppendLine("NOTES FROM THE ARCHITECTURE (records it keeps for you: your biology, your record, where you stand; measured, not assumed):");
            foreach (var line in notes) sb.AppendLine($"  {line}");
        }

        sb.AppendLine("FACTS NOW (true right now):");
        sb.AppendLine(context.Facts.Count == 0 ? "  (none)" : "  " + string.Join(", ", context.Facts.Order(StringComparer.Ordinal)));
        if (!nativeTools)
        {
            sb.AppendLine("ACTIONS (id: requires -> grants):");
            foreach (var tool in context.Tools.OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                var requires = tool.Requires.Count == 0 ? "(nothing)" : string.Join(", ", tool.Requires);
                var payload = tool.NeedsPayload ? $" NEEDS PAYLOAD: {tool.PayloadHint}" : "";
                sb.AppendLine($"  - {tool.Id}: requires [{requires}] -> grants [{string.Join(", ", tool.Grants)}] ({tool.Kind}){payload}");
            }
        }
        else
        {
            sb.AppendLine("ACTIONS: the functions you can call. Call exactly one, or no_action.");
        }

        if (context.Preferred.Count > 0) sb.AppendLine($"HABIT: the agent usually reaches for {string.Join(", ", context.Preferred)} first. Prefer it unless it has failed.");
        sb.AppendLine(trim >= 1 && history.Count < context.History.Count ? $"HISTORY (this run, last {history.Count} of {context.History.Count}):" : "HISTORY (this run, oldest first):");
        if (history.Count == 0) sb.AppendLine("  (nothing yet)");
        foreach (var entry in history) sb.AppendLine($"  {entry}");
        sb.AppendLine(nativeTools ? "Choose the single best next action now." : "Choose the single best next action now. JSON only.");
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseArgs(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static (string? Tool, string Why, string? Content) ParseJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, $"unparseable answer: {Truncate(content)}", null);
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;
            var why = root.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "";
            var payload = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var tool = root.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.Equals(tool, "null", StringComparison.OrdinalIgnoreCase) || string.Equals(tool, NoAction, StringComparison.Ordinal) || tool == "") tool = null;
            return (tool, why, payload);
        }
        catch (JsonException)
        {
            return (null, $"unparseable answer: {Truncate(content)}", null);
        }
    }

    private static string Truncate(string s) => s.Length <= 160 ? s.Trim() : s[..160].Trim() + "…";
}
