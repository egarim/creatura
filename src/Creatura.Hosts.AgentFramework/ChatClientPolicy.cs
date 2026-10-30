using System.Text;
using System.Text.Json;
using Creatura;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.AgentFramework;

/// <summary>
/// Any <see cref="IChatClient"/> in the loop's decision seat. The other direction from <see cref="ControlledAgent"/>:
/// there the framework owns the function-calling loop and the layer rides it as middleware; here
/// <see cref="ReferenceLoop"/> owns the loop and the chat client answers one question per tick. The visible tools go
/// out as declarations only (nothing here can run them); the first function call in the reply is the suggestion, a
/// hidden tool named is refused rather than obeyed, and a reply with no call is no action. Model seats without a
/// package live in <c>Creatura.Seats</c>; this is the path for the Microsoft ecosystem's clients.
/// </summary>
public sealed class ChatClientPolicy(IChatClient client, string name = "chat-client") : IPolicy
{
    public const string Instructions =
        """
        You are the decision component of an operations agent. Each turn, call exactly one of the offered tools,
        or call none when no offered tool can make progress. Each tool says what it requires and what it grants.
        The system checks every effect against the facts; never claim the goal is done.
        """;

    public string Name => name;

    public async Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions
        {
            Instructions = Instructions,
            Tools = context.Tools.Select(Declare).ToList(),
            AllowMultipleToolCalls = false,
        };
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, Render(context))], options, cancellationToken)
            .ConfigureAwait(false);

        var text = OneLine(response.Text);
        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        if (call is null)
        {
            return new PolicySuggestion(null, text.Length == 0 ? "no tool call" : text);
        }

        if (!context.Tools.Any(t => t.Id == call.Name))
        {
            return new PolicySuggestion(null, $"refused: {call.Name} is not a visible tool");
        }

        var content = call.Arguments is not null && call.Arguments.TryGetValue("content", out var value) ? value?.ToString() : null;
        return new PolicySuggestion(call.Name, text.Length == 0 ? "function call" : text, content);
    }

    /// <summary>The tick as the seat reads it: goal, facts, freedom, the self-model and stage when there is one, history and notes.</summary>
    public static string Render(PolicyContext context)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Goal (checked by the system, not by you): {context.GoalText}");
        prompt.AppendLine($"Facts that hold now: {(context.Facts.Count == 0 ? "none" : string.Join(", ", context.Facts.Order()))}");
        prompt.AppendLine(string.Create(TraceText.Culture, $"Tick {context.Tick} of {context.MaxTicks}; freedom {context.Freedom:F2}."));
        if (context.SelfModel is { Length: > 0 } self) prompt.AppendLine().AppendLine("Your record:").AppendLine(self);
        if (context.Stage is { Length: > 0 } stage) prompt.AppendLine($"Stage: {stage}");
        if (context.History.Count > 0) prompt.AppendLine().AppendLine("So far:").AppendLine(string.Join(Environment.NewLine, context.History.Select(h => "- " + h)));
        if (context.Notes.Count > 0) prompt.AppendLine().AppendLine("Notes to read before acting:").AppendLine(string.Join(Environment.NewLine, context.Notes.Select(n => "- " + n)));
        return prompt.ToString().TrimEnd();
    }

    private static AITool Declare(Tool tool)
    {
        var schema = tool.NeedsPayload
            ? """{"type":"object","properties":{"content":{"type":"string","description":"The payload this tool writes, complete and verbatim."}},"required":["content"]}"""
            : """{"type":"object","properties":{}}""";
        return AIFunctionFactory.CreateDeclaration(tool.Id, WorldTools.Describe(tool), JsonDocument.Parse(schema).RootElement);
    }

    private static string OneLine(string? text)
    {
        var line = (text ?? "").Trim().Split('\n', 2)[0].Trim();
        return line.Length <= 160 ? line : line[..160];
    }
}
