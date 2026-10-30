using Creatura;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.AgentFramework;

/// <summary>
/// A Microsoft Agent Framework agent wrapped in the control layer. The framework owns the model call and
/// the function-calling loop; the control layer (<see cref="ControlLayer"/> in Core, reached through
/// <see cref="ControlLayerMiddleware"/>) owns what the model sees, what counts as success, and when to stop.
/// One tick is one model call: that stays a host decision. The result has the same shape as a run of
/// <see cref="ReferenceLoop"/>, so a sample can print them side by side. Extensions (<see cref="AgentOptions"/>)
/// ride the host through the hooks it can honour: <c>BeforeRun</c>, the re-judge chain and the observers after
/// each judged call, <c>PolicyNotes</c> in the per-round prompt, and <c>Sleep</c>. The framework keeps the slots
/// that shape the option space, size the budget, answer "no route" and veto hand-offs; those stay the loop's.
/// With no options loaded the run is the one it always was.
/// </summary>
public sealed class ControlledAgent : IAgent
{
    private readonly Func<IWorld, AgentTask, IChatClient> _clientFactory;
    private readonly AgentTask _task;
    private readonly string _policyName;
    private readonly AgentOptions _options;
    private readonly ExtensionSet _ext;

    public ControlledAgent(Func<IWorld, AgentTask, IChatClient> clientFactory, string policyName, AgentTask? task = null, AgentOptions? options = null)
    {
        _clientFactory = clientFactory;
        _policyName = policyName;
        _task = task ?? RestoreServiceDomain.Task;
        _options = options ?? AgentOptions.None;
        _ext = new ExtensionSet(_options);
    }

    public string Name => "Controlled (AF)";

    /// <summary>The extensions this host runs with; none by default.</summary>
    public AgentOptions Options => _options;

    /// <summary>Cumulative model usage across runs, for the benchmark scoreboard.</summary>
    public int Calls { get; private set; }

    public int PromptTokens { get; private set; }

    public int CompletionTokens { get; private set; }

    /// <summary>The same agent pointed at another task. Usage counters are shared so the benchmark can take deltas.</summary>
    public ControlledAgent WithTask(AgentTask task)
    {
        var copy = new ControlledAgent(_clientFactory, _policyName, task, _options) { _shared = _shared ?? this };
        return copy;
    }

    private ControlledAgent? _shared;

    /// <summary>Offline: breadth-first search in the model's seat.</summary>
    public static ControlledAgent Offline(AgentTask? task = null, AgentOptions? options = null) =>
        new((world, t) => new PlannerChatClient(world, t), "bfs", task, options);

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
        control.Note($"policy {_policyName}; framework Microsoft.Agents.AI");

        // The hook context the extensions see: the layer's tick at the moment of the hook (the model call being made).
        TickContext Context() => new()
        {
            Agent = Name, World = world, Task = _task, Tick = control.Tick, MaxTicks = maxTicks, Notes = notes, Trace = trace,
            SelfModel = self, Predictions = control.Predictions,
        };
        _ext.BeforeRun(Context());

        IChatClient client = new ToolFilteringChatClient(_clientFactory(world, _task), control);
        AIAgent agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Name = Name,
                ChatOptions = new ChatOptions { Instructions = Instructions, Tools = WorldTools.For(world, control.ReportOutcome) },
            })
            .AsBuilder()
            .Use(new ControlLayerMiddleware(control, _ext, Context).OnFunctionCallAsync)
            .Build();

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

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
                string.Create(TraceText.Culture, $"""
                Goal (checked by the system, not by you): {_task.GoalText}
                Facts that hold now: {(world.Facts.Count == 0 ? "none" : string.Join(", ", world.Facts.Order()))}
                Progress toward the goal: {control.Progress:P0}.
                Round {round} of {maxRounds}; freedom {control.Freedom:F2}; failures tolerated this round: {control.Budget}.

                Your record (read it before acting):
                {record}

                Make progress with the offered tools.
                """);

            // What the extensions want the seat to read this round, after the record; nothing when none says anything.
            var policyNotes = _ext.PolicyNotes(Context());
            if (policyNotes.Count > 0)
            {
                prompt += Environment.NewLine + Environment.NewLine + "Notes to read before acting:" + Environment.NewLine +
                    string.Join(Environment.NewLine, policyNotes.Select(n => "- " + n));
            }

            AgentResponse response;
            try
            {
                response = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken).ConfigureAwait(false);
                Count((int)(response.Usage?.InputTokenCount ?? 0), (int)(response.Usage?.OutputTokenCount ?? 0), 0);
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
            var text = response.Text?.Trim() ?? "";
            var closure = control.Close(text, noRoute: text.Contains("NO PATH", StringComparison.OrdinalIgnoreCase));
            goalReached = closure.GoalReached;
            if (!closure.Continue) break;
        }

        // The host's consolidation, then every extension's (one may stand in for the host's, as in the loop).
        _ext.Sleep(new SleepContext
        {
            Agent = Name, World = world, Task = _task, LastTick = control.Tick, GoalReached = goalReached, Notes = notes, Trace = trace,
            SelfModel = self, Audit = control.Audit, Predictions = control.Predictions,
        }, control.Sleep);

        Count(0, 0, control.Tick);
        return new AgentRun(Name, goalReached, control.Tick, control.Trace, control.Notes);
    }

    private void Count(int prompt, int completion, int calls)
    {
        var target = _shared ?? this;
        target.PromptTokens += prompt;
        target.CompletionTokens += completion;
        target.Calls += calls;
    }
}
