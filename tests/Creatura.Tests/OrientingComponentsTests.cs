using Creatura.Orienting;

namespace Creatura.Tests;

/// <summary>
/// The loop-independent parts of the orienting agent (design-sokolov.md), pinned before the regulator exists:
/// habituation from control, waking on change, the four kinds against the goal, frustration landing hardest, the two
/// channels, courage, and generalisation by function.
/// </summary>
public class OrientingComponentsTests
{
    private static readonly OutcomeSignature Paid = new(true, ["fee.paid"]);
    private static readonly OutcomeSignature Nothing = new(false, []);

    private static HashSet<string> F(params string[] facts) => new(facts, StringComparer.Ordinal);

    private static readonly ValenceTable Permit = new(
        F("permit.issued", "citizen.notified"),
        new Dictionary<string, string> { ["fee.paid"] = "permit.issued", ["documents.complete"] = "permit.issued" },
        F("deed.missing", "zoning.doubt"));

    [Fact]
    public void FiveControlledMatchesHabituateAndTheFirstChangeWakesTheResponse()
    {
        var h = new HabituationByMatch();
        Assert.True(h.Observe("pay-fee", "s", Paid).Orient);
        for (var i = 0; i < 4; i++) Assert.False(h.Observe("pay-fee", "s", Paid).Orient);
        Assert.True(h.IsHabituated("pay-fee", "s"));

        var lie = h.Observe("pay-fee", "s", Nothing);
        Assert.True(lie.Orient);
        Assert.False(h.IsHabituated("pay-fee", "s"));

        // Honest again: a change in the other direction wakes it too.
        Assert.True(h.Observe("pay-fee", "s", Paid).Orient);
    }

    [Fact]
    public void ATrapThatFailsTheSameWayNeverHabituates()
    {
        var h = new HabituationByMatch();
        for (var i = 0; i < 10; i++) h.Observe("zoning-quick", "s", Nothing);
        Assert.False(h.IsHabituated("zoning-quick", "s"));
        Assert.Equal(0, h.Matches("zoning-quick", "s"));
    }

    [Fact]
    public void HabituationIsPerSituation()
    {
        var h = new HabituationByMatch();
        for (var i = 0; i < 5; i++) h.Observe("pay-fee", "a", Paid);
        Assert.True(h.IsHabituated("pay-fee", "a"));
        Assert.False(h.IsHabituated("pay-fee", "b"));
        Assert.Equal("x|y", HabituationByMatch.Situation(F("y", "x", "z"), ["x", "y", "w"]));
    }

    [Fact]
    public void AGoalFactIsASatisfactionAndAStepIsAPromiseForItsGoal()
    {
        var step = Valence.Classify(F("documents.complete"), F("documents.complete", "fee.paid"), true, Valence.Expectation.Unclassified, Permit);
        Assert.True(step.Has(ValenceKind.Promise));
        Assert.Equal(["permit.issued"], step.Drives[ValenceKind.Promise]);

        var goal = Valence.Classify(F("inspection.done"), F("inspection.done", "permit.issued"), true, Valence.Expectation.Reward, Permit);
        Assert.True(goal.Has(ValenceKind.Satisfaction));
        Assert.False(goal.Has(ValenceKind.Punishment));
    }

    [Fact]
    public void ASubgoalIsASatisfactionForItselfAndAPromiseForTheGoalAbove()
    {
        var nested = Permit with { Goals = F("fee.paid", "permit.issued") };
        var r = Valence.Classify(F(), F("fee.paid"), true, Valence.Expectation.Unclassified, nested);
        Assert.True(r.Has(ValenceKind.Satisfaction | ValenceKind.Promise));
        Assert.Equal(["fee.paid"], r.Drives[ValenceKind.Satisfaction]);
        Assert.Equal(["permit.issued"], r.Drives[ValenceKind.Promise]);
    }

