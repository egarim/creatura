using System.ComponentModel;
using Creatura;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.AgentFramework;

/// <summary>
/// Turns the world's tools into Agent Framework functions. Each function does one thing: it acts on the
/// world and returns what the world *reported*. Whether the effect actually landed is not the function's
/// business — the control layer checks that after the call, against the world's facts. The outcome itself
/// is handed to <paramref name="observe"/> so the layer can tell an honest failure from a lie.
/// </summary>
public static class WorldTools
{
    public static IList<AITool> For(IWorld world, Action<ActionOutcome>? observe = null) =>
        world.AllTools.Select(tool => (AITool)Create(world, tool, observe)).ToList();

    private static AIFunction Create(IWorld world, Tool tool, Action<ActionOutcome>? observe)
    {
        string Report(ActionOutcome outcome)
        {
            observe?.Invoke(outcome);
            return outcome.Observation ?? (outcome.Succeeded ? "ok" : "failed");
        }

        if (tool.NeedsPayload)
        {
            return AIFunctionFactory.Create(
                ([Description("The payload this tool writes, complete and verbatim.")] string content) =>
                    Report(world.Act(tool.Id, content)),
                tool.Id,
                Describe(tool));
        }

        return AIFunctionFactory.Create(() => Report(world.Act(tool.Id)), tool.Id, Describe(tool));
    }

    public static string Describe(Tool tool)
    {
        var requires = tool.Requires.Count == 0 ? "nothing" : string.Join(", ", tool.Requires);
        var grants = string.Join(", ", tool.Grants);
        var payload = tool.NeedsPayload ? $" Payload (content): {tool.PayloadHint ?? "required"}." : "";
        return $"Requires: {requires}. Grants: {grants}. Kind: {tool.Kind}.{payload}";
    }
}
