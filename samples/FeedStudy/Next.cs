// The next learner (docs/design-axi.md, slice I), pre-registered before it was built.
//   --tune-i   the grid of 27 v3 settings on the train seeds (1-20), 50 sessions: goals met first, the H4 share as tiebreak
//   --next     the confirmation on fresh seeds 241-260: v2 (slice H's frozen setting), v3 (frozen here), the references
//              with both oracles, and I1 to I5 decided as written
// `dotnet run --project samples/FeedStudy -- --tune-i [--out dir]`
// `dotnet run --project samples/FeedStudy -- --next --ask 0.7 --eps 0.1 --tau 0.4 [--out dir]`
using System.Globalization;
using System.Text;
using System.Text.Json;
using Creatura;
using Creatura.Worlds;
using Creatura.Worlds.Samples;

static class NextStudy
{
    private const int MaxTicks = 20, Sessions = 50;
    public static readonly int[] ConfirmSeeds = [.. Enumerable.Range(241, 20)];
    public static readonly LearnerOptions V2 = ImprovedStudy.V2(0.7, 2, 0, 0.4);

    public static LearnerOptions V3(double ask, double eps, double tau) =>
        ImprovedStudy.V2(ask, 2, eps, tau) with { WithinSession = true, SkimAware = true };

    public static async Task<int> Run(string[] args, bool tune)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i]] = args[i + 1];
        var outDir = Directory.CreateDirectory(opts.GetValueOrDefault("--out", tune ? "feed-tune-i-out" : "feed-next-out")).FullName;
        var cast = FeedCast.Load();
        if (tune) return await Tune(cast, outDir);

        double D(string k) => double.Parse(opts[k], CultureInfo.InvariantCulture);
        var v3 = V3(D("--ask"), D("--eps"), D("--tau"));
        var rows = new List<SessionRow>();
        var plays = new List<PlayScore>();
        var models = new List<ModelRow>();
        var barriers = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);
        var bare = new (string Name, Func<TikTokWorld, IPolicy> Make)[]
        {
            ("max-staytime", w => new MaxStaytimePolicy(w)), ("oracle", w => new FeedOraclePolicy(w)), ("oracle-fair", w => new FairOraclePolicy(w)),
        };
        foreach (var (name, make) in bare)
            foreach (var a in cast)
            foreach (var seed in ConfirmSeeds)
            {
                var user = FeedCast.Variant(a, seed);
                for (var s = 0; s < Sessions; s++)
                {
                    var w = new TikTokWorld(barriers, user, seed, s);
                    await Bare(w, make(w));
                    rows.Add(SessionRow.Of(name, a.Id, user.Id, seed, s, w));
                }
            }
        rows.AddRange(await ImprovedStudy.RunLearner("v2", V2, cast, ConfirmSeeds, plays, models));
        rows.AddRange(await ImprovedStudy.RunLearner("v3", v3, cast, ConfirmSeeds, plays, models));
        File.WriteAllText(Path.Combine(outDir, "sessions.json"), JsonSerializer.Serialize(rows));
        File.WriteAllText(Path.Combine(outDir, "plays.json"), JsonSerializer.Serialize(plays));
        var report = NextReport.Write(rows, plays, models, v3);
        File.WriteAllText(Path.Combine(outDir, "report-next.md"), report);
        Console.WriteLine(report);
        return 0;
    }

    /// <summary>The kinds that matter for a user (H4, I3): those serving a goal its true mix weighs 0.2 or more.</summary>
    public static bool Relevant(ModelRow r, IReadOnlyDictionary<string, FeedUser> cast)
    {
        if (!r.What.StartsWith("finish:", StringComparison.Ordinal)) return false;
        var k = Enum.Parse<FeedKind>(r.What["finish:".Length..]);
        return cast[r.Archetype].Goals.Any(kv => kv.Value >= 0.2 && TikTokWorld.Serves(k, kv.Key));
    }

    /// <summary>The H4 share: users whose every relevant kind is within 0.15 after 50 sessions.</summary>
    public static double H4Share(IEnumerable<ModelRow> models, string learner, IReadOnlyDictionary<string, FeedUser> cast)
    {
        var users = models.Where(r => r.Design == learner && r.Session == Sessions - 1 && r.Archetype is "learner" or "connector" or "late-scroller" && Relevant(r, cast))
            .GroupBy(r => r.User).ToList();
        return users.Count == 0 ? 0 : (double)users.Count(g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15)) / users.Count;
    }

    /// <summary>The grid on the train seeds: goals met first; within 0.2 points of the best, the highest H4 share; then listing order.</summary>
    private static async Task<int> Tune(IReadOnlyList<FeedUser> cast, string outDir)
    {
        var castById = cast.ToDictionary(u => u.Id);
        var results = new List<(double Ask, double Eps, double Tau, double Goals, double H4)>();
        foreach (var ask in new[] { 0.6, 0.7, 0.8 })
        foreach (var eps in new[] { 0.0, 0.1, 0.2 })
        foreach (var tau in new[] { 0.3, 0.4, 0.5 })
        {
            var models = new List<ModelRow>();
            var rows = await ImprovedStudy.RunLearner("tune", V3(ask, eps, tau), cast, [.. FeedCast.TrainSeeds], [], models);
            var goals = rows.Average(r => r.Served ? 1.0 : 0);
            var h4 = H4Share(models, "tune", castById);
            results.Add((ask, eps, tau, goals, h4));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"--ask {ask} --eps {eps} --tau {tau}: goals {goals:P2}, H4 {h4:P0}"));
        }
        var best = results.Max(r => r.Goals);
        var pick = results.Where(r => r.Goals >= best - 0.002).OrderByDescending(r => r.H4).First();   // OrderByDescending is stable: listing order on a tie
        var csv = new StringBuilder("ask,eps,tau,goal_met,h4_share\n");
        foreach (var r in results) csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{r.Ask},{r.Eps},{r.Tau},{r.Goals:0.0000},{r.H4:0.0000}"));
        File.WriteAllText(Path.Combine(outDir, "tune-i.csv"), csv.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"\nfrozen: --ask {pick.Ask} --eps {pick.Eps} --tau {pick.Tau} (goals {pick.Goals:P2}, best {best:P2}; H4 {pick.H4:P0})"));
        return 0;
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

