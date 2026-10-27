using Creatura;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Creatura.Hosts.AgentFramework;

/// <summary>
/// The control layer as Agent Framework function-calling middleware. The rules live in
/// <see cref="ControlLayer"/> (Core, host-free); this file only maps the framework's hooks onto its verbs:
/// the tool name in → <see cref="ControlLayer.Admit"/> → terminate or feedback; <c>next()</c>; the result
/// string in → <see cref="ControlLayer.Judge"/> → terminate or feedback. The model only ever sees the string
/// this returns. When the host runs with extensions, their re-judge chain and observers run inside the
/// layer's judge exactly as the reference loop runs them (after the layer's own verdict, before its record);
/// with none loaded the call is the one it always was.
/// </summary>
public sealed class ControlLayerMiddleware(ControlLayer layer, ExtensionSet? extensions = null, Func<TickContext>? tickContext = null)
{
    public ControlLayer Layer => layer;

    public async ValueTask<object?> OnFunctionCallAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var admission = layer.Admit(context.Function.Name);
        if (!admission.Admitted)
        {
            context.Terminate = admission.Terminate;
            return admission.Feedback;
        }

        var result = await next(context, cancellationToken).ConfigureAwait(false);
        var judgement = Judge(admission.Tool!, new ActionReport(result?.ToString() ?? "", layer.LastOutcome));
        context.Terminate = judgement.Terminate;
        return judgement.Feedback;
    }

    private EffectJudgement Judge(Tool tool, ActionReport report)
    {
        if (extensions is null || extensions.Count == 0 || tickContext is null)
        {
            return layer.Judge(tool, report);
        }

        // What the extensions see is what the layer judges: the outcome the tool wrapper reported (a claim of
        // success when none was), and whether the promised effect is in the registry.
        var outcome = report.Outcome ?? new ActionOutcome(tool.Id, true, [], null);
        var landed = layer.World.EffectsLanded(tool);
        var tick = tickContext();
        return layer.Judge(tool, report,
            v => extensions.Judge(tick, tool, outcome, landed, v),
            v => extensions.AfterAction(tick, tool, outcome, v));
    }
}
