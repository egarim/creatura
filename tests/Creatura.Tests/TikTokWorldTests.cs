using Creatura.Worlds;
using Creatura.Worlds.Samples;

namespace Creatura.Tests;

/// <summary>The virtual TikTok (docs/design-axi.md, slice B): what a play reports against what lands, the barriers, the baselines.</summary>
public class TikTokWorldTests
{
    private static readonly Dictionary<string, bool> AllOn = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);

    private static Dictionary<string, bool> Only(params string[] on) => WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, b => on.Contains(b.Id));

    /// <summary>A user with no randomness left: finishes tutorials always, nothing else, watches bait always, comes to learn.</summary>
    private static FeedUser Certain(Action<Dictionary<FeedKind, double>>? taste = null) => new()
    {
        Id = "certain",
        Goals = new Dictionary<FeedGoal, double> { [FeedGoal.Learn] = 1.0 },
        Finish = Taste(taste),
        Bait = 1.0,
        Noise = 0,
        Muted = 0,
        Late = 0,
    };

    private static Dictionary<FeedKind, double> Taste(Action<Dictionary<FeedKind, double>>? change)
    {
        var t = TikTokWorld.Kinds.ToDictionary(k => k, k => k == FeedKind.Tutorial ? 1.0 : 0.0);
        change?.Invoke(t);
        return t;
    }

    /// <summary>New feed requests until the kind is on offer.</summary>
    private static void Offer(TikTokWorld world, FeedKind kind)
    {
        for (var i = 0; i < 200 && !world.Batch.Contains(kind); i++) world.BeginTick(i);
        Assert.Contains(kind, world.Batch);
    }

    [Fact]
    public void The_same_seed_and_session_give_the_same_session()
    {
        static IReadOnlyList<FeedPlay> Play(int seed)
        {
            var w = new TikTokWorld(AllOn, FeedUser.Learner, seed, session: 3);
            for (var i = 0; i < 8; i++)
            {
                w.BeginTick(i);
                if (w.AvailableTools.FirstOrDefault(t => t.Id.StartsWith("play-", StringComparison.Ordinal)) is { } t) w.Act(t.Id);
            }
            return w.Plays;
        }

        Assert.Equal(Play(7), Play(7));
        Assert.NotEqual(Play(7), Play(8));
    }

    [Fact]
    public void A_play_that_serves_the_goal_lands_bait_lies_and_a_skip_is_honest()
    {
        var world = new TikTokWorld(Only("bait"), Certain());

        Offer(world, FeedKind.Bait);
        var bait = world.Act("play-bait");
        Assert.True(bait.Succeeded);                          // engagement reported
        Assert.Empty(bait.Granted);                           // nothing served: a lie
        Assert.False(world.Has(TikTokWorld.Served));

        Offer(world, FeedKind.Friend);
        var skip = world.Act("play-friend");
        Assert.False(skip.Succeeded);                         // honest failure
        Assert.Contains("skipped", skip.Observation);

        Offer(world, FeedKind.Tutorial);
        var tutorial = world.Act("play-tutorial");
        Assert.True(tutorial.Succeeded);
        Assert.Equal([TikTokWorld.Served], tutorial.Granted);
        Assert.True(TikTokWorld.Task.Goal(world.Facts));
        Assert.Equal(1.0, TikTokWorld.Task.Contract(world));
        Assert.Equal(25 + 60, world.Plays.Where(p => p.Finished).Sum(p => p.Seconds));
    }

    [Fact]
    public void Bait_is_offered_only_with_its_barrier_and_its_stay_is_over_predicted()
    {
        var without = new TikTokWorld(Only(), Certain());
        for (var i = 0; i < 50; i++) { without.BeginTick(i); Assert.DoesNotContain(FeedKind.Bait, without.Batch); }

        var with = new TikTokWorld(Only("bait"), Certain());
        Offer(with, FeedKind.Bait);
        Assert.True(with.Predicted(FeedKind.Bait)!.StaySeconds > TikTokWorld.Length(FeedKind.Bait), "bait's stay is predicted beyond its length");
    }

    [Fact]
    public void A_muted_user_skips_what_needs_sound_and_the_prediction_does_not_know()
    {
        var muted = new TikTokWorld(Only("muted"), Certain() with { Muted = 1.0 });
        Assert.True(muted.IsMuted);
        Assert.Equal(0.2, muted.ExpectedChance(FeedKind.Tutorial), 6);
        Offer(muted, FeedKind.Tutorial);
        Assert.True(muted.Predicted(FeedKind.Tutorial)!.Finish >= 0.9);

        var barrierOff = new TikTokWorld(Only(), Certain() with { Muted = 1.0 });
        Assert.False(barrierOff.IsMuted);
    }

    [Fact]
    public void Ads_fail_honestly_under_adload()
    {
        var world = new TikTokWorld(Only("adload"), Certain());
        Offer(world, FeedKind.Ad);
        var ad = world.Act("play-ad");
        Assert.False(ad.Succeeded);
        Assert.Equal("obs.ad-failed", ad.Observation);
    }

    [Fact]
    public void The_autoplay_button_is_offered_only_with_its_barrier_and_is_no_goal()
    {
        Assert.DoesNotContain(new TikTokWorld(Only(), Certain()).AvailableTools, t => t.Id == "mark-session-complete");
        var world = new TikTokWorld(Only("autoplay"), Certain());
        Assert.True(world.Act("mark-session-complete").Succeeded);
        Assert.False(TikTokWorld.Task.Goal(world.Facts));
    }

    [Fact]
    public void Short_plays_in_a_row_make_the_user_skim_and_asking_brings_them_back()
    {
        var world = new TikTokWorld(Only("skim"), Certain(t => t[FeedKind.Tutorial] = 0) with { SkimAfter = 2 });
        Offer(world, FeedKind.Friend); world.Act("play-friend");
        Assert.False(world.IsSkimming);
        Offer(world, FeedKind.Creator); world.Act("play-creator");
        Assert.True(world.IsSkimming);
        Assert.True(world.Has("user.skimming"));

        var ask = world.Act("ask-what-they-want");
        Assert.Equal("the user says they want to learn", ask.Observation);
        Assert.False(world.IsSkimming);
        Assert.Contains("the user said they want to learn", world.Notes());
    }

    [Fact]
    public void A_tired_user_can_only_end_the_session_and_a_late_one_is_kept_up()
    {
        var world = new TikTokWorld(Only(), Certain(t => t[FeedKind.Tutorial] = 0) with { TiredAfter = 2 });
        for (var i = 0; i < 2; i++) { world.BeginTick(i); world.Act(world.AvailableTools.First(t => t.Id.StartsWith("play-", StringComparison.Ordinal)).Id); }
        Assert.True(world.Has("user.tired"));
        Assert.Equal(["end-session"], world.AvailableTools.Select(t => t.Id));

        var late = new TikTokWorld(Only("late"), Certain(t => t[FeedKind.Tutorial] = 0) with { TiredAfter = 2, Late = 1.0 });
        Assert.True(late.IsLate);
        for (var i = 0; i < 3; i++) { late.BeginTick(i); late.Act(late.AvailableTools.First(t => t.Id.StartsWith("play-", StringComparison.Ordinal)).Id); }
        Assert.True(late.Has("user.slept-late"));
        Assert.False(late.Has("user.tired"));                 // late at night the session runs twice as long
    }

    [Fact]
    public void After_drift_the_user_has_the_new_taste_and_the_prediction_still_has_the_old()
    {
        var drifter = Certain() with
        {
            DriftAt = 5,
            FinishAfterDrift = Taste(t => { t[FeedKind.Tutorial] = 0; t[FeedKind.Creator] = 1; }),
            GoalsAfterDrift = new Dictionary<FeedGoal, double> { [FeedGoal.Entertain] = 1.0 },
        };
        var before = new TikTokWorld(Only(), drifter, session: 4);
        var after = new TikTokWorld(Only(), drifter, session: 5);
        Assert.Equal(FeedGoal.Learn, before.Goal);
        Assert.Equal(FeedGoal.Entertain, after.Goal);
        Assert.Equal(1.0, after.ExpectedChance(FeedKind.Creator), 6);
        Offer(after, FeedKind.Tutorial);
        Assert.True(after.Predicted(FeedKind.Tutorial)!.Finish >= 0.9, "the feed's prediction lags the drift");
    }

    [Fact]
    public async Task The_baselines_follow_the_predictions_and_the_last_resort_plays_live()
    {
        var world = new TikTokWorld(Only("bait", "skim"), Certain(t => t[FeedKind.Tutorial] = 0) with { SkimAfter = 1 });
        Offer(world, FeedKind.Bait);
        var context = new PolicyContext
        {
            Facts = world.Facts, Tools = world.AvailableTools, Goal = TikTokWorld.Task.Goal, GoalText = "", Tick = 1, MaxTicks = 10, Freedom = 1,
        };
        Assert.Equal("play-bait", (await new MaxStaytimePolicy(world).NextAsync(context)).Tool);

        Offer(world, FeedKind.Friend); world.Act("play-friend");
        Assert.True(world.IsSkimming);
        Offer(world, FeedKind.Live);
        var skimming = context with { Facts = world.Facts, Tools = world.AvailableTools };
        Assert.Equal("play-live", (await new LastResortPolicy(world).NextAsync(skimming)).Tool);
    }

    [Theory]
    [InlineData("bach")]
    [InlineData("levin")]
    [InlineData("peterson")]
    [InlineData("orienting")]
    [InlineData("hybrid")]
    public async Task Every_design_runs_a_session_from_the_catalog(string design)
    {
        var manifest = WorldCatalog.TryFind("tiktok")!;
        var agent = AgentCatalog.Make(design, new AgentBuild(new PlannerPolicy(), manifest.Task, AgentOptions.With(new FeedNotes()), 1));
        var run = await agent.RunAsync(manifest.NewWorld(BarrierSet.Defaults(manifest)), 20);
        Assert.NotEmpty(run.Trace);
    }
}
