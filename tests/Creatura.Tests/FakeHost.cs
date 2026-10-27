namespace Creatura.Tests;

/// <summary>What the fake model is handed at one call: the round, the call within it (one call is one tick), the prompt and
/// the feedback so far, the tools offered after visibility, and the facts as the prompt states them.</summary>
public sealed record FakeModelCall(int Round, int Call, int Tick, string Prompt, IReadOnlyList<string> Feedback, IReadOnlyList<Tool> Offered, IReadOnlySet<string> Facts);

/// <summary>What the fake model answers: one tool call (with its payload, when the tool needs one) or text and no call.</summary>
public sealed record FakeModelReply(string? Tool, string Text = "", string? Content = null)
{
    public static FakeModelReply Call(string tool, string? content = null) => new(tool, "", content);

    public static FakeModelReply Say(string text) => new(null, text);
}

/// <summary>
/// A host with no framework behind it: an in-process function-calling loop driven by a delegate that plays the model,
/// handing the control layer exactly the verbs the Agent Framework host hands it, in the same order. Per round:
/// <c>BeginRound</c>, <c>Stop</c>, <c>Assemble</c>, then model calls until one answers with text or a verb says terminate;
/// per model call: <c>BeginTick</c> and <c>Offer</c> (what the host's chat-client filter does before the request leaves);
/// per tool call: <c>Admit</c>, the world's <c>Act</c> with <c>ReportOutcome</c> (the tool wrapper), <c>Judge</c> (the
/// function middleware); per reply: <c>Close</c>; at the end, <c>Sleep</c>. It references no <c>Microsoft.Agents.AI</c>
/// and no <c>Microsoft.Extensions.AI</c>: if it reaches the host's goldens row for row, the verbs carry the layer and the
/// framework only carries the calls.
///
/// What the framework does that this mirrors without its code: the function-calling loop calls the model again after
/// every judged call unless a verb set terminate; a model that names a function the framework does not know (not in the
/// world at all) gets a "not found" result without any verb being called; the loop gives up after
/// <see cref="MaximumCallsPerRound"/> calls in one round (the framework's default iteration cap; no scenario here reaches it).
/// </summary>
public sealed class FakeHost(
    Func<IWorld, AgentTask, Func<FakeModelCall, FakeModelReply>> modelFactory,
    string policyName,
    AgentTask? task = null,
    string name = "fake host",
    string framework = "none (in-process)") : IAgent
{
    public const int MaximumCallsPerRound = 40;

    /// <summary>The host's own instruction to the model when nothing it is offered helps; <see cref="ControlLayer.Close"/> reads it as no route.</summary>
    public const string NoPath = "NO PATH";

    private readonly AgentTask _task = task ?? RestoreServiceDomain.Task;

    public string Name => name;

    public Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default)
    {
        var self = new SelfModel { Role = "controlled operator", Principal = "joche", StageLevel = 3 };
        self.OpenCommitments.Add(_task.Name);
        var trace = new List<TraceEvent>();
        var notes = new List<string>();
        var control = new ControlLayer(world, _task, self, ControlLayerOptions.Host, trace: trace, notes: notes);
        control.Note($"policy {policyName}; framework {framework}");

        var model = modelFactory(world, _task);
        var goalReached = false;
        const int maxRounds = 6;

        for (var round = 1; round <= maxRounds && control.Tick < maxTicks; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            control.BeginRound();
            if (control.Stop() is not null) break;

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

            string text;
            try
            {
                text = Round(world, control, model, round, prompt).Trim();
            }
            catch (Exception exception)
            {
                // The model call itself failed: that is the host's to report, not the layer's.
                control.Record("organism", "contract", 1.0 - control.Progress, TraceText.ModelError, TraceKind.Escalated,
                    $"{exception.GetType().Name}: {exception.Message}");
                control.Note("escalated: the model call failed; the goal is unregulatable from here");
                break;
            }

            var closure = control.Close(text, noRoute: text.Contains(NoPath, StringComparison.OrdinalIgnoreCase));
            goalReached = closure.GoalReached;
            if (!closure.Continue) break;
        }

        control.Sleep();
        return Task.FromResult(new AgentRun(Name, goalReached, control.Tick, control.Trace, control.Notes));
    }

    /// <summary>One round's function-calling loop. Returns the reply's text: the model's words, or empty when a verb ended the round.</summary>
    private static string Round(IWorld world, ControlLayer control, Func<FakeModelCall, FakeModelReply> model, int round, string prompt)
    {
        var feedback = new List<string>();
        for (var call = 1; call <= MaximumCallsPerRound; call++)
        {
            control.BeginTick();
            var offered = control.Offer();
            var reply = model(new FakeModelCall(round, call, control.Tick, prompt, feedback.ToList(), offered, world.Facts.ToHashSet(StringComparer.Ordinal)));
            if (reply.Tool is not { } toolId) return reply.Text;

            if (world.Tool(toolId) is null)
            {
                feedback.Add($"Error: function {toolId} not found.");
                continue;
            }

            var admission = control.Admit(toolId);
            if (!admission.Admitted)
            {
                feedback.Add(admission.Feedback ?? "");
                if (admission.Terminate) return "";
                continue;
            }

            var tool = admission.Tool!;
            var outcome = tool.NeedsPayload ? world.Act(tool.Id, reply.Content) : world.Act(tool.Id);
            control.ReportOutcome(outcome);
            var reported = outcome.Observation ?? (outcome.Succeeded ? "ok" : "failed");

            var judgement = control.Judge(tool, new ActionReport(reported, control.LastOutcome));
            feedback.Add(judgement.Feedback);
            if (judgement.Terminate) return "";
        }

        return "";
    }
}
