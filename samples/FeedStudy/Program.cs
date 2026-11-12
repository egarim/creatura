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
var seeds = FeedCast.TestSeeds.Skip(int.Parse(opts.GetValueOrDefault("--seed-skip", "0"))).Take(seedCount).ToList();   // round 4: --seed-skip 10, seeds 111-120
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

            // Round 2 (docs/design-axi.md, pre-registered): P1 skips are honest and not spent, P2 explore, P3 fade, P4 a record per context.
            var w2 = new TikTokWorld(barriers, user, seed, s);
            var key2 = $"{design}+r2/{user.Id}/{(w2.IsMuted ? "muted" : "sound")}";
            await Designed(design, w2, new ExploringSeat(w2, new MaxStaytimePolicy(w2), store.Load(key2), unchecked(seed * 7_919 + s)), key2, Round2.Layer(design), new FadeExtension(store, key2));
            rows.Add(SessionRow.Of($"{design}+r2", archetype.Id, user.Id, seed, s, w2));
            learned.AddRange(LearnedRow.Of($"{design}+r2", archetype.Id, user, s, store.Load($"{design}+r2/{user.Id}/sound")));

            // Round 3 (pre-registered): skips recorded, not spent (the Core's RecordOnly); a seat that chooses from the record; P3, P4 kept.
            var w3 = new TikTokWorld(barriers, user, seed, s);
            var key3 = $"{design}+r3/{user.Id}/{(w3.IsMuted ? "muted" : "sound")}";
            await Designed(design, w3, new ThompsonSeat(w3, store.Load(key3), unchecked(seed * 104_729 + s)), key3,
                Round2.Layer(design) with { HonestFailureRule = HonestFailureRule.RecordOnly }, new FadeExtension(store, key3), recordHonest: true);
            rows.Add(SessionRow.Of($"{design}+r3", archetype.Id, user.Id, seed, s, w3));
            learned.AddRange(LearnedRow.Of($"{design}+r3", archetype.Id, user, s, store.Load($"{design}+r3/{user.Id}/sound")));
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

async Task Designed(string design, TikTokWorld w, IPolicy seat, string key, ControlLayerOptions? layer = null, IAgentExtension? first = null, bool recordHonest = false)
{
    // The fade (P3) runs before the record is read, so the session starts from the faded record.
    var carry = new RecordCarryExtension(store, key, recordHonestFailures: recordHonest);
    var options = first is null ? AgentOptions.With(new FeedNotes(), carry) : AgentOptions.With(first, new FeedNotes(), carry);
    var agent = AgentCatalog.Make(design, new AgentBuild(seat, TikTokWorld.Task, options, 1, layer, Store: store, RecordKey: key));
    await agent.RunAsync(w, MaxTicks);
}

/// <summary>Round 2's settings that are not code of their own.</summary>
static class Round2
{
    /// <summary>P1: each design's own layer, with a skip judged an honest failure (report first) that is habituated, not spent.</summary>
    public static ControlLayerOptions Layer(string design) => (design switch
    {
        "levin" => AgentCatalog.ColonyLayer,
        "peterson" => AgentCatalog.MapKeeperLayer,
        "orienting" => AgentCatalog.OrientingLayer,
        _ => ControlLayerOptions.Design,
    }) with { JudgeRule = JudgeRule.ReportFirst, HonestFailureRule = HonestFailureRule.Habituate };
}

/// <summary>P2: one play in four (seeded) is the offered kind with the fewest counted plays in the user's record; otherwise the inner seat.</summary>
sealed class ExploringSeat(TikTokWorld world, IPolicy inner, AgentRecord? record, int seed) : IPolicy
{
    private readonly Random _rng = new(seed);

