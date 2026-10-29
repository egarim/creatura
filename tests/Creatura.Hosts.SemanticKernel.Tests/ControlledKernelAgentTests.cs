using Creatura;
using Creatura.Hotz;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Creatura.Hosts.SemanticKernel.Tests;

/// <summary>
/// The control layer on Semantic Kernel. The kernel owns the function-calling loop; these tests check what the filter and
/// the offering client around it do when the world and the tool's report disagree: the model is told the layer's verdict
/// instead of the claim, a distrusted function is neither offered nor run, and a verb's terminate ends the kernel's whole
/// loop, not just the call.
/// </summary>
public class ControlledKernelAgentTests
{
    /// <summary>The function results the model read, in order, from the kernel's chat history.</summary>
    private static List<string> Results(ChatHistory history) =>
        history.Where(m => m.Role == AuthorRole.Tool)
            .SelectMany(m => m.Items.OfType<Microsoft.SemanticKernel.FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? "")
            .ToList();

    [Fact]
    public async Task ALieReachesTheModelAsTheLayersWarningNotTheToolsClaim()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");
        var agent = ControlledKernelAgent.Offline();

        var run = await agent.RunAsync(world, 30);

        Assert.True(run.GoalReached);
        var results = Results(agent.LastHistory!);
        var warning = Assert.Single(results, r => r.StartsWith("WARNING: apply-patch reported", StringComparison.Ordinal));
        Assert.Contains("patch.applied did not become true", warning, StringComparison.Ordinal);
        Assert.Contains("distrusted for now; use a different route", warning, StringComparison.Ordinal);
        // Every other result the model read carries the layer's verification, never a bare report.
        Assert.All(results.Where(r => r != warning), r => Assert.Contains("[verified: ", r, StringComparison.Ordinal));
        Assert.Contains(run.Trace, e => e.Action == "apply-patch" && e.Kind == TraceKind.Blocked);
        Assert.Contains(run.Trace, e => e.Action == "apply-config" && e.Kind == TraceKind.Acted);
    }

    [Fact]
    public async Task ADistrustedFunctionIsNotOfferedAndNamedFromMemoryIsRefusedNotRun()
    {
        var toy = RestoreServiceDomain.NewWorld();
        toy.MakeLiar("apply-patch");
        var world = new Counting(toy);
        var model = new Scripted("world_inspect_logs", "world_identify_component", "world_apply_patch", "world_apply_patch", "world_launch_rockets");
        var agent = new ControlledKernelAgent((_, _) => model, "scripted");

        var run = await agent.RunAsync(world, 30);

        // After the lie the function's schema is gone from the next request...
        Assert.Contains("world_apply_patch", model.Offered[2]);
        Assert.DoesNotContain("world_apply_patch", model.Offered[3]);
        // ...and the model naming it anyway is refused by the layer before the function runs.
        var results = Results(agent.LastHistory!);
        Assert.StartsWith("WARNING: apply-patch reported", results[2], StringComparison.Ordinal);
        Assert.StartsWith("REFUSED: apply-patch is not available (distrusted after a failed effect", results[3], StringComparison.Ordinal);
        Assert.Equal(1, world.Calls("apply-patch"));
        // A name that is no function at all is Semantic Kernel's to answer, before any verb; the layer never sees it.
        Assert.Contains("not found", results[4], StringComparison.Ordinal);
        Assert.DoesNotContain(run.Trace, e => e.Action.Contains("rockets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnHonestFailureIsNotCalledALieAndTheToolStaysOffered()
    {
        // apply-patch before the component is identified: the world says it failed, and nothing landed. The tool wrapper
        // hands that outcome to the layer, so this is an honest failure, not a lie.
        var model = new Scripted("world_apply_patch", "world_inspect_logs");
        var agent = new ControlledKernelAgent((_, _) => model, "scripted");

        var run = await agent.RunAsync(RestoreServiceDomain.NewWorld(), 30);

        var first = Results(agent.LastHistory!)[0];
        Assert.Contains("[not verified: patch.applied still false; the tool reported failure]", first, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", first, StringComparison.Ordinal);
        Assert.Contains("world_apply_patch", model.Offered[1]);
        Assert.Contains(run.Trace, e => e.Action == "apply-patch" && e.Detail.StartsWith("failed honestly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TerminateEndsTheKernelsLoopAndTheRunHandsOff()
    {
        // A model that keeps naming the function it was told is distrusted: refusals spend the budget until a verb terminates.
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");
        var model = new Scripted(Enumerable.Repeat("world_apply_patch", 50).ToArray());
        var agent = new ControlledKernelAgent((_, _) => model, "scripted");

        var run = await agent.RunAsync(world, 30);

        Assert.False(run.GoalReached);
        Assert.Contains("escalated: failures exceeded the freedom budget", run.Notes);
        Assert.Contains(run.Trace, e => e.Kind == TraceKind.Escalated);
        // One request per tick, and none after the terminating call: the kernel did not ask the model again.
        Assert.Equal(run.Ticks, model.Offered.Count);
        Assert.Equal(model.Offered.Count, agent.Calls);
        Assert.True(run.Ticks < 6, $"{run.Ticks} ticks");
        Assert.DoesNotContain(agent.LastHistory!, m => m.Role == AuthorRole.Assistant && !string.IsNullOrEmpty(m.Content));
    }

    [Fact]
    public async Task ARevokedToolIsNeverOfferedAndTheRunRoutesAround()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.Revoke("apply-patch");
        var seen = new List<IReadOnlyList<string>>();
        var agent = new ControlledKernelAgent((w, t) => new Watching(new PlannerChatClient(w, t), seen), "bfs");

        var run = await agent.RunAsync(world, 30);

        Assert.True(run.GoalReached);
        Assert.NotEmpty(seen);
        Assert.All(seen, offered => Assert.DoesNotContain("world_apply_patch", offered));
        Assert.DoesNotContain(run.Trace, e => e.Action == "apply-patch");
    }

    [Fact]
    public void ToolIdsBecomeKernelFunctionNamesAndBack()
    {
        var plugin = new WorldPlugin(RestoreServiceDomain.NewWorld());

        Assert.Contains(plugin.Plugin, f => f.Name == "apply_patch");
        Assert.Equal("apply-patch", plugin.ToolId("apply_patch"));
        Assert.Equal("apply-patch", plugin.ToolId("world_apply_patch"));
        Assert.Equal("apply-patch", plugin.ToolId("world-apply_patch"));
        Assert.Null(plugin.ToolId("world_launch_rockets"));
        Assert.Equal("drive_A_B", WorldPlugin.FunctionName("drive:A-B"));

        var clash = new World([new Tool("a-b", [], ["x"], "read"), new Tool("a_b", [], ["y"], "read")], []);
        var e = Assert.Throws<ArgumentException>(() => new WorldPlugin(clash));
        Assert.Contains("a-b and a_b are both the kernel function a_b", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACriticLoadedAsAnOptionReadsARouteAndLeavesTheRunUnchanged()
    {
        var alone = ControlledKernelGoldenTests.Toy("patchlies");
        var reference = await ControlledKernelAgent.Offline().RunAsync(alone, 30);

        var citizen = new HotzAgent(new OwnerToken(), RestoreServiceDomain.Task, ["alert.resolved"]);
        var world = ControlledKernelGoldenTests.Toy("patchlies");
        var run = await ControlledKernelAgent.Offline(options: AgentOptions.With(new CriticExtension(citizen))).RunAsync(world, 30);
        var route = citizen.Close(run, world);

        Assert.Equal(reference.Trace, run.Trace);
        Assert.Equal(reference.Notes, run.Notes);
        var flag = Assert.Single(route.Judgements, j => j.Shown && j.IsFlag);
        Assert.Equal("apply-patch", flag.Tool);
        Assert.Equal(["patch.applied"], flag.Missing);
        Assert.Equal(1, citizen.Report().LiesCaught);
    }

    [Fact]
    public async Task PolicyNotesReachThePerRoundPromptOnlyWhenAnExtensionWritesThem()
    {
        var quiet = ControlledKernelAgent.Offline();
        await quiet.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        Assert.DoesNotContain(quiet.LastHistory!, m => m.Role == AuthorRole.User && m.Content!.Contains("Notes to read before acting", StringComparison.Ordinal));

        var noted = ControlledKernelAgent.Offline(options: AgentOptions.With(new Noting()));
        await noted.RunAsync(RestoreServiceDomain.NewWorld(), 30);
        var prompts = noted.LastHistory!.Where(m => m.Role == AuthorRole.User).ToList();
        Assert.NotEmpty(prompts);
        Assert.All(prompts, p => Assert.Contains("Notes to read before acting:" + Environment.NewLine + "- read the registry, not the report", p.Content, StringComparison.Ordinal));
    }

    /// <summary>An extension with one standing note for the seat and nothing else.</summary>
    private sealed class Noting : IAgentExtension
    {
        public string Name => "noting";

        public IEnumerable<string> PolicyNotes(TickContext context) => ["read the registry, not the report"];
    }

    /// <summary>Calls the named functions in order, one per request, then says NO PATH; remembers what each request offered.</summary>
    private sealed class Scripted(params string[] calls) : IChatClient
    {
        public List<IReadOnlyList<string>> Offered { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Offered.Add((options?.Tools ?? []).Select(t => t.Name).ToList());
            var n = Offered.Count - 1;
            var reply = n < calls.Length
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{n}", calls[n], new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "NO PATH");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>The world, counting how often each tool actually ran.</summary>
    private sealed class Counting(World inner) : IWorld
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public int Calls(string tool) => _calls.GetValueOrDefault(tool);

        public IReadOnlySet<string> Facts => inner.Facts;

        public IReadOnlyList<Tool> AvailableTools => inner.AvailableTools;

        public IReadOnlyList<Tool> AllTools => inner.AllTools;

        public int Ticks => inner.Ticks;

        public bool Has(string fact) => inner.Has(fact);

        public Tool? Tool(string id) => inner.Tool(id);

        public bool EffectsLanded(Tool tool) => inner.EffectsLanded(tool);

        public ActionOutcome Act(string toolId, string? payload = null)
        {
            _calls[toolId] = Calls(toolId) + 1;
            return inner.Act(toolId, payload);
        }
    }

    /// <summary>Records the tool names of every request before the inner client answers it.</summary>
    private sealed class Watching(IChatClient inner, List<IReadOnlyList<string>> seen) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            seen.Add((options?.Tools ?? []).Select(t => t.Name).ToList());
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}
