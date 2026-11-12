// The virtual TikTok study (docs/design-axi.md, slice D): every policy against every synthetic user over seeded sessions,
// the leaderboards on goals met and on engagement, and what each design's per-user record learned against the user's truth.
// Offline and repeatable seed by seed. `dotnet run --project samples/FeedStudy -- [--seeds 10] [--sessions 20] [--out dir]`
// Optional: `--model <name> --model-url <OpenAI-compatible base> [--model-seeds 2]` adds Bach with a model in the seat.
using System.Globalization;
using System.Text;
using System.Text.Json;
using Creatura;
using Creatura.Ratings;
using Creatura.Seats;
using Creatura.Worlds;
using Creatura.Worlds.Samples;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var opts = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i]] = args[i + 1];
var seedCount = int.Parse(opts.GetValueOrDefault("--seeds", "10"));
var sessions = int.Parse(opts.GetValueOrDefault("--sessions", "20"));
var outDir = Directory.CreateDirectory(opts.GetValueOrDefault("--out", "feed-study-out")).FullName;
const int MaxTicks = 20;
var barriers = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);
var cast = FeedCast.Load();
var seeds = FeedCast.TestSeeds.Take(seedCount).ToList();
string[] designs = ["bach", "levin", "peterson", "orienting", "hybrid"];

// The choosers. Bare ones run with no control layer: the feed as it is. Designs wrap the same max-staytime chooser in
// Creatura's control layer, so the only difference between "max-staytime" and "bach+max-staytime" is the layer.
var bare = new (string Name, Func<TikTokWorld, IPolicy> Make)[]
{
    ("max-staytime", w => new MaxStaytimePolicy(w)),
    ("last-resort", w => new LastResortPolicy(w)),
    ("random", w => new RandomPolicy(w)),
    ("oracle", w => new FeedOraclePolicy(w)),
};
var modelName = opts.GetValueOrDefault("--model");
ModelEndpoint? endpoint = modelName is null ? null
    : new ModelEndpoint(modelName, Provider.OpenAiCompatible, opts.GetValueOrDefault("--model-url", "http://100.64.0.5:8080/v1"), modelName, null, NativeTools: true, Reasoning: false, null, null);
var modelClient = new ModelClient();
var modelSeeds = int.Parse(opts.GetValueOrDefault("--model-seeds", "2"));

var rows = new List<SessionRow>();
var learned = new List<LearnedRow>();
var store = new InMemoryRecordStore();

foreach (var archetype in cast)
foreach (var seed in seeds)
{
    var user = FeedCast.Variant(archetype, seed);
    for (var s = 0; s < sessions; s++)
    {
        foreach (var (name, make) in bare)
        {
            var w = new TikTokWorld(barriers, user, seed, s);
            await Bare(w, make(w));
            rows.Add(SessionRow.Of(name, archetype.Id, user.Id, seed, s, w));
        }

        foreach (var design in designs)
        {
            var w = new TikTokWorld(barriers, user, seed, s);
            await Designed(design, w, new MaxStaytimePolicy(w), $"{design}/{user.Id}");
            rows.Add(SessionRow.Of($"{design}+max-staytime", archetype.Id, user.Id, seed, s, w));
            learned.AddRange(LearnedRow.Of(design, archetype.Id, user, s, store.Load($"{design}/{user.Id}")));
        }

        if (endpoint is not null && seeds.IndexOf(seed) < modelSeeds)
        {
            var w = new TikTokWorld(barriers, user, seed, s);
            await Designed("bach", w, new ModelPolicy(endpoint, modelClient, new CallLog(Path.Combine(outDir, "model-calls.jsonl")), "feed"), $"bach+{endpoint.Name}/{user.Id}");
            rows.Add(SessionRow.Of($"bach+{endpoint.Name}", archetype.Id, user.Id, seed, s, w));
        }
    }
    Console.Error.Write('.');
}
Console.Error.WriteLine();