    public string Name => $"explore+{inner.Name}";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var plays = context.Tools.Where(t => TikTokWorld.KindOf(t.Id) is not null).ToList();
        if (plays.Count > 0 && _rng.Next(4) == 0)
        {
            var least = plays.OrderBy(t => record?.Skills.GetValueOrDefault(t.Id)?.Attempted ?? 0).ThenBy(t => t.Id, StringComparer.Ordinal).First();
            return Task.FromResult(new PolicySuggestion(least.Id, "explore: the least-tried kind on offer"));
        }
        return inner.NextAsync(context, cancellationToken);
    }
}

/// <summary>
/// Round 3's seat: Thompson sampling from the record. For each kind on offer, a draw from
/// Beta(1 + 2p + s, 1 + 2(1 - p) + n - s): p the feed's predicted finish (a prior worth two plays), s and n the kind's
/// landed and counted plays in this user's record for this context. The highest draw is played. It never sees the truth
/// or the session's goal.
/// </summary>
sealed class ThompsonSeat(TikTokWorld world, AgentRecord? record, int seed) : IPolicy
{
    private readonly Random _rng = new(seed);

    public string Name => "thompson";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var best = context.Tools.Where(t => TikTokWorld.KindOf(t.Id) is { } k && world.Predicted(k) is not null)
            .Select(t =>
            {
                var p = world.Predicted(TikTokWorld.KindOf(t.Id)!.Value)!.Finish;
                var b = record?.Skills.GetValueOrDefault(t.Id);
                double s = b?.Supported ?? 0, n = b?.Attempted ?? 0;
                return (Tool: t, Draw: Beta(1 + 2 * p + s, 1 + 2 * (1 - p) + n - s));
            })
            .OrderByDescending(x => x.Draw).ThenBy(x => x.Tool.Id, StringComparer.Ordinal).FirstOrDefault();
        return Task.FromResult(best.Tool is null
            ? new PolicySuggestion(context.Tools.Any(t => t.Id == "end-session") ? "end-session" : null, "nothing to play")
            : new PolicySuggestion(best.Tool.Id, $"sampled {best.Draw:0.00}"));
    }

    private double Beta(double a, double b) { var x = Gamma(a); return x / (x + Gamma(b)); }

    /// <summary>Marsaglia and Tsang's method; for a shape below 1, the usual boost.</summary>
    private double Gamma(double shape)
    {
        if (shape < 1) return Gamma(shape + 1) * Math.Pow(_rng.NextDouble(), 1 / shape);
        var d = shape - 1.0 / 3; var c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do { x = Normal(); v = 1 + c * x; } while (v <= 0);
            v = v * v * v; var u = _rng.NextDouble();
            if (u < 1 - 0.0331 * x * x * x * x || Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
        }
    }

    private double Normal() => Math.Sqrt(-2 * Math.Log(1 - _rng.NextDouble())) * Math.Cos(2 * Math.PI * _rng.NextDouble());
}

/// <summary>P3: before a session, a belief with more than 10 counted plays has its counts halved.</summary>
sealed class FadeExtension(IRecordStore store, string key) : IAgentExtension
{
    public string Name => "fade";

    public void BeforeRun(TickContext context)
    {
        if (store.Load(key) is not { } record) return;
        foreach (var (tool, b) in record.Skills.ToList())
            if (b.Attempted > 10) record.Skills[tool] = b with { Supported = b.Supported / 2, Attempted = b.Attempted / 2 };
        store.Save(key, record);
    }
}

/// <summary>A uniform choice among the kinds on offer, seeded by the session.</summary>
sealed class RandomPolicy(TikTokWorld world) : IPolicy
{
    private readonly Random _rng = new(unchecked(world.Session * 7_919 + world.User.Id.Aggregate(17, (h, c) => h * 31 + c)));

    public string Name => "random";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PolicySuggestion(FeedCast.RandomChoice(world, _rng), "random"));
}

