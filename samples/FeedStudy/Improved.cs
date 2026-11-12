// The improved learner (docs/design-axi.md, slice H, issue #79), pre-registered before it was built.
//   --tune       the grid of 81 v2 settings on the train seeds (1-20), 50 sessions: prints and writes tune.csv
//   --improved   the confirmation on fresh seeds 221-240: v1, its five ablations, v2 with the frozen setting, the
//                references, the prediction score, and H1 to H7 decided as written
// `dotnet run --project samples/FeedStudy -- --tune [--out dir]`
// `dotnet run --project samples/FeedStudy -- --improved --ask 0.6 --poolw 4 --eps 0.1 --tau 0.4 [--out dir]`
using System.Globalization;
using System.Text;
using System.Text.Json;
using Creatura;
using Creatura.Worlds;
using Creatura.Worlds.Samples;

static class ImprovedStudy
{
    private const int MaxTicks = 20, Sessions = 50;
    public static readonly int[] ConfirmSeeds = [.. Enumerable.Range(221, 20)];

    public static LearnerOptions V2(double ask, double poolWeight, double eps, double tau) =>
        LearnerOptions.V1 with { AskBelow = ask, PoolWeight = poolWeight, Explore = eps, ChangeTau = tau, SoftGoals = true, Discount = 1.0 };

