using Creatura.Hotz;
using Creatura.Worlds;

namespace Creatura.Tests;

/// <summary>
/// The critic composes (migration step 13): the citizen's agent watching the control layer instead of the world,
/// riding the reference loop as an observer-only extension, and the map between the layer's verdict and its own.
/// All on the toy service-restore world with the patch that lies once; <c>HotzAgentTests</c> in the research
/// repository keep pinning the critic itself through <c>Watch(IWorld)</c>.
/// </summary>
public class CriticTests
{
    private static readonly string[] Evidence = ["alert.resolved"];

    private sealed class PlainRegulator : IRegulator
    {
        public Identity Identity => new("plain", "tests");
    }

    /// <summary>A plain regulator whose only act is to hand the loop's layer to the critic before the run.</summary>
    private sealed class LayerWatching(HotzAgent citizen) : IRegulator
    {
        public Identity Identity => new("plain", "tests");

        public List<ActionObservation> Observed { get; } = [];

        public void BeforeRun(RegulatorContext context)
        {
            citizen.Watch(context.Layer);
            context.Layer.ActionObserved += Observed.Add;
        }
    }

    private static World PatchLies()
    {
        var world = RestoreServiceDomain.NewWorld();
        world.MakeLiar("apply-patch");
        return world;
    }

    private static HotzAgent Citizen() => new(new OwnerToken(), RestoreServiceDomain.Task, Evidence);

    private static ReferenceLoop Loop(IRegulator regulator, AgentOptions? options = null) =>
        new(regulator, new PlannerPolicy(), RestoreServiceDomain.Task, options, ControlLayerOptions.Design, "plain");