    [Fact]
    public void FrustrationLandsHarderThanALieAndALieHarderThanAnHonestFailure()
    {
        var frustration = Valence.Classify(F(), F(), true, Valence.Expectation.Reward, Permit);
        var lie = Valence.Classify(F(), F(), true, Valence.Expectation.Unclassified, Permit);
        var honest = Valence.Classify(F(), F(), false, Valence.Expectation.Unclassified, Permit);
        Assert.All([frustration, lie, honest], r => Assert.True(r.Has(ValenceKind.Punishment)));
        Assert.True(frustration.PunishmentWeight > lie.PunishmentWeight);
        Assert.True(lie.PunishmentWeight > honest.PunishmentWeight);
    }

    [Fact]
    public void ATroubleFactIsAThreatAndAnExpectedPunishmentThatDeliversIsRelief()
    {
        var doubt = Valence.Classify(F(), F("zoning.doubt"), true, Valence.Expectation.Unclassified, Permit);
        Assert.True(doubt.Has(ValenceKind.Threat));
        Assert.True(doubt.Has(ValenceKind.Punishment)); // trouble is not delivery

        var relief = Valence.Classify(F("documents.complete"), F("documents.complete", "fee.paid"), true, Valence.Expectation.Punishment, Permit);
        Assert.True(relief.Has(ValenceKind.Relief | ValenceKind.Promise));
    }

    [Fact]
    public void TheTwoChannelsMoveApartAndSettleBackToRest()
    {
        var a = new Affect();
        a.Feel(Valence.Classify(F(), F(), true, Valence.Expectation.Reward, Permit));
        Assert.True(a.I > a.RestingI);
        Assert.True(a.A < a.RestingA);
        Assert.True(a.Freedom <= 1 - a.I + 1e-9 || a.Freedom == 0.2);

        for (var i = 0; i < 30; i++) a.Settle();
        Assert.Equal(a.RestingA, a.A, 2);
        Assert.Equal(a.RestingI, a.I, 2);
    }

    [Fact]
    public void CourageLowersRestingInhibitionAndItCarries()
    {
        var a = new Affect();
        var before = a.RestingI;
        a.Courage();
        a.Courage();
        Assert.True(a.RestingI < before);
        var next = new Affect(a.RestingI);
        Assert.Equal(a.RestingI, next.I, 6);
    }

    [Fact]
    public void ThreatIsPerGoalAndGeneralisesToToolsWithTheSameFunction()
    {
        var w = new ValenceWeights();
        w.Declare("pay-fee", ["fee.paid"]);
        w.Declare("pay-in-person", ["fee.paid"]);
        Assert.Equal(ToolWeight.Unclassified, w.Get("pay-in-person", "permit.issued"));

        var lie = Valence.Classify(F(), F(), true, Valence.Expectation.Reward, Permit);
        for (var i = 0; i < 3; i++) w.Learn("pay-fee", "permit.issued", lie);
        var own = w.Get("pay-fee", "permit.issued");
        Assert.True(own.Threat > own.Promise);

        var sibling = w.Get("pay-in-person", "permit.issued");
        Assert.True(sibling.Promise < 1.0);
        Assert.Equal(0, sibling.Seen);

        // On another goal the same tool starts unclassified.
        Assert.Equal(ToolWeight.Unclassified, w.Get("pay-fee", "citizen.notified"));
        Assert.Equal(ToolWeight.Unclassified, new ValenceWeights { Generalisation = 0 }.Get("pay-in-person", "permit.issued"));
    }

    [Fact]
    public void ALiarExpectedToDeliverEndsMoreThreateningThanAnHonestFailure()
    {
        // Hypothesis S5's mechanism, at component level.
        var w = new ValenceWeights();
        var honest = Valence.Classify(F(), F(), false, Valence.Expectation.Unclassified, Permit);
        var frustrating = Valence.Classify(F(), F(), true, Valence.Expectation.Reward, Permit);
        for (var i = 0; i < 3; i++)
        {
            w.Learn("honest-tool", "permit.issued", honest);
            w.Learn("lying-tool", "permit.issued", frustrating);
        }

        Assert.True(w.Get("lying-tool", "permit.issued").Threat > w.Get("honest-tool", "permit.issued").Threat);
    }
}
