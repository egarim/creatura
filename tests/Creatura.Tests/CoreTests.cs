using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The primitives, tested against the claims the designs make about them.
/// </summary>
public class CoreTests
{
    [Fact]
    public void StressLeaksSidewaysSoNeighboursBecomeFlexible()
    {
        var world = RestoreServiceDomain.NewWorld();
        var bus = new StressBus { Tick = 1 };
        var calm = new SetPoint { Id = "effect", Space = "tool effect", Progress = _ => 1.0 };
        var struggling = new SetPoint { Id = "contract", Space = "service health", Progress = _ => 0.0 };

        bus.Publish("cell", calm, world);
        bus.Publish("organism", struggling, world);

        Assert.Equal(0.0, bus.Own("cell"));
        Assert.True(bus.Effective("cell") > 0.0, "a struggling neighbour must leak stress into a calm level");
        Assert.Equal(1.0, bus.Aggregate);
    }

    [Fact]
    public void PlasticityRisesWithErrorAndStaysBounded()
    {
        Assert.Equal(0.2, Plasticity.Freedom(0.0), 3);
        Assert.Equal(1.0, Plasticity.Freedom(1.0), 3);
        Assert.True(Plasticity.Freedom(0.9) > Plasticity.Freedom(0.1));
        Assert.InRange(Plasticity.Freedom(5.0), 0.0, 1.0);
    }

    [Fact]
    public void ALiarReportsSuccessAndGrantsNothing()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("inspect-logs");

        var outcome = world.Act("inspect-logs");

        Assert.True(outcome.Succeeded);
        Assert.Equal("reported success", outcome.Observation);
        Assert.False(world.EffectsLanded(world.Tool("inspect-logs")!));
    }

    [Fact]
    public void ARevokedToolReportsUnavailableAndVanishesFromPlanning()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.Revoke("apply-patch");

        var outcome = world.Act("apply-patch");

        Assert.False(outcome.Succeeded);
        Assert.Equal("tool unavailable", outcome.Observation);
        Assert.DoesNotContain(world.AvailableTools, t => t.Id == "apply-patch");
    }

    [Fact]
    public void PlannerFollowsTheHabitAndDetoursAroundExcludedTools()
    {
        var world = RestoreServiceDomain.NewWorld();

        var habitual = Planner.Find(world, RestoreServiceDomain.Goal, 8, null, RestoreServiceDomain.Habit);
        Assert.NotNull(habitual);
        Assert.Contains("apply-patch", habitual!);

        var detour = Planner.Find(world, RestoreServiceDomain.Goal, 8, new HashSet<string> { "apply-patch" },
            RestoreServiceDomain.Habit);
        Assert.NotNull(detour);
        Assert.Contains("apply-config", detour!);
        Assert.DoesNotContain("apply-patch", detour!);
    }

    [Fact]
    public void CoherenceDropsWithContradictionsAndKeepsTheEvidence()
    {
        var audit = new CoherenceAudit();
        audit.Claim();
        Assert.True(audit.Accept("apply-patch produces patch.applied", true, "effect landed", 1));
        Assert.Equal(1.0, audit.Score, 3);

        audit.Claim();
        Assert.False(audit.Accept("apply-patch produces patch.applied", false, "no effect", 2));
        // Two claims, one of them contradicted: the score is pinned, not merely "below one".
        Assert.Equal(0.5, audit.Score, 3);
        Assert.Single(audit.Contradictions);
        Assert.Contains("no effect", audit.Contradictions[0].Evidence);
    }

    [Fact]
    public void AStageIsEarnedByPredictionsNotByConfidence()
    {
        var tracker = new PredictionTracker();
        for (var i = 0; i < 3; i++)
        {
            tracker.Record(true);
        }

        Assert.False(tracker.EarnedNextStage, "three predictions is not a record");

        tracker.Record(true);
        Assert.True(tracker.EarnedNextStage);

        var unreliable = new PredictionTracker();
        unreliable.Record(true);
        unreliable.Record(false);
        unreliable.Record(false);
        unreliable.Record(false);
        Assert.False(unreliable.EarnedNextStage);
    }

    [Fact]
    public void TheSelfModelRendersItsEvidenceAndForgetsNothingSilently()
    {
        var model = new SelfModel { Role = "operator", Principal = "joche" };
        model.RecordOutcome("apply-patch", true);
        model.RecordOutcome("apply-patch", false);

        var rendered = model.Render();

        Assert.Contains("role: operator for joche", rendered);
        Assert.Contains("apply-patch", rendered);
        Assert.Contains("50%", rendered);
        Assert.Contains("did not do what I expected", rendered);
    }
}