    private static void AssertSameRun(AgentRun expected, AgentRun actual)
    {
        Assert.Equal(expected.Trace.Count, actual.Trace.Count);
        for (var i = 0; i < expected.Trace.Count; i++) Assert.Equal(expected.Trace[i], actual.Trace[i]);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.GoalReached, actual.GoalReached);
        Assert.Equal(expected.Ticks, actual.Ticks);
    }

    [Fact]
    public async Task WatchingTheLayerYieldsTheSameRouteAsWatchingTheWorld()
    {
        var throughWorld = Citizen();
        var worldA = PatchLies();
        var watched = throughWorld.Watch(worldA);
        var runA = await Loop(new PlainRegulator()).RunAsync(watched, 30);
        var routeA = throughWorld.Close(runA, worldA);

        var throughLayer = Citizen();
        var worldB = PatchLies();
        var runB = await Loop(new LayerWatching(throughLayer)).RunAsync(worldB, 30);
        var routeB = throughLayer.Close(runB, worldB);

        // The same run, whichever side the critic watched from.
        AssertSameRun(runA, runB);
        Assert.Contains(routeA.Claims, c => c.Tool == "apply-patch" && c.ReportedSuccess && !c.Registry.Contains("patch.applied"));
        Assert.Contains(routeA.Judgements, j => j.Tool == "apply-patch" && j.Verdict == Verdict.Contradicted && j.Shown);
        // Byte for byte: the layer-fed claim carries the same ordinal tick the recorder gives, so nothing is "modulo".
        Assert.Equal(routeA.ToJson(), routeB.ToJson());
        var (a, b) = (throughWorld.Report(), throughLayer.Report());
        Assert.Equal((a.Flags, a.Upheld, a.Dismissed, a.Withheld, a.Unchecked, a.LiesInRoute, a.LiesCaught), (b.Flags, b.Upheld, b.Dismissed, b.Withheld, b.Unchecked, b.LiesInRoute, b.LiesCaught));
        Assert.Equal(a.PerCheck.OrderBy(k => k.Key), b.PerCheck.OrderBy(k => k.Key));
    }

    [Fact]
    public async Task TheCriticExtensionLeavesTheTraceUnchangedAndReadsTheSameRoute()
    {
        var alone = await Loop(new PlainRegulator()).RunAsync(PatchLies(), 30);

        var citizen = Citizen();
        var world = PatchLies();
        var withCritic = await Loop(new PlainRegulator(), AgentOptions.With(new CriticExtension(citizen))).RunAsync(world, 30);
        var route = citizen.Close(withCritic, world);

        AssertSameRun(alone, withCritic);

        var reference = Citizen();
        var worldR = PatchLies();
        var watched = reference.Watch(worldR);
        var runR = await Loop(new PlainRegulator()).RunAsync(watched, 30);
        Assert.Equal(reference.Close(runR, worldR).ToJson(), route.ToJson());
        Assert.Equal(1, citizen.Report().LiesCaught);
    }

    [Fact]
    public async Task TheLayersVerdictMappedIsTheCitizensOnTheToyRun()
    {
        var citizen = Citizen();
        var regulator = new LayerWatching(citizen);
        var world = PatchLies();
        var run = await Loop(regulator).RunAsync(world, 30);
        citizen.Close(run, world);

        Assert.Equal(regulator.Observed.Count, citizen.Claims.Count);
        for (var i = 0; i < regulator.Observed.Count; i++)
        {
            var observed = regulator.Observed[i];
            var judgement = citizen.Judgements[i];
            Assert.Equal(observed.Tool.Id, judgement.Tool);
            Assert.True(Verdicts.Agree(observed.Verdict, judgement.Verdict), $"claim {i + 1} {observed.Tool.Id}: layer {observed.Verdict}, critic {judgement.Verdict}");
            // The claim's tick is its ordinal in the case file; the event's is the loop's tick, never behind it.
            Assert.Equal(i + 1, citizen.Claims[i].Tick);
            Assert.True(observed.Tick >= i + 1);
            if (i > 0) Assert.True(observed.Tick > regulator.Observed[i - 1].Tick);
        }

        Assert.Contains(regulator.Observed, o => o.Tool.Id == "apply-patch" && o.Verdict == ActionVerdict.Lied && !o.Landed && o.Outcome.Succeeded);
    }

    [Fact]
    public void TheEventIsRaisedAfterTheRecordAndTheFramesRow()
    {
        var world = PatchLies();
        var self = new SelfModel { Role = "test operator", Principal = "tests", StageLevel = 3 };
        var layer = new ControlLayer(world, RestoreServiceDomain.Task, self, ControlLayerOptions.Design);
        layer.BeginTick();
        layer.BeginRound();
        ActionObservation? seen = null;
        layer.ActionObserved += o =>
        {
            seen = o;
            Assert.Equal(1, layer.Predictions.Total);
            Assert.Contains("apply-patch", layer.Distrust.Hidden(layer.Tick));
            Assert.Contains(layer.Trace, e => e.Action == "apply-patch" && e.Kind == TraceKind.Blocked);
        };

        var admission = layer.Admit("apply-patch");
        var outcome = world.Act("apply-patch");
        layer.ReportOutcome(outcome);
        var judgement = layer.Judge(admission.Tool!, ActionReport.Of(outcome));

        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Tick);
        Assert.Same(outcome, seen.Outcome);
        Assert.Equal(judgement.Verdict, seen.Verdict);
        Assert.Equal(ActionVerdict.Lied, seen.Verdict);
        Assert.False(seen.Landed);
    }

    [Fact]
    public void AReportTheHostDidNotSeeIsObservedAsAClaimOfSuccess()
    {
        var world = RestoreServiceDomain.NewWorld();
        var self = new SelfModel { Role = "test operator", Principal = "tests", StageLevel = 3 };
        var layer = new ControlLayer(world, RestoreServiceDomain.Task, self, ControlLayerOptions.Host);
        var citizen = Citizen();
        citizen.Watch(layer);
        layer.BeginTick();
        layer.BeginRound();

        var admission = layer.Admit("inspect-logs");
        world.Act("inspect-logs");
        layer.Judge(admission.Tool!, new ActionReport("done", null));

        var claim = Assert.Single(citizen.Claims);
        Assert.True(claim.ReportedSuccess);
        Assert.Null(claim.Observation);
        Assert.Equal(Verdict.Supported, Assert.Single(citizen.Judgements).Verdict);
    }

    [Fact]
    public void VerdictsMapIsTotalOneWayAndPartialTheOther()
    {
        Assert.Equal(Verdict.Supported, Verdicts.Map(ActionVerdict.Landed));
        Assert.Equal(Verdict.Contradicted, Verdicts.Map(ActionVerdict.Lied));
        Assert.Equal(Verdict.HonestFailure, Verdicts.Map(ActionVerdict.HonestFailure));
        Assert.All(Enum.GetValues<ActionVerdict>(), v => Assert.Equal(v, Verdicts.Map(Verdicts.Map(v))));

        Assert.Equal(ActionVerdict.Landed, Verdicts.Map(Verdict.Supported));
        Assert.Equal(ActionVerdict.Lied, Verdicts.Map(Verdict.Contradicted));
        Assert.Equal(ActionVerdict.HonestFailure, Verdicts.Map(Verdict.HonestFailure));
        Assert.Null(Verdicts.Map(Verdict.Unchecked));
        Assert.All(Enum.GetValues<ActionVerdict>(), v => Assert.False(Verdicts.Agree(v, Verdict.Unchecked)));
    }

    [Fact]
    public void TheOwnerRegistersTheCriticInTheCatalogUnderItsName()
    {
        var citizen = Citizen();
        ExtensionCatalog.Register("critic", _ => new CriticExtension(citizen));

        var options = ExtensionCatalog.Parse(["critic"]);
        var critic = Assert.IsType<CriticExtension>(Assert.Single(options.Extensions));
        Assert.Same(citizen, critic.Citizen);
        Assert.Equal("critic", critic.Name);
    }
}
