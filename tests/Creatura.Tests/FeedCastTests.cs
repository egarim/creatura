using Creatura.Worlds;
using Creatura.Worlds.Samples;

namespace Creatura.Tests;

/// <summary>The synthetic users (docs/design-axi.md, slice B2): valid, learnable, and their truth computed as the note says.</summary>
public class FeedCastTests
{
    private static readonly Dictionary<string, bool> AllOn = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);

    [Fact]
    public void The_truth_includes_the_noise_clipped_at_0_and_1()
    {
        Assert.Equal(0.0125, FeedCast.Clipped(0, 0.05), 9);   // at 0 the noise can only push up: 0.05 / 4
        Assert.Equal(0.5, FeedCast.Clipped(0.5, 0.3), 9);      // in the middle the noise averages out
        Assert.Equal(1 - 0.0125, FeedCast.Clipped(1, 0.05), 9);
        // the same number the world draws with, measured
        var u = FeedUser.Learner with { Noise = 0.05, Muted = 0, Late = 0 };
        var hits = 0;
        for (var i = 0; i < 20000; i++)
        {
            var w = new TikTokWorld(new Dictionary<string, bool>(), u, seed: i, session: 0);
            while (!w.Batch.Contains(FeedKind.Ad)) w.BeginTick(0);
            w.Act("play-ad");
            if (w.Plays[0].Finished) hits++;
        }
        Assert.Equal(FeedCast.Clipped(0.05, 0.05), hits / 20000.0, 2);
    }

    [Fact]
    public void The_six_archetypes_load_in_order_and_are_valid()
    {
        var cast = FeedCast.Load();
        Assert.Equal(FeedCast.Archetypes, cast.Select(u => u.Id));
        foreach (var u in cast) Assert.True(FeedCast.Problems(u).Count == 0, $"{u.Id}: {string.Join("; ", FeedCast.Problems(u))}");
        Assert.All(cast, u => Assert.False(string.IsNullOrWhiteSpace(u.About)));
    }

    [Fact]
    public void Every_variant_is_valid_and_the_same_seed_gives_the_same_variant()
    {
        foreach (var u in FeedCast.Load())
        {
            foreach (var seed in FeedCast.TrainSeeds.Concat(FeedCast.TestSeeds))
            {
                var v = FeedCast.Variant(u, seed);
                Assert.True(FeedCast.Problems(v).Count == 0, $"{v.Id}: {string.Join("; ", FeedCast.Problems(v))}");
            }
            Assert.Equal(FeedCast.Variant(u, 7).Finish, FeedCast.Variant(u, 7).Finish);
            Assert.NotEqual(FeedCast.Variant(u, 7).Finish, FeedCast.Variant(u, 8).Finish);
        }
        Assert.Empty(FeedCast.TrainSeeds.Intersect(FeedCast.TestSeeds));
    }

    [Fact]
    public void A_broken_profile_is_named()
    {
        var bad = FeedUser.Learner with { Goals = new Dictionary<FeedGoal, double> { [FeedGoal.Learn] = 0.5, [(FeedGoal)999] = 0.5 }, Bait = 1.5, DriftAt = 3 };
        var problems = FeedCast.Problems(bad);
        Assert.Contains("goals has a goal that does not exist: 999", problems);
        Assert.Contains("bait is 1.5, not a chance between 0 and 1", problems);
        Assert.Contains("driftAt without anything to drift to", problems);
        Assert.Contains("goals sum to 0.5000, not 1", FeedCast.Problems(FeedUser.Learner with { Goals = new Dictionary<FeedGoal, double> { [FeedGoal.Learn] = 0.5 } }));
        Assert.Contains("driftAt is -1: a drift starts at session 1 or later", FeedCast.Problems(FeedUser.Learner with { DriftAt = -1, GoalsAfterDrift = FeedUser.Learner.Goals }));
    }

    [Fact]
    public void The_truth_is_taste_times_context_times_the_goals_it_serves()
    {
        var cast = FeedCast.Load().ToDictionary(u => u.Id);
        // the learner: tutorial 0.8, muted 1 session in 5 (a tutorial needs sound: x(0.8 + 0.2 x 0.2) = 0.84), serves Learn (0.8).
        Assert.Equal(0.8 * 0.84 * 0.8, FeedCast.TrueLandedRate(cast["learner"], FeedKind.Tutorial), 9);   // noise 0.05 never clips here
        Assert.Equal(0.0, FeedCast.TrueLandedRate(cast["learner"], FeedKind.Bait));
        // the muted commuter's context takes most of what it likes in creators away.
        var commuter = cast["muted-commuter"];
        Assert.True(FeedCast.TasteLandedRate(commuter, FeedKind.Creator) > 2 * FeedCast.TrueLandedRate(commuter, FeedKind.Creator));
        // the drifter after session 10 is another user.
        var drifter = cast["drifter"];
        Assert.True(FeedCast.TrueLandedRate(drifter, FeedKind.Tutorial, session: 0) > 0.4);
        Assert.Equal(0.0, FeedCast.TrueLandedRate(drifter, FeedKind.Tutorial, session: 10));
        Assert.Equal(0.85 * 0.8 + FeedCast.Clipped(0.85 * 0.2, 0.05) * 0.2, FeedCast.TrueLandedRate(drifter, FeedKind.Creator, session: 10), 9);   // muted 1 in 5 counts after the drift too
    }

    [Fact]
    public void Every_user_is_learnable_the_oracle_clearly_beats_random()
    {
        foreach (var u in FeedCast.Load())
        {
            var oracle = FeedCast.ServedRate(u, FeedCast.OracleChoice, AllOn, FeedCast.TrainSeeds.Take(10));
            var random = FeedCast.ServedRate(u, FeedCast.RandomChoice, AllOn, FeedCast.TrainSeeds.Take(10));
            Assert.True(oracle - random >= 0.15, $"{u.Id}: oracle {oracle:P0}, random {random:P0}");
        }
    }

    [Fact]
    public void The_noisy_users_taste_is_flat_its_only_structure_is_the_shared_goal_mapping()
    {
        var noisy = FeedCast.Load().Single(u => u.Id == "noisy");
        Assert.Single(noisy.Finish.Where(kv => kv.Key != FeedKind.Bait).Select(kv => kv.Value).Distinct());
        foreach (var seed in FeedCast.TestSeeds)                // every variant of a flat taste is flat
            Assert.Single(FeedCast.Variant(noisy, seed).Finish.Where(kv => kv.Key != FeedKind.Bait).Select(kv => kv.Value).Distinct());
        // what differs between kinds comes only from how many of its goals each kind serves
        Assert.Equal(2 * FeedCast.TrueLandedRate(noisy, FeedKind.Tutorial), FeedCast.TrueLandedRate(noisy, FeedKind.Live), 9);
    }
}