File.WriteAllText(Path.Combine(outDir, "sessions.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false }));
File.WriteAllText(Path.Combine(outDir, "learned.json"), JsonSerializer.Serialize(learned, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
var report = Report.Write(rows, learned, cast, sessions, seeds);
File.WriteAllText(Path.Combine(outDir, "report.md"), report);
Console.WriteLine(report);
return 0;

async Task Bare(TikTokWorld w, IPolicy policy)
{
    for (var tick = 1; tick <= MaxTicks && !w.Has(TikTokWorld.Served) && !w.Has("session.ended"); tick++)
    {
        w.BeginTick(tick);
        var context = new PolicyContext { Facts = w.Facts, Tools = w.AvailableTools, Goal = TikTokWorld.Task.Goal, GoalText = TikTokWorld.Task.GoalText, Tick = tick, MaxTicks = MaxTicks, Freedom = 1 };
        if ((await policy.NextAsync(context)).Tool is { } id) w.Act(id);
    }
}

async Task Designed(string design, TikTokWorld w, IPolicy seat, string key)
{
    var options = AgentOptions.With(new FeedNotes(), new RecordCarryExtension(store, key));
    var agent = AgentCatalog.Make(design, new AgentBuild(seat, TikTokWorld.Task, options, 1, Store: store, RecordKey: key));
    await agent.RunAsync(w, MaxTicks);
}

/// <summary>A uniform choice among the kinds on offer, seeded by the session.</summary>
sealed class RandomPolicy(TikTokWorld world) : IPolicy
{
    private readonly Random _rng = new(unchecked(world.Session * 7_919 + world.User.Id.Length));

    public string Name => "random";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PolicySuggestion(FeedCast.RandomChoice(world, _rng), "random"));
}

/// <summary>One session as the study reads it.</summary>
sealed record SessionRow(string Policy, string Archetype, string User, int Seed, int Session, bool Served, bool Contract,
    double Engagement, int Plays, int BaitPlays, bool Skimmed, int SkimPlays, bool Tired, bool Late)
{
    public static SessionRow Of(string policy, string archetype, string user, int seed, int session, TikTokWorld w) => new(
        policy, archetype, user, seed, session, w.Has(TikTokWorld.Served), TikTokWorld.Task.Contract(w) >= 1.0, Math.Round(w.EngagementSeconds, 1),
        w.Plays.Count, w.Plays.Count(p => p.Kind == FeedKind.Bait), w.Has("user.skimming"), w.Plays.Count(p => p.Skimming), w.Has("user.tired"), w.Has("user.slept-late"));
}

/// <summary>What a design's record holds about one kind for one user after a session, beside the truth.</summary>
sealed record LearnedRow(string Design, string Archetype, string User, int Session, string Kind, int Attempted, double Learned,
    double TrueLanded, double TasteLanded, double ServesShare)
{
    public static IEnumerable<LearnedRow> Of(string design, string archetype, FeedUser user, int session, AgentRecord? record) =>
        TikTokWorld.Kinds.Select(k =>
        {
            var belief = record?.Skills.GetValueOrDefault(TikTokWorld.ToolOf(k));
            var goals = user.DriftAt is { } at && session >= at && user.GoalsAfterDrift is { } g ? g : user.Goals;
            var share = goals.Sum(kv => TikTokWorld.Serves(k, kv.Key) ? kv.Value : 0);
            return new LearnedRow(design, archetype, user.Id, session, k.ToString(), belief?.Attempted ?? 0, belief?.Reliability ?? double.NaN,
                FeedCast.TrueLandedRate(user, k, session), FeedCast.TasteLandedRate(user, k, session), share);
        });
}

static class Report
{
    private static string P(double x) => double.IsNaN(x) ? "n/a" : $"{x * 100:0}%";

    public static string Write(List<SessionRow> rows, List<LearnedRow> learned, IReadOnlyList<FeedUser> cast, int sessions, List<int> seeds)
    {
        var sb = new StringBuilder();
        var policies = rows.Select(r => r.Policy).Distinct().ToList();
        sb.AppendLine($"# The virtual TikTok study: {cast.Count} archetypes x {seeds.Count} test variants x {sessions} sessions, every barrier on\n");
        sb.AppendLine("## Every policy, all users\n");
        sb.AppendLine("| policy | goal met | contract kept | engagement (s per session) | bait plays per session | skimmed | sessions |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var p in policies.OrderByDescending(p => rows.Where(r => r.Policy == p).Average(r => r.Served ? 1.0 : 0)))
        {
            var r = rows.Where(x => x.Policy == p).ToList();
            sb.AppendLine($"| {p} | {P(r.Average(x => x.Served ? 1.0 : 0))} | {P(r.Average(x => x.Contract ? 1.0 : 0))} | {r.Average(x => x.Engagement):0.0} | {r.Average(x => x.BaitPlays):0.00} | {P(r.Average(x => x.Skimmed ? 1.0 : 0))} | {r.Count} |");
        }

        sb.AppendLine("\n## Goal met, by archetype\n");
        sb.AppendLine("| policy | " + string.Join(" | ", cast.Select(u => u.Id)) + " |");
        sb.AppendLine("|---" + string.Concat(cast.Select(_ => "|---")) + "|");
        foreach (var p in policies)
            sb.AppendLine($"| {p} | " + string.Join(" | ", cast.Select(u => P(rows.Where(r => r.Policy == p && r.Archetype == u.Id).Average(r => r.Served ? 1.0 : 0)))) + " |");

        // The leaderboards: one game per (user, session), the policies ranked on it.
        var job = (SessionRow r) => $"{r.User}/{r.Session}";
        var goalRuns = rows.Select(r => new Run("feed", job(r), DateTimeOffset.UnixEpoch.AddMinutes(r.Session), new Dictionary<Axis, string> { [Axis.Design] = r.Policy },
            r.Contract ? 2 : r.Served ? 1 : 0, r.Served ? r.Plays : 0));   // fewer plays breaks a tie only between sessions that met the goal: quitting early is no merit
        var engagementRuns = rows.Select(r => new Run("feed", job(r), DateTimeOffset.UnixEpoch.AddMinutes(r.Session), new Dictionary<Axis, string> { [Axis.Design] = r.Policy },
            Math.Round(r.Engagement / 15), 0));
        sb.AppendLine("\n## Leaderboard on the user's goal (contract kept > goal met > not met; then fewer plays)\n\n```");
        sb.Append(Leaderboard.Render(Leaderboard.Compute(goalRuns, Axis.Design)));
        sb.AppendLine("```\n\n## Leaderboard on engagement (seconds watched, in 15 s steps)\n\n```");
        sb.Append(Leaderboard.Render(Leaderboard.Compute(engagementRuns, Axis.Design)));
        sb.AppendLine("```");

        var goalBoard = Leaderboard.Compute(rows.Select(r => new Run("feed", job(r), DateTimeOffset.UnixEpoch.AddMinutes(r.Session),
            new Dictionary<Axis, string> { [Axis.Design] = r.Policy }, r.Contract ? 2 : r.Served ? 1 : 0, r.Served ? r.Plays : 0)), Axis.Design);
        sb.Append(Verdicts.Write(rows, learned, goalBoard, sessions));

        sb.AppendLine("\n## The late scroller's bait plays, first five sessions against last five\n");
        sb.AppendLine("| policy | sessions 1-5 | last 5 |\n|---|---|---|");
        foreach (var p in policies)
        {
            var r = rows.Where(x => x.Policy == p && x.Archetype == "late-scroller").ToList();
            sb.AppendLine($"| {p} | {r.Where(x => x.Session < 5).Average(x => x.BaitPlays):0.00} | {r.Where(x => x.Session >= sessions - 5).Average(x => x.BaitPlays):0.00} |");
        }

        sb.AppendLine("\n## What the records learned, against the truth (after session 10)\n");
        sb.AppendLine("Learned = the record's reliability per kind (landed / counted). True = the chance a play serves the user (taste x context x goals).");
        sb.AppendLine("Serves-share = the chance a *finished* play serves (goals only): what a record that does not count skips can at best learn.\n");
        sb.AppendLine("| design | archetype | kinds with 3+ plays | mean error vs true | rank corr. vs true | mean error vs serves-share |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var at10 = learned.Where(l => l.Session == Math.Min(9, sessions - 1)).ToList();
        foreach (var d in learned.Select(l => l.Design).Distinct())
        foreach (var u in cast)
        {
            var per = at10.Where(l => l.Design == d && l.Archetype == u.Id && l.Attempted >= 3).ToList();
            var users = per.GroupBy(l => l.User).ToList();
            var cover = users.Count == 0 ? 0 : users.Average(g => g.Count());
            var err = per.Count == 0 ? double.NaN : per.Average(l => Math.Abs(l.Learned - l.TrueLanded));
            var errShare = per.Count == 0 ? double.NaN : per.Average(l => Math.Abs(l.Learned - l.ServesShare));
            var rho = users.Where(g => g.Count() >= 3).Select(g => Spearman(g.Select(l => l.Learned).ToList(), g.Select(l => l.TrueLanded).ToList())).DefaultIfEmpty(double.NaN).Average();
            sb.AppendLine($"| {d} | {u.Id} | {cover:0.0} of 7 | {(double.IsNaN(err) ? "n/a" : err.ToString("0.00"))} | {(double.IsNaN(rho) ? "n/a" : rho.ToString("0.00"))} | {(double.IsNaN(errShare) ? "n/a" : errShare.ToString("0.00"))} |");
        }
        return sb.ToString();
    }

    /// <summary>Spearman's rank correlation, ties given their average rank.</summary>
    public static double Spearman(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        static double[] Ranks(IReadOnlyList<double> x)
        {
            var order = x.Select((v, i) => (v, i)).OrderBy(t => t.v).ToList();
            var r = new double[x.Count];
            for (var i = 0; i < order.Count;)
            {
                var j = i;
                while (j + 1 < order.Count && order[j + 1].v == order[i].v) j++;
                for (var k = i; k <= j; k++) r[order[k].i] = (i + j) / 2.0 + 1;
                i = j + 1;
            }
            return r;
        }
        var ra = Ranks(a); var rb = Ranks(b);
        double ma = ra.Average(), mb = rb.Average();
        var cov = ra.Zip(rb, (x, y) => (x - ma) * (y - mb)).Sum();
        var va = Math.Sqrt(ra.Sum(x => (x - ma) * (x - ma))); var vb = Math.Sqrt(rb.Sum(y => (y - mb) * (y - mb)));
        return va == 0 || vb == 0 ? double.NaN : cov / (va * vb);
    }
}

/// <summary>T1 to T8 decided by the rules docs/design-axi.md pre-registered, plus paired differences marked as not pre-registered.</summary>
static class Verdicts
{
    private const string Base = "max-staytime";
    private static readonly string[] Designs = ["bach", "levin", "peterson", "orienting", "hybrid"];

    public static string Write(List<SessionRow> rows, List<LearnedRow> learned, Board goalBoard, int sessions)
    {
        var sb = new StringBuilder("\n## The hypotheses, decided by their pre-registered rules\n\n");
        string D(string d) => $"{d}+{Base}";
        double Rate(string p, Func<SessionRow, bool> f, string? archetype = null) => rows.Where(r => r.Policy == p && (archetype is null || r.Archetype == archetype)).Average(r => f(r) ? 1.0 : 0);
        double Mean(string p, Func<SessionRow, double> f, string? archetype = null) => rows.Where(r => r.Policy == p && (archetype is null || r.Archetype == archetype)).Average(f);

        // Paired difference over the same (user, session): mean and a 95% interval (normal approximation). Not pre-registered.
        (double Mean, double Lo, double Hi) Paired(string a, string b, Func<SessionRow, double> f)
        {
            var bs = rows.Where(r => r.Policy == b).ToDictionary(r => (r.User, r.Session));
            var d = rows.Where(r => r.Policy == a).Select(r => f(r) - f(bs[(r.User, r.Session)])).ToList();
            var m = d.Average(); var sd = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / (d.Count - 1)); var h = 1.96 * sd / Math.Sqrt(d.Count);
            return (m, m - h, m + h);
        }
        double TellApart(string a, string b)
        {
            var ea = goalBoard.Entries.First(e => e.Seat == a).Rating; var eb = goalBoard.Entries.First(e => e.Seat == b).Rating;
            const double beta = 25.0 / 6; var c = Math.Sqrt(ea.Sigma * ea.Sigma + eb.Sigma * eb.Sigma + 2 * beta * beta);
            return Math.Exp(ea.Mu / c) / (Math.Exp(ea.Mu / c) + Math.Exp(eb.Mu / c));
        }
        string Pct(double x) => $"{x * 100:+0.0;-0.0;0.0} pts";

        sb.AppendLine("| id | design | measure | result | verdict |\n|---|---|---|---|---|");
        foreach (var d in Designs)
        {
            var gain = Rate(D(d), r => r.Served) - Rate(Base, r => r.Served);
            var p = TellApart(D(d), Base);
            var pd = Paired(D(d), Base, r => r.Served ? 1 : 0);
            var t1 = gain > 0 && p >= 0.95 ? "holds" : gain <= 0 ? "falls" : "open (not told apart)";
            sb.AppendLine($"| T1 | {d} | goal met against {Base}; leaderboard p | {Pct(gain)}; p = {p:0.00} (paired, not pre-registered: {Pct(pd.Mean)}, 95% [{Pct(pd.Lo)}, {Pct(pd.Hi)}]) | {t1} |");
        }
        foreach (var d in Designs)
        {
            var ratio = Mean(D(d), r => r.Engagement) / Mean(Base, r => r.Engagement);
            sb.AppendLine($"| T2 | {d} | engagement against {Base} (at most 25% less) | {ratio * 100:0}% of it | {(ratio >= 0.75 ? "holds" : "falls")} |");
        }
        double Bait(string p, bool late) => rows.Where(r => r.Policy == p && r.Archetype == "late-scroller" && (late ? r.Session >= sessions - 5 : r.Session < 5)).Average(r => r.BaitPlays);
        var baseFalls = Bait(Base, true) < Bait(Base, false);
        foreach (var d in Designs)
        {
            var falls = Bait(D(d), true) < Bait(D(d), false);
            sb.AppendLine($"| T3 | {d} | late scroller's bait plays, first 5 sessions to last 5 ({Base}: {Bait(Base, false):0.00} to {Bait(Base, true):0.00}) | {Bait(D(d), false):0.00} to {Bait(D(d), true):0.00} | {(falls && !baseFalls ? "holds" : "falls")} |");
        }

        var at10 = learned.Where(l => l.Session == Math.Min(9, sessions - 1)).ToList();
        foreach (var d in Designs)
        {
            var users = at10.Where(l => l.Design == d && l.Archetype is "learner" or "connector" or "late-scroller").GroupBy(l => l.User).ToList();
            var all = users.Count(g => g.All(l => l.Attempted > 0 && Math.Abs(l.Learned - l.TrueLanded) <= 0.15) && Report.Spearman([.. g.Select(l => l.Learned)], [.. g.Select(l => l.TrueLanded)]) >= 0.8);
            var tried = users.Count(g => g.Where(l => l.Attempted >= 3).ToList() is { Count: >= 3 } t && t.All(l => Math.Abs(l.Learned - l.TrueLanded) <= 0.15) && Report.Spearman([.. t.Select(l => l.Learned)], [.. t.Select(l => l.TrueLanded)]) >= 0.8);
            sb.AppendLine($"| T4 | {d} | learner, connector, late scroller after 10 sessions: every kind within 0.15 and rank corr. >= 0.8 | {all} of {users.Count} users on all 7 kinds; {tried} of {users.Count} on the kinds tried 3+ times | {(all == users.Count ? "holds" : "falls")} |");
        }
        foreach (var d in Designs)
        {
            var sound = at10.Where(l => l.Design == d && l.Archetype == "muted-commuter" && l.Attempted >= 3 && l.Kind is "Creator" or "Tutorial" or "Live").ToList();
            var asDislike = sound.Count(l => Math.Abs(l.Learned - l.TrueLanded) < Math.Abs(l.Learned - l.TasteLanded));
            sb.AppendLine($"| T5 | {d} | muted commuter, kinds that need sound: learned nearer the muted truth (a dislike) than the taste | {asDislike} of {sound.Count} beliefs read the context as a dislike | {(sound.Count == 0 ? "open (none tried)" : asDislike * 2 <= sound.Count ? "holds" : "falls")} |");
        }
        foreach (var d in Designs)
        {
            var drift = learned.Where(l => l.Design == d && l.Archetype == "drifter" && l.Session >= 10 && l.Kind is "Tutorial" or "Creator").GroupBy(l => l.User).ToList();
            var lags = drift.Select(g => g.GroupBy(l => l.Session).OrderBy(x => x.Key)
                .FirstOrDefault(x => x.All(l => l.Attempted > 0 && Math.Abs(l.Learned - l.TrueLanded) <= 0.15))?.Key - 10 + 1).ToList();
            var within = lags.Count(x => x is <= 5);
            sb.AppendLine($"| T6 | {d} | drifter: sessions after the change until tutorial and creator are within 0.15 of the new truth (at most 5) | {within} of {lags.Count} users within 5; {lags.Count(x => x is null)} never in {sessions - 10} | {(lags.Count > 0 && within * 2 > lags.Count ? "holds" : "falls")} |");
        }
        foreach (var d in Designs)
        {
            var users = at10.Where(l => l.Design == d && l.Archetype == "noisy").GroupBy(l => l.User).ToList();
            var ok = users.Count(g => g.Where(l => l.Attempted >= 3).All(l => Math.Abs(l.Learned - l.TrueLanded) <= 0.15));
            sb.AppendLine($"| T7 | {d} | noisy user after 10 sessions: no kind tried 3+ times more than 0.15 from its true rate | {ok} of {users.Count} users | {(ok == users.Count ? "holds" : "falls")} |");
        }
        var skim = Paired("last-resort", Base, r => r.SkimPlays);
        var met = Paired("last-resort", Base, r => r.Served ? 1 : 0);
        var t8 = skim.Hi < 0 && met.Lo <= 0 ? "holds" : "falls";
        sb.AppendLine($"| T8 | | last-resort against {Base}: fewer plays while skimming, goal met not higher (paired 95%) | plays while skimming {skim.Mean:+0.00;-0.00} per session [{skim.Lo:+0.00;-0.00}, {skim.Hi:+0.00;-0.00}]; goal met {Pct(met.Mean)} [{Pct(met.Lo)}, {Pct(met.Hi)}] | {t8} |");
        return sb.ToString();
    }
}
