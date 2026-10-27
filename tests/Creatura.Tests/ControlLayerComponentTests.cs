using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The control layer in Core, one component at a time, without a host: Visibility (Offer), Gatekeeper (Admit),
/// EffectCheck (Judge) and Ledger (Failures, predictions, trace, notes). The controlled goldens pin the host's
/// rows; these pin the rules behind them where the host cannot reach: the designs' judge and honest-failure
/// rules, and failures that never reset.
/// </summary>
public class ControlLayerComponentTests
{
    private static (World World, ControlLayer Layer) Fresh(ControlLayerOptions? options = null, AgentTask? task = null)
    {
        var world = RestoreServiceDomain.NewWorld();
        var self = new SelfModel { Role = "test operator", Principal = "tests", StageLevel = 3 };
        var layer = new ControlLayer(world, task ?? RestoreServiceDomain.Task, self, options);
        layer.BeginTick();
        layer.BeginRound();
        return (world, layer);
    }

    [Fact]
    public void AHiddenToolIsNotOfferedAndIsRefusedOnCall()
    {
        var (world, layer) = Fresh();
        world.Revoke("apply-config");
        layer.Distrust.Judged("apply-patch", ActionVerdict.Lied, layer.Tick, habituated: false);

        var offered = layer.Offer().Select(t => t.Id).ToList();
        Assert.DoesNotContain("apply-patch", offered);
        Assert.DoesNotContain("apply-config", offered);
        Assert.Contains("inspect-logs", offered);

        var quarantined = layer.Admit("apply-patch");
        Assert.False(quarantined.Admitted);
        Assert.Equal(RefusalReason.Quarantined, quarantined.Refusal);
        Assert.Contains("REFUSED", quarantined.Feedback, StringComparison.Ordinal);

        var revoked = layer.Admit("apply-config");
        Assert.Equal(RefusalReason.NotAvailable, revoked.Refusal);

        var unknown = layer.Admit("teleport");
        Assert.Equal(RefusalReason.NoSuchTool, unknown.Refusal);
        Assert.Null(unknown.Tool);

        // Every refusal spends budget and leaves a blocked row; the round ends once the budget is exceeded.
        Assert.Equal(3, layer.Failures);
        Assert.Equal(3, layer.Trace.Count(e => e.Kind == TraceKind.Blocked && e.Detail.StartsWith("refused: ", StringComparison.Ordinal)));
        Assert.False(quarantined.Terminate);
        Assert.Equal(layer.Failures > layer.Budget, unknown.Terminate);

        var admitted = layer.Admit("inspect-logs");
        Assert.True(admitted.Admitted);
        Assert.Equal(GateDecision.Open, admitted.Gate);
        Assert.Equal("inspect-logs", admitted.Tool!.Id);
    }

    [Fact]
    public void AReleaseIsRefusedUntilTheRecordEarnsItThenTheStageAdvances()
    {
        var (world, layer) = Fresh();
        var deploy = world.Tool("deploy")!;

        var refused = layer.Admit("deploy");
        Assert.False(refused.Admitted);
        Assert.Equal(RefusalReason.ReleaseNotEarned, refused.Refusal);
        Assert.True(refused.Terminate);
        Assert.NotNull(layer.Escalation);
        Assert.Contains(layer.Trace, e => e.Action == "deploy" && e.Kind == TraceKind.Escalated && e.Detail.StartsWith("release requires human approval", StringComparison.Ordinal));
        Assert.Equal(0, layer.Failures);
        Assert.Equal(3, layer.Self.StageLevel);

        // Four correct predictions earn stage 4, and the same release is admitted with the stage advanced.
        foreach (var id in new[] { "inspect-logs", "identify-component", "apply-patch", "run-tests" })
        {
            var tool = world.Tool(id)!;
            var outcome = world.Act(id);
            var judgement = layer.Judge(tool, new ActionReport(outcome.Observation ?? "ok", outcome));
            Assert.Equal(ActionVerdict.Landed, judgement.Verdict);
        }

        var admitted = layer.Admit("deploy");
        Assert.True(admitted.Admitted);
        Assert.Equal(GateDecision.Advanced, admitted.Gate);
        Assert.Equal(4, layer.Self.StageLevel);
        Assert.Contains(layer.Notes, n => n.StartsWith("stage advance: 4", StringComparison.Ordinal));
        Assert.Same(deploy, admitted.Tool);
    }