    public static async Task<int> Run(string[] args, bool tune)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i]] = args[i + 1];
        var outDir = Directory.CreateDirectory(opts.GetValueOrDefault("--out", tune ? "feed-tune-out" : "feed-improved-out")).FullName;
        var cast = FeedCast.Load();
        if (tune) return await Tune(cast, outDir);

        double D(string k) => double.Parse(opts[k], CultureInfo.InvariantCulture);
        var v2 = V2(D("--ask"), D("--poolw"), D("--eps"), D("--tau"));
        var learners = new (string Name, LearnerOptions O)[]
        {
            ("v1", LearnerOptions.V1),
            ("v1-goal", LearnerOptions.V1 with { Goal = false }),
            ("v1-model", LearnerOptions.V1 with { Model = false }),
            ("v1-pool", LearnerOptions.V1 with { Pool = false }),
            ("v1-discount", LearnerOptions.V1 with { Discount = 1.0 }),
            ("v1-ask", LearnerOptions.V1 with { Ask = false }),
            ("v2", v2),
        };
        var rows = new List<SessionRow>();
        var scored = new List<PlayScore>();
        var models = new List<ModelRow>();
        var bare = new (string Name, Func<TikTokWorld, IPolicy> Make)[]
        {
            ("max-staytime", w => new MaxStaytimePolicy(w)), ("taste-known", w => new TasteKnownPolicy(w)), ("oracle", w => new FeedOraclePolicy(w)),
        };
        var barriers = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);
        foreach (var (name, make) in bare)
            foreach (var archetype in cast)
            foreach (var seed in ConfirmSeeds)
            {
                var user = FeedCast.Variant(archetype, seed);
                for (var s = 0; s < Sessions; s++)
                {
                    var w = new TikTokWorld(barriers, user, seed, s);
                    await Bare(w, make(w));
                    rows.Add(SessionRow.Of(name, archetype.Id, user.Id, seed, s, w));
                }
            }
        foreach (var (name, o) in learners)
        {
            var r = await RunLearner(name, o, cast, ConfirmSeeds, scored, models);
            rows.AddRange(r);
            Console.Error.Write('.');
        }
        Console.Error.WriteLine();
        File.WriteAllText(Path.Combine(outDir, "sessions.json"), JsonSerializer.Serialize(rows));
        File.WriteAllText(Path.Combine(outDir, "plays.json"), JsonSerializer.Serialize(scored));
        var report = ImprovedReport.Write(rows, scored, models, v2);
        File.WriteAllText(Path.Combine(outDir, "report-improved.md"), report);
        Console.WriteLine(report);
        return 0;
    }

    /// <summary>The grid on the train seeds; the setting with the highest share of goals met, the earlier one on a tie.</summary>
    private static async Task<int> Tune(IReadOnlyList<FeedUser> cast, string outDir)
    {
        var csv = new StringBuilder("ask,poolw,eps,tau,goal_met\n");
        (double Score, LearnerOptions O, string Label)? best = null;
        foreach (var ask in new[] { 0.5, 0.6, 0.7 })
        foreach (var poolw in new[] { 2.0, 4.0, 8.0 })
        foreach (var eps in new[] { 0.0, 0.1, 0.2 })
        foreach (var tau in new[] { 0.3, 0.4, 0.5 })
        {
            var o = V2(ask, poolw, eps, tau);
            var rows = await RunLearner("tune", o, cast, [.. FeedCast.TrainSeeds], [], []);
            var score = rows.Average(r => r.Served ? 1.0 : 0);
            var label = string.Create(CultureInfo.InvariantCulture, $"--ask {ask} --poolw {poolw} --eps {eps} --tau {tau}");
            csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{ask},{poolw},{eps},{tau},{score:0.0000}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {score:P2}"));
            if (best is null || score > best.Value.Score) best = (score, o, label);
        }
        File.WriteAllText(Path.Combine(outDir, "tune.csv"), csv.ToString());
        Console.WriteLine($"\nfrozen: {best!.Value.Label} ({best.Value.Score:P2} on the train seeds)");
        return 0;
    }

    /// <summary>One learner over every user and session: its own record store and population, Levin with round 3's layer.</summary>
    public static async Task<List<SessionRow>> RunLearner(string name, LearnerOptions o, IReadOnlyList<FeedUser> cast, IReadOnlyList<int> seeds, List<PlayScore> scored, List<ModelRow> models)
    {
        var barriers = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);
        var store = new InMemoryRecordStore();
        var population = new Population(o);
        var rows = new List<SessionRow>();
        foreach (var archetype in cast)
        foreach (var seed in seeds)
        {
            var user = FeedCast.Variant(archetype, seed);
            for (var s = 0; s < Sessions; s++)
            {
                var w = new TikTokWorld(barriers, user, seed, s);
                var key = $"{name}/{user.Id}";
                var model = UserModel.Load(store, key);
                model.Decay(o.Discount);
                var seat = new LearnerSeat(w, model, population, unchecked(seed * 15_485_863 + s), o);
                var options = AgentOptions.With(new FeedNotes(), new RecordCarryExtension(store, key, recordHonestFailures: true));
                var layer = Round2.Layer("levin") with { HonestFailureRule = HonestFailureRule.RecordOnly };
                await AgentCatalog.Make("levin", new AgentBuild(seat, TikTokWorld.Task, options, 1, layer, Store: store, RecordKey: key)).RunAsync(w, MaxTicks);
                foreach (var p in w.Plays)
                    if (seat.Stated.FirstOrDefault(x => x.Tick == p.Tick) is { Kind: var k, P: var stated } && k == p.Kind)
                        scored.Add(new PlayScore(name, archetype.Id, user.Id, s, p.Kind.ToString(), stated, p.PredictedFinish, TrueFinish(user, w, p), p.Finished ? 1 : 0));
                model.Learn(w, population, o);
                model.Save(store, key);
                rows.Add(SessionRow.Of(name, archetype.Id, user.Id, seed, s, w));
                if (s == 19 || s == Sessions - 1) models.AddRange(ModelRow.Of(name, archetype.Id, user, s, model, population));
            }
        }
        return rows;
    }

    /// <summary>The persona's true chance of finishing a play, for scoring only: taste after drift, muted, skimming and late, with the noise clipped.</summary>
    public static double TrueFinish(FeedUser u, TikTokWorld w, FeedPlay p)
    {
        var drifted = u.DriftAt is { } at && w.Session >= at;
        var taste = drifted && u.FinishAfterDrift is { } f ? f : u.Finish;
        var x = p.Kind == FeedKind.Bait ? u.Bait * (w.IsLate ? 1.5 : 1.0) : taste.GetValueOrDefault(p.Kind);
        if (p.Muted && TikTokWorld.NeedsSound(p.Kind)) x *= 0.2;
        if (p.Skimming && p.Kind != FeedKind.Live) x *= 0.5;
        return FeedCast.Clipped(Math.Clamp(x, 0, 1), u.Noise);
    }

    private static async Task Bare(TikTokWorld w, IPolicy policy)
    {
        for (var tick = 1; tick <= MaxTicks && !w.Has(TikTokWorld.Served) && !w.Has("session.ended"); tick++)
        {
            w.BeginTick(tick);
            var context = new PolicyContext { Facts = w.Facts, Tools = w.AvailableTools, Goal = TikTokWorld.Task.Goal, GoalText = TikTokWorld.Task.GoalText, Tick = tick, MaxTicks = MaxTicks, Freedom = 1 };
            if ((await policy.NextAsync(context)).Tool is { } id) w.Act(id);
        }
    }
}

/// <summary>One play, scored: what the learner stated, what the feed predicted, the persona's true chance, and what happened.</summary>
sealed record PlayScore(string Learner, string Archetype, string User, int Session, string Kind, double Stated, double Feed, double Truth, int Finished)
{
    public double Brier => (Stated - Finished) * (Stated - Finished);
    public double FeedBrier => (Feed - Finished) * (Feed - Finished);
}

static class ImprovedReport
{
    private static string P(double x) => $"{x * 100:0}%";
    private static string Pts(double x) => $"{x * 100:+0.0;-0.0;0.0} pts";

