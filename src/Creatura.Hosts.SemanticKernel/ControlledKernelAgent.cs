using Creatura;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Creatura.Hosts.SemanticKernel;

/// <summary>
/// A Semantic Kernel agent wrapped in the control layer. The kernel owns the function-calling loop
/// (<c>UseKernelFunctionInvocation</c>, which runs the kernel's filters) and the chat history; the control layer
/// (<see cref="ControlLayer"/> in Core) owns what the model sees (<see cref="OfferingChatClient"/>), what counts as
/// success and when to stop (<see cref="ControlLayerFilter"/>). One tick is one model request, as on the Agent
/// Framework host, so the two hosts' runs can be compared row for row. The model comes in as an
/// <see cref="IChatClient"/>, the abstraction Semantic Kernel's own connectors now hand out. Extensions ride the host
/// through the hooks it can honour: <c>BeforeRun</c>, the re-judge chain and the observers after each judged call,
/// <c>PolicyNotes</c> in the per-round prompt, and <c>Sleep</c>; the slots that shape the option space, size the
/// budget, answer "no route" and veto hand-offs stay the reference loop's. With no options loaded the run is the one
/// it always was.
/// </summary>
public sealed class ControlledKernelAgent(Func<IWorld, AgentTask, IChatClient> clientFactory, string policyName, AgentTask? task = null, AgentOptions? options = null)
    : IAgent
{
    public const string Framework = "Microsoft.SemanticKernel";

    private readonly AgentTask _task = task ?? RestoreServiceDomain.Task;
    private readonly AgentOptions _options = options ?? AgentOptions.None;

    public string Name => "Controlled (SK)";

    /// <summary>The extensions this host runs with; none by default.</summary>
    public AgentOptions Options => _options;

    /// <summary>Cumulative model usage across runs: requests made, and the tokens the client reported.</summary>
    public int Calls { get; private set; }

    public int PromptTokens { get; private set; }

    public int CompletionTokens { get; private set; }

    /// <summary>The kernel and history of the last run, for a test or a sample to look into; null before the first.</summary>
    public Kernel? LastKernel { get; private set; }

    public ChatHistory? LastHistory { get; private set; }

    /// <summary>Offline: breadth-first search in the model's seat.</summary>
    public static ControlledKernelAgent Offline(AgentTask? task = null, AgentOptions? options = null) =>
        new((world, t) => new PlannerChatClient(world, t), "bfs", task, options);

    /// <summary>The same words the Agent Framework host gives the model.</summary>
    public const string Instructions =
        """
        You are the decision component of an operations agent. You act only through the tools offered to you.
        Call one tool at a time. Each tool says what it requires and what it grants.
        After every call you will be told whether the effect was verified. Trust that verdict, not the tool's
        own report. If a tool is refused or its effect did not land, take a different route.
        Never claim the goal is done; the system checks that itself. When no offered tool can make progress,
        reply exactly: NO PATH.
        """;

    public async Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var self = new SelfModel { Role = "controlled operator", Principal = "joche", StageLevel = 3 };
        self.OpenCommitments.Add(_task.Name);
        var trace = new List<TraceEvent>();
        var notes = new List<string>();
        var control = new ControlLayer(world, _task, self, ControlLayerOptions.Host, trace: trace, notes: notes);
        control.Note($"policy {policyName}; framework {Framework}");
        var ext = new ExtensionSet(_options);

        // The hook context the extensions see: the layer's tick at the moment of the hook (the model request being made).
        TickContext Context() => new()
        {
            Agent = Name, World = world, Task = _task, Tick = control.Tick, MaxTicks = maxTicks, Notes = notes, Trace = trace,
            SelfModel = self, Predictions = control.Predictions,
        };
        ext.BeforeRun(Context());

        var plugin = new WorldPlugin(world, control.ReportOutcome);
        var filter = new ControlLayerFilter(control, plugin, ext, Context);
        var chat = new OfferingChatClient(clientFactory(world, _task), control, plugin, Count)
            .AsBuilder()
            .UseKernelFunctionInvocation()
            .Build()
            .AsChatCompletionService();

        var kernel = Kernel.CreateBuilder().Build();
        kernel.Plugins.Add(plugin.Plugin);
        kernel.AutoFunctionInvocationFilters.Add(filter);
        var settings = new PromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        var history = new ChatHistory(Instructions);
        LastKernel = kernel;
        LastHistory = history;

        var goalReached = false;
        const int maxRounds = 6;

        for (var round = 1; round <= maxRounds && control.Tick < maxTicks; round++)
        {
            control.BeginRound();

            // Stuck regulation: rounds that act without moving the gap are handed to a human (Bach 01:56:31).
            if (control.Stop() is not null) break;

            // The self-model is read before acting: it is part of the message, every round.
            var record = control.Assemble(round);
            var prompt =
                $"""
                Goal (checked by the system, not by you): {_task.GoalText}
                Facts that hold now: {(world.Facts.Count == 0 ? "none" : string.Join(", ", world.Facts.Order()))}
                Progress toward the goal: {control.Progress:P0}.
                Round {round} of {maxRounds}; freedom {control.Freedom:F2}; failures tolerated this round: {control.Budget}.

                Your record (read it before acting):
                {record}

                Make progress with the offered tools.
                """;

            // What the extensions want the seat to read this round, after the record; nothing when none says anything.
            var policyNotes = ext.PolicyNotes(Context());
            if (policyNotes.Count > 0)
            {
                prompt += Environment.NewLine + Environment.NewLine + "Notes to read before acting:" + Environment.NewLine +
                    string.Join(Environment.NewLine, policyNotes.Select(n => "- " + n));
            }

            history.AddUserMessage(prompt);
            filter.BeginRound();
            string text;
            try
            {
                var replies = await chat.GetChatMessageContentsAsync(history, settings, kernel, cancellationToken).ConfigureAwait(false);
                // A verb that ended the kernel's loop leaves the function's result last, not a reply: no text, as on the other host.
                text = filter.Terminated ? "" : string.Concat(replies.Select(r => r.Content)).Trim();
                if (!filter.Terminated) history.AddRange(replies);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The model call itself failed: that is the host's to report, not the layer's.
                control.Record("organism", "contract", 1.0 - control.Progress, TraceText.ModelError, TraceKind.Escalated,
                    $"{exception.GetType().Name}: {exception.Message}");
                control.Note("escalated: the model call failed; the goal is unregulatable from here");
                break;
            }

            // Success is a predicate over the world, never the model's text; the layer reads it, then the
            // escalations, then the reply. "NO PATH" is this host's instruction to the model (see Instructions).
            var closure = control.Close(text, noRoute: text.Contains("NO PATH", StringComparison.OrdinalIgnoreCase));
            goalReached = closure.GoalReached;
            if (!closure.Continue) break;
        }

        // The host's consolidation, then every extension's (one may stand in for the host's, as in the loop).
        ext.Sleep(new SleepContext
        {
            Agent = Name, World = world, Task = _task, LastTick = control.Tick, GoalReached = goalReached, Notes = notes, Trace = trace,
            SelfModel = self, Audit = control.Audit, Predictions = control.Predictions,
        }, control.Sleep);

        return new AgentRun(Name, goalReached, control.Tick, control.Trace, control.Notes);
    }

    private void Count(UsageDetails? usage)
    {
        Calls++;
        PromptTokens += (int)(usage?.InputTokenCount ?? 0);
        CompletionTokens += (int)(usage?.OutputTokenCount ?? 0);
    }
}
