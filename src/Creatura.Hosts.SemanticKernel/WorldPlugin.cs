using System.ComponentModel;
using System.Text.RegularExpressions;
using Creatura;
using Microsoft.SemanticKernel;

namespace Creatura.Hosts.SemanticKernel;

/// <summary>
/// The world's tools as one Semantic Kernel plugin. Each function does one thing: it acts on the world and returns what
/// the world *reported*. Whether the effect actually landed is not the function's business; the control layer checks
/// that after the call, against the world's facts. The outcome itself is handed to <c>observe</c> so the layer can tell
/// an honest failure from a lie.
///
/// Kernel function names allow only letters, digits and underscores, so a tool id like <c>apply-patch</c> is the
/// function <c>apply_patch</c>; the model sees it under the plugin's name as well (<c>world_apply_patch</c> on the chat
/// client path). <see cref="ToolId"/> reads any of those spellings back. Two tools that would share a name are refused
/// when the plugin is built, never resolved by guessing.
/// </summary>
public sealed partial class WorldPlugin
{
    public const string PluginName = "world";

    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    public WorldPlugin(IWorld world, Action<ActionOutcome>? observe = null)
    {
        var functions = new List<KernelFunction>();
        foreach (var tool in world.AllTools)
        {
            var name = FunctionName(tool.Id);
            if (_ids.TryGetValue(name, out var other))
            {
                throw new ArgumentException($"tools {other} and {tool.Id} are both the kernel function {name}; rename one", nameof(world));
            }

            _ids[name] = tool.Id;
            functions.Add(Create(world, tool, name, observe));
        }

        Plugin = KernelPluginFactory.CreateFromFunctions(PluginName, functions);
    }

    public KernelPlugin Plugin { get; }

    /// <summary>The tool behind a function name as SK or the model spells it (bare, or after the plugin's name and either separator); null when it is no world tool.</summary>
    public string? ToolId(string name)
    {
        foreach (var prefix in (string[])[PluginName + "_", PluginName + "-", ""])
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && _ids.TryGetValue(name[prefix.Length..], out var id))
            {
                return id;
            }
        }

        return null;
    }

    public static string FunctionName(string toolId) => NotAllowed().Replace(toolId, "_");

    private static KernelFunction Create(IWorld world, Tool tool, string name, Action<ActionOutcome>? observe)
    {
        string Report(ActionOutcome outcome)
        {
            observe?.Invoke(outcome);
            return outcome.Observation ?? (outcome.Succeeded ? "ok" : "failed");
        }

        if (tool.NeedsPayload)
        {
            return KernelFunctionFactory.CreateFromMethod(
                ([Description("The payload this tool writes, complete and verbatim.")] string content) => Report(world.Act(tool.Id, content)),
                name,
                Describe(tool));
        }

        return KernelFunctionFactory.CreateFromMethod(() => Report(world.Act(tool.Id)), name, Describe(tool));
    }

    /// <summary>The description the model reads; the same words the Agent Framework host gives a tool.</summary>
    public static string Describe(Tool tool)
    {
        var requires = tool.Requires.Count == 0 ? "nothing" : string.Join(", ", tool.Requires);
        var grants = string.Join(", ", tool.Grants);
        var payload = tool.NeedsPayload ? $" Payload (content): {tool.PayloadHint ?? "required"}." : "";
        return $"Requires: {requires}. Grants: {grants}. Kind: {tool.Kind}.{payload}";
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotAllowed();
}