    private static (double M, double Lo, double Hi) Ci(IReadOnlyList<double> d)
    {
        var m = d.Average(); var sd = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / (d.Count - 1)); var h = 1.96 * sd / Math.Sqrt(d.Count);
        return (m, m - h, m + h);
    }

    public static string Write(List<SessionRow> rows, List<PlayScore> plays, List<ModelRow> models, LearnerOptions v2)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder($"# The improved learner: 6 archetypes x 20 fresh variants (seeds 221-240) x 50 sessions, Levin with round 3's layer\n\n");
        sb.AppendLine(string.Create(ic, $"v2's frozen setting: ask below {v2.AskBelow}, pool prior weight {v2.PoolWeight}, ε {v2.Explore}, τ {v2.ChangeTau}; soft goal evidence on; no discount.\n"));
        IEnumerable<SessionRow> Of(string p) => rows.Where(r => r.Policy == p);
        var policies = rows.Select(r => r.Policy).Distinct().ToList();
        sb.AppendLine("| policy | goal met | engagement reported (s) | bait plays | plays | asked |\n|---|---|---|---|---|---|");
        foreach (var p in policies.OrderByDescending(p => Of(p).Average(r => r.Served ? 1.0 : 0)))
            sb.AppendLine($"| {p} | {P(Of(p).Average(r => r.Served ? 1.0 : 0))} | {Of(p).Average(r => r.Reported):0.0} | {Of(p).Average(r => r.BaitPlays):0.00} | {Of(p).Average(r => r.Plays):0.0} | {P(Of(p).Average(r => r.Asked ? 1.0 : 0))} |");

        var byKey = rows.ToDictionary(r => (r.Policy, r.User, r.Session));
        List<double> Paired(string a, string b) => [.. Of(a).Select(r => (r.Served ? 1.0 : 0) - (byKey[(b, r.User, r.Session)].Served ? 1.0 : 0))];

        sb.AppendLine("\n## The tests, decided as pre-registered\n\n| id | rule | result | verdict |\n|---|---|---|---|");
        foreach (var lever in new[] { "goal", "model", "pool", "discount", "ask" })
        {
            var c = Ci(Paired($"v1-{lever}", "v1"));
            sb.AppendLine($"| H1-{lever} | v1 without {lever} meets fewer goals than v1 (paired 95% below 0) | {Pts(c.M)} [{Pts(c.Lo)}, {Pts(c.Hi)}] | {(c.Hi < 0 ? "holds" : "falls")} |");
        }
        var h2 = Ci(Paired("v2", "v1"));
        sb.AppendLine($"| H2 | v2 meets more goals than v1 (paired 95% above 0) | {Pts(h2.M)} [{Pts(h2.Lo)}, {Pts(h2.Hi)}] | {(h2.Lo > 0 ? "holds" : "falls")} |");

        string Share(string what, Func<ModelRow, bool> filter, Func<IGrouping<string, ModelRow>, bool> ok, string rule)
        {
            var users = models.Where(filter).GroupBy(r => r.User).ToList();
            var pass = users.Count(ok);
            var share = users.Count == 0 ? 0 : (double)pass / users.Count;
            return $"| {what} | {rule} | {pass} of {users.Count} users ({P(share)}) | {(share >= 0.8 ? "holds" : "falls")} |";
        }
        var cast = FeedCast.Load().ToDictionary(u => u.Id);
        sb.AppendLine(Share("H3", r => r.Design == "v2" && r.Session == 49 && r.Archetype is "learner" or "connector" or "late-scroller" or "muted-commuter" && r.What.StartsWith("goal:", StringComparison.Ordinal),
            g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15), "v2's goal mix, every goal within 0.15 after 50 sessions (80%)"));
        bool Relevant(ModelRow r)
        {
            if (!r.What.StartsWith("finish:", StringComparison.Ordinal)) return false;
            var k = Enum.Parse<FeedKind>(r.What["finish:".Length..]);
            return cast[r.Archetype].Goals.Any(kv => kv.Value >= 0.2 && TikTokWorld.Serves(k, kv.Key));
        }
        sb.AppendLine(Share("H4", r => r.Design == "v2" && r.Session == 49 && r.Archetype is "learner" or "connector" or "late-scroller" && Relevant(r),
            g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15), "v2's finish rate of every kind serving a goal weighed 0.2 or more, within 0.15 after 50 sessions (80%)"));
        sb.AppendLine(Share("H5", r => r.Design == "v2" && r.Session == 19 && r.Archetype == "drifter" && r.What is "finish:Tutorial" or "finish:Creator",
            g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15), "drifters: tutorial and creator within 0.15 of the new taste by session 20 (80%)"));

        var v2plays = plays.Where(x => x.Learner == "v2").ToList();
        var change = v2plays.Where(x => x.Archetype != "drifter").GroupBy(x => x.User)
            .Where(g => g.Any(x => x.Session < 5) && g.Any(x => x.Session >= 25))
            .Select(g => g.Where(x => x.Session >= 25).Average(x => x.Brier) - g.Where(x => x.Session < 5).Average(x => x.Brier)).ToList();
        var h6 = Ci(change);
        sb.AppendLine($"| H6 | per user, v2's Brier score in sessions 26-50 minus sessions 1-5 (95% below 0; {change.Count} users) | {h6.M:+0.000;-0.000} [{h6.Lo:+0.000;-0.000}, {h6.Hi:+0.000;-0.000}] | {(h6.Hi < 0 ? "holds" : "falls")} |");
        var vsFeed = v2plays.Where(x => x.Session >= 25).GroupBy(x => x.User).Select(g => g.Average(x => x.Brier) - g.Average(x => x.FeedBrier)).ToList();
        var h7 = Ci(vsFeed);
        sb.AppendLine($"| H7 | per user, v2's Brier score minus the feed's, sessions 26-50 (95% below 0; {vsFeed.Count} users) | {h7.M:+0.000;-0.000} [{h7.Lo:+0.000;-0.000}, {h7.Hi:+0.000;-0.000}] | {(h7.Hi < 0 ? "holds" : "falls")} |");

        sb.AppendLine("\n## Predicting the persona: mean Brier score by blocks of sessions (lower is better; drifter included)\n");
        var blocks = Enumerable.Range(0, 10).Select(b => (From: b * 5, To: b * 5 + 5)).ToList();
        sb.AppendLine("| who predicts | " + string.Join(" | ", blocks.Select(b => $"{b.From + 1}-{b.To}")) + " |\n|---" + string.Concat(blocks.Select(_ => "|---")) + "|");
        foreach (var l in new[] { "v1", "v2" })
        {
            var x = plays.Where(p => p.Learner == l).ToList();
            sb.AppendLine($"| {l} | " + string.Join(" | ", blocks.Select(b => x.Where(p => p.Session >= b.From && p.Session < b.To).Select(p => p.Brier).DefaultIfEmpty(double.NaN).Average().ToString("0.000", ic))) + " |");
            sb.AppendLine($"| the feed, on {l}'s plays | " + string.Join(" | ", blocks.Select(b => x.Where(p => p.Session >= b.From && p.Session < b.To).Select(p => p.FeedBrier).DefaultIfEmpty(double.NaN).Average().ToString("0.000", ic))) + " |");
            sb.AppendLine($"| the truth, on {l}'s plays (the floor) | " + string.Join(" | ", blocks.Select(b => x.Where(p => p.Session >= b.From && p.Session < b.To).Select(p => (p.Truth - p.Finished) * (p.Truth - p.Finished)).DefaultIfEmpty(double.NaN).Average().ToString("0.000", ic))) + " |");
        }
        sb.AppendLine("\nThe truth row is what a predictor that knew each persona exactly would score: the noise of the draws themselves.");
        sb.AppendLine("\n## Predicting the persona: mean absolute difference from the persona's true chance of finishing (lower is better)\n");
        sb.AppendLine("| who predicts | " + string.Join(" | ", blocks.Select(b => $"{b.From + 1}-{b.To}")) + " |\n|---" + string.Concat(blocks.Select(_ => "|---")) + "|");
        foreach (var l in new[] { "v1", "v2" })
        {
            var x = plays.Where(p => p.Learner == l).ToList();
            sb.AppendLine($"| {l} | " + string.Join(" | ", blocks.Select(b => x.Where(p => p.Session >= b.From && p.Session < b.To).Select(p => Math.Abs(p.Stated - p.Truth)).DefaultIfEmpty(double.NaN).Average().ToString("0.000", ic))) + " |");
            sb.AppendLine($"| the feed, on {l}'s plays | " + string.Join(" | ", blocks.Select(b => x.Where(p => p.Session >= b.From && p.Session < b.To).Select(p => Math.Abs(p.Feed - p.Truth)).DefaultIfEmpty(double.NaN).Average().ToString("0.000", ic))) + " |");
        }

        sb.AppendLine("\n## Goal met by blocks of sessions (drifter left out)\n");
        sb.AppendLine("| policy | " + string.Join(" | ", blocks.Select(b => $"{b.From + 1}-{b.To}")) + " |\n|---" + string.Concat(blocks.Select(_ => "|---")) + "|");
        foreach (var p in policies)
            sb.AppendLine($"| {p} | " + string.Join(" | ", blocks.Select(b => P(Of(p).Where(r => r.Archetype != "drifter" && r.Session >= b.From && r.Session < b.To).Average(r => r.Served ? 1.0 : 0)))) + " |");
        return sb.ToString();
    }
}
