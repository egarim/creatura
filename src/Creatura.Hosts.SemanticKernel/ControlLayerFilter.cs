using Creatura;
using Microsoft.SemanticKernel;

namespace Creatura.Hosts.SemanticKernel;

/// <summary>
/// The control layer as a Semantic Kernel auto-function-invocation filter. The rules live in <see cref="ControlLayer"/>
/// (Core, host-free); this file only maps the filter onto its verbs: the function's tool in → <see cref="ControlLayer.Admit"/>
/// → terminate or feedback; <c>next</c>; the function's result in → <see cref="ControlLayer.Judge"/> → terminate or feedback.
/// The model only ever sees the result this leaves: the layer's feedback replaces whatever the tool said, so a tool that
/// reports success while its effect did not land reaches the model as a warning, not as the claim.
///
/// The reserved design split this between an <see cref="IFunctionInvocationFilter"/> (admit and judge) and this filter
/// (terminate). It is one filter because <c>Terminate</c> exists only here, and two filters would have had to hand one
/// call's verdict from one to the other; this one also runs only for calls the model made, which are the calls the
/// layer is there to judge. When the host runs with extensions, their re-judge chain and observers run inside the
/// layer's judge exactly as the reference loop runs them; with none loaded the call is the one it always was.
/// </summary>
public sealed class ControlLayerFilter(ControlLayer layer, WorldPlugin plugin, ExtensionSet? extensions = null, Func<TickContext>? tickContext = null)
    : IAutoFunctionInvocationFilter
{
    public ControlLayer Layer => layer;

    /// <summary>Whether a verb ended Semantic Kernel's loop since the last <see cref="BeginRound"/>; the round's reply is then no text.</summary>
    public bool Terminated { get; private set; }

    public void BeginRound() => Terminated = false;

    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        var admission = layer.Admit(plugin.ToolId(context.Function.Name) ?? context.Function.Name);
        if (!admission.Admitted)
        {
            context.Result = new FunctionResult(context.Function, admission.Feedback ?? "");
            End(context, admission.Terminate);
            return;
        }

        await next(context).ConfigureAwait(false);
        var reported = context.Result.GetValue<object>()?.ToString() ?? "";
        var judgement = Judge(admission.Tool!, new ActionReport(reported, layer.LastOutcome));
        context.Result = new FunctionResult(context.Result, judgement.Feedback);
        End(context, judgement.Terminate);
    }

    private void End(AutoFunctionInvocationContext context, bool terminate)
    {
        if (!terminate) return;
        context.Terminate = true;
        Terminated = true;
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
