namespace Creatura;

/// <summary>What the stage interlock decided about one tool.</summary>
public enum GateDecision
{
    /// <summary>Not a release, or the stage already permits release: nothing to gate.</summary>
    Open,

    /// <summary>A release, and the prediction record earned the next stage: the stage advanced and the tool may run.</summary>
    Advanced,

    /// <summary>A release the record does not support: refused, a human decides.</summary>
    Refused,
}

/// <summary>
/// The stage interlock: release is a capability, not a step in a plan (Bach, 00:21:15). A release-kind tool
/// runs only when the self-model's stage permits it; a stage is earned by a prediction record, never by a
/// claim. Lived as three copies in the Bach agent, the hybrid and the control layer on Agent Framework;
/// each caller still renders its own rows and notes from the decision.
/// </summary>
public sealed class StageInterlock
{
    private readonly SelfModel _self;
    private readonly PredictionTracker _predictions;
    private readonly string _releaseKind;

    public StageInterlock(SelfModel self, PredictionTracker predictions, string releaseKind)
    {
        _self = self;
        _predictions = predictions;
        _releaseKind = releaseKind;
    }

    public SelfModel Self => _self;

    public PredictionTracker Predictions => _predictions;

    /// <summary>Gate one tool. Advances the stage as a side effect when the record has earned it.</summary>
    public GateDecision Gate(Tool tool)
    {
        if (tool.Kind != _releaseKind || _self.Stage.MayDeploy) return GateDecision.Open;
        if (_predictions.EarnedNextStage)
        {
            _self.StageLevel = Math.Min(Stages.All.Length, _self.StageLevel + 1);
            return GateDecision.Advanced;
        }

        return GateDecision.Refused;
    }
}