    [Fact]
    public void AnHonestFailureIsNotALieUnderTheHostsRules()
    {
        var (world, layer) = Fresh();
        var patch = world.Tool("apply-patch")!;

        // The tool said it failed and nothing landed: budget and reliability are spent, the tool stays visible.
        var honest = layer.Judge(patch, new ActionReport("failed", new ActionOutcome("apply-patch", false, [], "preconditions unmet")));
        Assert.Equal(ActionVerdict.HonestFailure, honest.Verdict);
        Assert.False(honest.Landed);
        Assert.Equal(["patch.applied"], honest.Missing);
        Assert.Equal(1, layer.Failures);
        Assert.Equal(1, layer.Predictions.Total);
        Assert.Equal(0.0, layer.Self.Reliability("apply-patch"));
        Assert.Contains("apply-patch", layer.Offer().Select(t => t.Id));
        Assert.Contains(layer.Trace, e => e.Action == "apply-patch" && e.Kind == TraceKind.Blocked && e.Detail.StartsWith("failed honestly", StringComparison.Ordinal));
        Assert.DoesNotContain(layer.Notes, n => n.Contains("distrusted", StringComparison.Ordinal));

        // The tool said it succeeded and nothing landed: a lie, quarantined, with the warning the seat reads.
        var lie = layer.Judge(patch, new ActionReport("ok", new ActionOutcome("apply-patch", true, [], "reported success")));
        Assert.Equal(ActionVerdict.Lied, lie.Verdict);
        Assert.Equal(2, layer.Failures);
        Assert.DoesNotContain("apply-patch", layer.Offer().Select(t => t.Id));
        Assert.Contains(layer.Notes, n => n.Contains("apply-patch: claimed 'ok', effect missing — distrusted", StringComparison.Ordinal));
        Assert.StartsWith("WARNING", lie.Feedback, StringComparison.Ordinal);
        Assert.Single(layer.Audit.Contradictions);

        // A report the host never saw counts as a claim of success.
        var unseen = layer.Judge(world.Tool("run-tests")!, new ActionReport("", null));
        Assert.Equal(ActionVerdict.Lied, unseen.Verdict);
    }

    [Fact]
    public void TheDesignsRulesHabituateAnHonestFailureAndTheTwoWayJudgeCallsItALie()
    {
        var habituate = Fresh(ControlLayerOptions.Host with { HonestFailureRule = HonestFailureRule.Habituate });
        var patch = habituate.World.Tool("apply-patch")!;
        var honest = habituate.Layer.Judge(patch, new ActionReport("failed", new ActionOutcome("apply-patch", false, [], "preconditions unmet")));
        Assert.Equal(ActionVerdict.HonestFailure, honest.Verdict);
        Assert.False(honest.Terminate);
        Assert.Equal(0, habituate.Layer.Failures);
        Assert.Equal(0, habituate.Layer.Predictions.Total);
        Assert.DoesNotContain("apply-patch", habituate.Layer.Offer().Select(t => t.Id));
        Assert.Contains(habituate.Layer.Trace, e => e.Action == "apply-patch" && e.Detail == "reported failure, nothing landed (honest)");

        var twoWay = Fresh(ControlLayerOptions.Host with { JudgeRule = JudgeRule.TwoWay });
        var lie = twoWay.Layer.Judge(twoWay.World.Tool("apply-patch")!, new ActionReport("failed", new ActionOutcome("apply-patch", false, [], "preconditions unmet")));
        Assert.Equal(ActionVerdict.Lied, lie.Verdict);
        Assert.DoesNotContain("apply-patch", twoWay.Layer.Offer().Select(t => t.Id));
    }

    [Fact]
    public void FailuresResetPerRoundForTheHostAndNeverForTheDesigns()
    {
        var perRound = Fresh();
        perRound.Layer.Admit("teleport");
        perRound.Layer.Admit("teleport");
        Assert.Equal(2, perRound.Layer.Failures);
        perRound.Layer.BeginRound();
        Assert.Equal(0, perRound.Layer.Failures);

        var never = Fresh(ControlLayerOptions.Host with { ResetFailures = ResetFailures.Never });
        never.Layer.Admit("teleport");
        never.Layer.Admit("teleport");
        never.Layer.BeginRound();
        Assert.Equal(2, never.Layer.Failures);
    }

    [Fact]
    public void CloseReadsTheGoalFromTheRegistryAndHandsOffOnNoRouteOnlyWhenNothingIsQuarantined()
    {
        var (world, layer) = Fresh();

        // Something quarantined: no route is a wait, not a hand-off.
        layer.Distrust.Judged("apply-patch", ActionVerdict.Lied, layer.Tick, habituated: false);
        var waiting = layer.Close("NO PATH", noRoute: true);
        Assert.True(waiting.Continue);
        Assert.Contains(layer.Notes, n => n.StartsWith("no path right now", StringComparison.Ordinal));

        // Nothing quarantined: the goal is unregulatable with these tools.
        layer.Distrust.Clear();
        var handedOff = layer.Close("NO PATH", noRoute: true);
        Assert.False(handedOff.Continue);
        Assert.Equal(StopCause.NoPath, handedOff.Stop!.Cause);
        Assert.Equal(HandOffReason.NoPath, handedOff.Stop.HandOff!.Reason);
        Assert.Contains(layer.Trace, e => e.Action == "no path" && e.Kind == TraceKind.Escalated);

        // The colony's option: no route lifts the quarantine instead of waiting it out.
        var clearing = Fresh(ControlLayerOptions.Host with { ClearQuarantineOnNoRoute = true });
        clearing.Layer.Distrust.Judged("apply-patch", ActionVerdict.Lied, clearing.Layer.Tick, habituated: false);
        Assert.True(clearing.Layer.Close("NO PATH", noRoute: true).Continue);
        Assert.Contains("apply-patch", clearing.Layer.Offer().Select(t => t.Id));

        // Success is a predicate over the world, never the seat's text.
        foreach (var id in new[] { "inspect-logs", "identify-component", "apply-patch", "run-tests", "deploy", "resolve-alert" }) world.Act(id);
        var met = layer.Close("I am done", noRoute: false);
        Assert.True(met.GoalReached);
        Assert.Equal(StopCause.GoalMet, met.Stop!.Cause);
        Assert.Contains(layer.Trace, e => e.Action == "contract met" && e.Kind == TraceKind.Acted);
    }
}
