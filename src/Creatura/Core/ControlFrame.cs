namespace Creatura;

/// <summary>
/// How the control layer's verbs render what they decide: the rows, the notes and the audit evidence that
/// <see cref="ControlLayer.Admit"/> and <see cref="ControlLayer.Judge"/> write. The rules are the layer's; the
/// words are the frame's, because every design renders the same decision in its own vocabulary and the
/// readers parse those words. <see cref="HostFrame"/> is how the Agent Framework host has always written
/// them; a regulator is its own frame.
/// </summary>
public interface IControlFrame
{
    /// <summary>The stage gate for a tool. The default is the layer's own <see cref="StageInterlock"/>.</summary>
    GateDecision Gate(ControlLayer layer, Tool tool) => layer.Interlock.Gate(tool);

    /// <summary>A tool the seat named was not in the visible set.</summary>
    void Refused(ControlLayer layer, string toolId, RefusalReason why);

    /// <summary>The record earned the next stage on the way to a release.</summary>
    void StageAdvanced(ControlLayer layer);

    /// <summary>A release the record does not support.</summary>
    void ReleaseRefused(ControlLayer layer, Tool tool);

    /// <summary>What the coherence audit records as evidence against a tool that reported success without effect.</summary>
    string Evidence(ControlLayer layer, Tool tool, ActionReport report);

    /// <summary>
    /// An action was judged. <paramref name="landed"/> is the registry's answer, <paramref name="verdict"/> the
    /// verdict after the re-judge chain; <paramref name="habituated"/>: an honest failure under
    /// <see cref="HonestFailureRule.Habituate"/>, quarantined and not recorded.
    /// </summary>
    void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated);
}

/// <summary>The host's rendering: the rows and notes the control layer on Agent Framework has always written.</summary>
public sealed class HostFrame : IControlFrame
{
    public static readonly HostFrame Instance = new();

    public void Refused(ControlLayer layer, string toolId, RefusalReason why) =>
        layer.Record("organism", "visibility", 1.0, toolId, TraceKind.Blocked, TraceText.Refused(why));

    public void StageAdvanced(ControlLayer layer) =>
        layer.Note(TraceText.StageAdvance(layer.Self.Stage, layer.Predictions.RecentAccuracy));

    public void ReleaseRefused(ControlLayer layer, Tool tool)
    {
        layer.Escalate(TraceText.ReleaseRequiresApproval(layer.Self.Stage, layer.Predictions));
        layer.Record("organism", "stage", 1.0, tool.Id, TraceKind.Escalated, layer.Escalation!);
    }

    public string Evidence(ControlLayer layer, Tool tool, ActionReport report) => $"reported '{report.Summary}' with no effect";

    public void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated)
    {
        switch (verdict)
        {
            case ActionVerdict.Landed:
                layer.Record("cell", "effect", 0.0, tool.Id, TraceKind.Acted, TraceText.EffectVerified(layer.Predictions.RecentAccuracy));
                break;
            case ActionVerdict.HonestFailure when habituated:
                layer.Record("cell", "effect", 0.0, tool.Id, TraceKind.Blocked, TraceText.HonestFailure);
                break;
            case ActionVerdict.HonestFailure:
                layer.Record("cell", "effect", 1.0, tool.Id, TraceKind.Blocked, TraceText.FailedHonestly(report.Summary));
                break;
            default:
                layer.Record("cell", "effect", 1.0, tool.Id, TraceKind.Blocked, TraceText.ReportedButMissing(report.Summary));
                layer.Note(TraceText.Distrusted(tool.Id, report.Summary, layer.Quarantine.CooldownTicks));
                break;
        }
    }
}