/// <summary>
/// The fair oracle: it knows the truth like the oracle, but it never skips a batch for free. When nothing on offer can serve
/// the session's goal it still plays, the offered kind the user is most likely to finish.
/// </summary>
sealed class FairOraclePolicy(TikTokWorld world) : IPolicy
{
    public string Name => "oracle-fair";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var visible = context.Tools.Select(t => TikTokWorld.KindOf(t.Id)).OfType<FeedKind>().ToList();
        var choice = FeedCast.OracleChoice(world, visible)
            ?? visible.OrderByDescending(world.ExpectedChance).ThenBy(k => k).Select(TikTokWorld.ToolOf).FirstOrDefault();
        return Task.FromResult(new PolicySuggestion(choice, "the truth, and never a free skip"));
    }
}

static class NextReport
{
    private static string P(double x) => $"{x * 100:0}%";
    private static string Pts(double x) => $"{x * 100:+0.0;-0.0;0.0} pts";

    private static (double M, double Lo, double Hi) Ci(IReadOnlyList<double> d)
    {
        var m = d.Average(); var sd = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / (d.Count - 1)); var h = 1.96 * sd / Math.Sqrt(d.Count);
        return (m, m - h, m + h);
    }

    public static string Write(List<SessionRow> rows, List<PlayScore> plays, List<ModelRow> models, LearnerOptions v3)
    {
        var ic = CultureInfo.InvariantCulture;
        var cast = FeedCast.Load().ToDictionary(u => u.Id);
        var sb = new StringBuilder("# The next learner: 6 archetypes x 20 fresh variants (seeds 241-260) x 50 sessions, Levin with round 3's layer\n\n");
        sb.AppendLine(string.Create(ic, $"v3's frozen setting: ask below {v3.AskBelow}, ε {v3.Explore}, τ {v3.ChangeTau}, pool weight {v3.PoolWeight}; within-session update and skimming as context on. v2: slice H's frozen setting.\n"));
        IEnumerable<SessionRow> Of(string p) => rows.Where(r => r.Policy == p);
        var policies = rows.Select(r => r.Policy).Distinct().ToList();
        sb.AppendLine("| policy | goal met | engagement reported (s) | bait plays | plays | tired | asked |\n|---|---|---|---|---|---|---|");
        foreach (var p in policies.OrderByDescending(p => Of(p).Average(r => r.Served ? 1.0 : 0)))
            sb.AppendLine($"| {p} | {P(Of(p).Average(r => r.Served ? 1.0 : 0))} | {Of(p).Average(r => r.Reported):0.0} | {Of(p).Average(r => r.BaitPlays):0.00} | {Of(p).Average(r => r.Plays):0.0} | {P(Of(p).Average(r => r.Tired ? 1.0 : 0))} | {P(Of(p).Average(r => r.Asked ? 1.0 : 0))} |");

        var archetypes = cast.Keys.ToList();
        sb.AppendLine("\n## Goal met by archetype (deciding nothing)\n\n| policy | " + string.Join(" | ", archetypes) + " |\n|---" + string.Concat(archetypes.Select(_ => "|---")) + "|");
        foreach (var p in policies)
            sb.AppendLine($"| {p} | " + string.Join(" | ", archetypes.Select(a => P(Of(p).Where(r => r.Archetype == a).Average(r => r.Served ? 1.0 : 0)))) + " |");

        var byKey = rows.ToDictionary(r => (r.Policy, r.User, r.Session));
        List<double> Paired(string a, string b, string? archetype = null) =>
            [.. Of(a).Where(r => archetype is null || r.Archetype == archetype).Select(r => (r.Served ? 1.0 : 0) - (byKey[(b, r.User, r.Session)].Served ? 1.0 : 0))];

        sb.AppendLine("\n## The tests, decided as pre-registered\n\n| id | rule | result | verdict |\n|---|---|---|---|");
        var i1 = Ci(Paired("v3", "v2"));
        sb.AppendLine($"| I1 | v3 meets more goals than v2 (paired 95% above 0) | {Pts(i1.M)} [{Pts(i1.Lo)}, {Pts(i1.Hi)}] | {(i1.Lo > 0 ? "holds" : "falls")} |");
        var i2 = Ci(Paired("v3", "v2", "muted-commuter"));
        sb.AppendLine($"| I2 | muted commuter: v3 meets more goals than v2 (paired 95% above 0) | {Pts(i2.M)} [{Pts(i2.Lo)}, {Pts(i2.Hi)}] | {(i2.Lo > 0 ? "holds" : "falls")} |");
        var i3 = NextStudy.H4Share(models, "v3", cast);
        var i3v2 = NextStudy.H4Share(models, "v2", cast);
        sb.AppendLine($"| I3 | v3: the kinds that matter within 0.15 after 50 sessions (80% of users) | {P(i3)} (v2: {P(i3v2)}) | {(i3 >= 0.8 ? "holds" : "falls")} |");
        string Drift(string l)
        {
            var users = models.Where(r => r.Design == l && r.Session == 19 && r.Archetype == "drifter" && r.What is "finish:Tutorial" or "finish:Creator").GroupBy(r => r.User).ToList();
            return $"{users.Count(g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15))} of {users.Count}";
        }
        var driftUsers = models.Where(r => r.Design == "v3" && r.Session == 19 && r.Archetype == "drifter" && r.What is "finish:Tutorial" or "finish:Creator").GroupBy(r => r.User).ToList();
        var i4 = driftUsers.Count == 0 ? 0 : (double)driftUsers.Count(g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15)) / driftUsers.Count;
        sb.AppendLine($"| I4 | drifters: tutorial and creator within 0.15 of the new taste by session 20 (80%) | {Drift("v3")} drifters (v2: {Drift("v2")}) | {(i4 >= 0.8 ? "holds" : "falls")} |");
        var v2b = plays.Where(x => x.Learner == "v2" && x.Session >= 25).GroupBy(x => x.User).ToDictionary(g => g.Key, g => g.Average(x => x.Brier));
        var diff = plays.Where(x => x.Learner == "v3" && x.Session >= 25).GroupBy(x => x.User).Where(g => v2b.ContainsKey(g.Key)).Select(g => g.Average(x => x.Brier) - v2b[g.Key]).ToList();
        var i5 = Ci(diff);
        sb.AppendLine($"| I5 | per user, v3's Brier score minus v2's, sessions 26-50 (95% below 0; {diff.Count} users) | {i5.M:+0.000;-0.000} [{i5.Lo:+0.000;-0.000}, {i5.Hi:+0.000;-0.000}] | {(i5.Hi < 0 ? "holds" : "falls")} |");

        var blocks = Enumerable.Range(0, 10).Select(b => (From: b * 5, To: b * 5 + 5)).ToList();
        sb.AppendLine("\n## Goal met by blocks of sessions, per archetype (deciding nothing)\n");
        foreach (var p in new[] { "v2", "v3", "oracle-fair" })
        {
            sb.AppendLine($"\n{p}\n\n| archetype | " + string.Join(" | ", blocks.Select(b => $"{b.From + 1}-{b.To}")) + " |\n|---" + string.Concat(blocks.Select(_ => "|---")) + "|");
            foreach (var a in archetypes)
                sb.AppendLine($"| {a} | " + string.Join(" | ", blocks.Select(b => P(Of(p).Where(r => r.Archetype == a && r.Session >= b.From && r.Session < b.To).Average(r => r.Served ? 1.0 : 0)))) + " |");
        }
        sb.AppendLine("\n## Predicting the persona: mean absolute difference from the true chance of finishing, by blocks (deciding nothing)\n");
        sb.AppendLine("| who predicts | " + string.Join(" | ", blocks.Select(b => $"{b.From + 1}-{b.To}")) + " |\n|---" + string.Concat(blocks.Select(_ => "|---")) + "|");
        foreach (var l in new[] { "v2", "v3" })
            sb.AppendLine($"| {l} | " + string.Join(" | ", blocks.Select(b => plays.Where(p => p.Learner == l && p.Session >= b.From && p.Session < b.To).Select(p => Math.Abs(p.Stated - p.Truth)).DefaultIfEmpty(double.NaN).Average().ToString("0.000", ic))) + " |");
        return sb.ToString();
    }
}