/// <summary>One session as the study reads it.</summary>
sealed record SessionRow(string Policy, string Archetype, string User, int Seed, int Session, bool Served, bool Contract,
    double Engagement, double Reported, int Plays, int BaitPlays, bool Skimmed, int SkimPlays, bool Tired, bool Late)
{
    public static SessionRow Of(string policy, string archetype, string user, int seed, int session, TikTokWorld w) => new(
        policy, archetype, user, seed, session, w.Has(TikTokWorld.Served), TikTokWorld.Task.Contract(w) >= 1.0, Math.Round(w.EngagementSeconds, 1), Math.Round(w.EngagementReported, 1),
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
        sb.AppendLine("| policy | goal met | contract kept | engagement reported (s) | watched (s) | bait plays | plays | skimmed | sessions |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var p in policies.OrderByDescending(p => rows.Where(r => r.Policy == p).Average(r => r.Served ? 1.0 : 0)))
        {
            var r = rows.Where(x => x.Policy == p).ToList();
            sb.AppendLine($"| {p} | {P(r.Average(x => x.Served ? 1.0 : 0))} | {P(r.Average(x => x.Contract ? 1.0 : 0))} | {r.Average(x => x.Reported):0.0} | {r.Average(x => x.Engagement):0.0} | {r.Average(x => x.BaitPlays):0.00} | {r.Average(x => x.Plays):0.0} | {P(r.Average(x => x.Skimmed ? 1.0 : 0))} | {r.Count} |");
        }

        sb.AppendLine("\n## Goal met, by archetype\n");
        sb.AppendLine("| policy | " + string.Join(" | ", cast.Select(u => u.Id)) + " |");
        sb.AppendLine("|---" + string.Concat(cast.Select(_ => "|---")) + "|");
        foreach (var p in policies)
            sb.AppendLine($"| {p} | " + string.Join(" | ", cast.Select(u => P(rows.Where(r => r.Policy == p && r.Archetype == u.Id).Average(r => r.Served ? 1.0 : 0)))) + " |");

        // The leaderboards: one game per (user, session), the policies ranked on it.
        var job = (SessionRow r) => $"{r.User}/{r.Session}";
        var goalBoard = Leaderboard.Compute(rows.Select(r => new Run("feed", job(r), DateTimeOffset.UnixEpoch.AddMinutes(r.Session),
            new Dictionary<Axis, string> { [Axis.Design] = r.Policy }, r.Served ? 1 : 0, 0)), Axis.Design);   // T1's measure: goal met, nothing else
        var engagementRuns = rows.Select(r => new Run("feed", job(r), DateTimeOffset.UnixEpoch.AddMinutes(r.Session), new Dictionary<Axis, string> { [Axis.Design] = r.Policy },
            Math.Round(r.Reported / 15), 0));
        sb.AppendLine("\n## Leaderboard on the user's goal (goal met or not)\n\n```");
        sb.Append(Leaderboard.Render(goalBoard));
        sb.AppendLine("```\n\n## Leaderboard on engagement reported (predicted stay of the plays made, in 15 s steps)\n\n```");
        sb.Append(Leaderboard.Render(Leaderboard.Compute(engagementRuns, Axis.Design)));
        sb.AppendLine("```");

        sb.Append(Verdicts.Write(rows, learned, goalBoard, sessions, cast, round: 1));
        sb.Append(Verdicts.Write(rows, learned, goalBoard, sessions, cast, round: 2));
        sb.Append(Verdicts.Write(rows, learned, goalBoard, sessions, cast, round: 3));

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
        var at10 = learned.Where(l => l.Session == 9).ToList();
        foreach (var d in learned.Select(l => l.Design).Distinct())
        foreach (var u in cast)
        {
            var per = at10.Where(l => l.Design == d && l.Archetype == u.Id && l.Attempted >= 3).ToList();
            var users = per.GroupBy(l => l.User).ToList();
            var everyone = at10.Where(l => l.Design == d && l.Archetype == u.Id).Select(l => l.User).Distinct().ToList();
            var cover = everyone.Count == 0 ? 0 : everyone.Average(id => per.Count(l => l.User == id));   // a user with no kind tried 3+ times counts as 0
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

/// <summary>
/// T1 to T8 decided by the rules docs/design-axi.md states: the hypothesis table, and where it cannot be computed as
/// written, the operational rules beside it (set after round 1 was first seen, from codex's review, and marked so).
/// Paired differences with 95% intervals are printed beside them and decide nothing.
/// </summary>
static class Verdicts
{
    private const string Base = "max-staytime";
    private static readonly string[] Designs = ["bach", "levin", "peterson", "orienting", "hybrid"];

    public static string Write(List<SessionRow> rows, List<LearnedRow> learned, Board goalBoard, int sessions, IReadOnlyList<FeedUser> cast, int round)
    {
        var round2 = round >= 2;                                              // rounds 2 and 3 share P4 and P6
        var sb = new StringBuilder(round == 1 ? "\n## Round 1: the hypotheses, decided by the note's rules\n\n" : $"\n## Round {round}: the hypotheses, decided by the rules pre-registered for it\n\n");
        string D(string d) => round2 ? $"{d}+r{round}" : $"{d}+{Base}";
        string L(string d) => round2 ? $"{d}+r{round}" : d;                  // the design's name in the learned rows
        bool Belief(LearnedRow l) => round switch { 1 => l.Attempted > 0, 2 => l.Attempted >= 5, _ => l.Attempted >= 3 };   // P5, and round 3's floor of 3
        double Truth(LearnedRow l) => round2 ? l.TasteLanded : l.TrueLanded;        // P4: the with-sound record against the truth with sound
        IEnumerable<SessionRow> Of(string p, string? archetype = null) => rows.Where(r => r.Policy == p && (archetype is null || r.Archetype == archetype));
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
        string Pts(double x) => $"{x * 100:+0.0;-0.0;0.0} pts";
        double Slope(IEnumerable<(double X, double Y)> points)
        {
            var pts = points.ToList(); var mx = pts.Average(q => q.X); var my = pts.Average(q => q.Y);
            var sxx = pts.Sum(q => (q.X - mx) * (q.X - mx));
            return sxx == 0 ? 0 : pts.Sum(q => (q.X - mx) * (q.Y - my)) / sxx;
        }

        sb.AppendLine("| id | design | rule | result | verdict |\n|---|---|---|---|---|");
        if (round == 3)   // L1, the closed loop: goals met in sessions 16-20 above sessions 1-5, the drifter left out, 95% interval above 0
        {
            foreach (var p in Designs.Select(D).Prepend(Base))
            {
                var early = Of(p).Where(r => r.Archetype != "drifter" && r.Session < 5).Select(r => r.Served ? 1.0 : 0).ToList();
                var late = Of(p).Where(r => r.Archetype != "drifter" && r.Session >= sessions - 5).Select(r => r.Served ? 1.0 : 0).ToList();
                double Var(List<double> x) { var m = x.Average(); return x.Sum(v => (v - m) * (v - m)) / (x.Count - 1); }
                var diff = late.Average() - early.Average(); var h = 1.96 * Math.Sqrt(Var(early) / early.Count + Var(late) / late.Count);
                var decides = p != Base;
                sb.AppendLine($"| L1 | {p} | goals met, sessions {sessions - 4}-{sessions} against 1-5: 95% interval above 0 | {early.Average() * 100:0}% to {late.Average() * 100:0}%: {Pts(diff)} [{Pts(diff - h)}, {Pts(diff + h)}] | {(decides ? diff - h > 0 ? "holds" : "falls" : "(baseline, decides nothing)")} |");
            }
            // L1' (round 4): per user, the paired gap to the baseline in sessions 11-20 minus the gap in sessions 1-2; users as units.
            var baseRows = Of(Base).ToDictionary(r => (r.User, r.Session));
            foreach (var d in Designs)
            {
                var deltas = Of(D(d)).Where(r => r.Archetype != "drifter").GroupBy(r => r.User).Select(g =>
                {
                    double Gap(Func<int, bool> when) => g.Where(r => when(r.Session)).Average(r => (r.Served ? 1.0 : 0) - (baseRows[(r.User, r.Session)].Served ? 1.0 : 0));
                    return Gap(x => x < 2) - Gap(x => x >= 10);
                }).Select(x => -x).ToList();
                var m = deltas.Average(); var sd = Math.Sqrt(deltas.Sum(x => (x - m) * (x - m)) / (deltas.Count - 1)); var h = 1.96 * sd / Math.Sqrt(deltas.Count);
                sb.AppendLine($"| L1' | {d} | per user, the gap to {Base} in sessions 11-20 minus the gap in sessions 1-2: 95% interval above 0 | {Pts(m)} [{Pts(m - h)}, {Pts(m + h)}] over {deltas.Count} users | {(m - h > 0 ? "holds" : "falls")} |");
            }
        }
        foreach (var d in Designs)   // T1: more goals met, and the leaderboard on goals met tells them apart (p >= 0.95)
        {
            var gain = Of(D(d)).Average(r => r.Served ? 1.0 : 0) - Of(Base).Average(r => r.Served ? 1.0 : 0);
            var p = TellApart(D(d), Base); var pd = Paired(D(d), Base, r => r.Served ? 1 : 0);
            if (round2) sb.AppendLine($"| T1 | {d} | goal met more often than {Base}: the paired 95% interval above 0 | {Pts(pd.Mean)} [{Pts(pd.Lo)}, {Pts(pd.Hi)}] | {(pd.Lo > 0 ? "holds" : "falls")} |");
            else sb.AppendLine($"| T1 | {d} | goal met more often than {Base}, told apart by the leaderboard (p >= 0.95) | {Pts(gain)}; p = {p:0.00} (paired {Pts(pd.Mean)} [{Pts(pd.Lo)}, {Pts(pd.Hi)}], decides nothing) | {(gain > 0 && p >= 0.95 ? "holds" : "falls")} |");
        }
        foreach (var d in Designs)   // T2: engagement reported (predicted stay of the plays made) at least 75% of the baseline's
        {
            var ratio = Of(D(d)).Average(r => r.Reported) / Of(Base).Average(r => r.Reported);
            sb.AppendLine($"| T2 | {d} | engagement reported at least 75% of {Base}'s | {ratio * 100:0}% (seconds watched: {Of(D(d)).Average(r => r.Engagement) / Of(Base).Average(r => r.Engagement) * 100:0}%) | {(ratio >= 0.75 ? "holds" : "falls")} |");
        }
        double BaitSlope(string p) => Slope(Of(p, "late-scroller").GroupBy(r => r.Session).Select(g => ((double)g.Key, g.Average(r => r.BaitPlays))));
        var baseSlope = BaitSlope(Base);
        foreach (var d in Designs)   // T3: round 1, a falling trend and a baseline not falling; round 2 (P6), below 0 and below the baseline's slope
        {
            var slope = BaitSlope(D(d));
            var holds = round2 ? slope < 0 && slope < baseSlope : slope < 0 && baseSlope >= 0;
            sb.AppendLine($"| T3 | {d} | late scroller's bait plays per session: slope < 0 and {(round2 ? "below" : "the baseline's not falling,")} {Base}'s ({baseSlope:+0.000;-0.000}) | slope {slope:+0.000;-0.000} a session | {(holds ? "holds" : "falls")} |");
        }

        if (sessions < 10)
        {
            sb.AppendLine("| T4-T7 | | need 10 sessions | not run | not run |");
        }
        else
        {
            var at10 = learned.Where(l => l.Session == 9).ToList();
            foreach (var d in Designs)   // T4: every kind within 0.15 and rank corr. >= 0.8, for every learner, connector and late scroller
            {
                var users = at10.Where(l => l.Design == L(d) && l.Archetype is "learner" or "connector" or "late-scroller").GroupBy(l => l.User).ToList();
                var ok = users.Count(g => g.All(l => Belief(l) && Math.Abs(l.Learned - Truth(l)) <= 0.15) && Report.Spearman([.. g.Select(l => l.Learned)], [.. g.Select(Truth)]) >= 0.8);
                var tried = users.Average(g => g.Count(l => Belief(l)));
                sb.AppendLine($"| T4 | {d} | after 10 sessions every kind within 0.15 of true, rank corr. >= 0.8, for every such user | {ok} of {users.Count} users; kinds with any belief: {tried:0.0} of 7 | {(ok == users.Count ? "holds" : "falls")} |");
            }
            foreach (var d in Designs)   // T5 (operational): no belief about a kind that needs sound is nearer the muted truth than the taste
            {
                var beliefs = at10.Where(l => l.Design == L(d) && l.Archetype == "muted-commuter" && Belief(l) && l.Kind is "Creator" or "Tutorial" or "Live").ToList();
                var dislike = beliefs.Count(l => Math.Abs(l.Learned - l.TrueLanded) < Math.Abs(l.Learned - l.TasteLanded));
                var never = beliefs.Count(l => l.Learned >= l.TasteLanded);
                sb.AppendLine($"| T5 | {d} | muted commuter after 10 sessions: no belief about a kind that needs sound nearer the muted truth than the taste | {dislike} of {beliefs.Count} beliefs read the context as a dislike; {never} sit at or above the taste | {(dislike == 0 ? beliefs.Count == 0 ? "holds (no belief at all)" : "holds" : "falls")} |");
            }
            foreach (var d in Designs)   // T6 (operational): within 5 sessions of the change, every belief within 0.15 of the new truth, for every drifter
            {
                var driftAt = cast.Single(u => u.Id == "drifter").DriftAt ?? 0;
                var users = learned.Where(l => l.Design == L(d) && l.Archetype == "drifter" && l.Session >= driftAt).GroupBy(l => l.User).ToList();
                var lags = users.Select(g => g.GroupBy(l => l.Session).OrderBy(x => x.Key)
                    .FirstOrDefault(x => x.Where(l => Belief(l)).All(l => Math.Abs(l.Learned - Truth(l)) <= 0.15))?.Key - driftAt + 1).ToList();
                var within = lags.Count(x => x is <= 5);
                sb.AppendLine($"| T6 | {d} | drifter: within 5 sessions after the change every belief within 0.15 of the new truth, for every drifter | {within} of {lags.Count} users; {lags.Count(x => x is null)} never in {sessions - driftAt} sessions | {(lags.Count > 0 && within == lags.Count ? "holds" : "falls")} |");
            }
            foreach (var d in Designs)   // T7: after 10 sessions no belief more than 0.15 from its true rate, for every noisy user
            {
                var users = at10.Where(l => l.Design == L(d) && l.Archetype == "noisy").GroupBy(l => l.User).ToList();
                var none = users.Count(g => g.All(l => !Belief(l)));
                var ok = users.Count(g => g.Where(l => Belief(l)).All(l => Math.Abs(l.Learned - Truth(l)) <= 0.15));
                sb.AppendLine($"| T7 | {d} | noisy users after 10 sessions: no belief more than 0.15 from its true rate | {ok} of {users.Count} users{(none > 0 ? $" ({none} with no belief at all)" : "")} | {(ok == users.Count ? "holds" : "falls")} |");
            }
        }
        // T8 (operational for "skimming"): fewer plays while skimming, and goals met not higher (paired means; intervals decide nothing)
        var skim = Paired("last-resort", Base, r => r.SkimPlays);
        var met = Paired("last-resort", Base, r => r.Served ? 1 : 0);
        sb.AppendLine($"| T8 | | last-resort against {Base}: fewer plays while skimming, and goals met not higher | plays while skimming {skim.Mean:+0.00;-0.00} a session [{skim.Lo:+0.00;-0.00}, {skim.Hi:+0.00;-0.00}]; goals met {Pts(met.Mean)} [{Pts(met.Lo)}, {Pts(met.Hi)}] | {(skim.Mean < 0 && met.Mean <= 0 ? "holds" : "falls")} |");
        return sb.ToString();
    }
}
