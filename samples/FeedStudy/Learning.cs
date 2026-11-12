// The learning study (docs/design-axi.md, slice G, issue #77): a learner seat with goal inference, a factorized user model
// kept in the record, a population prior and discounting, against round 3's configuration and the baselines, on 20 fresh
// variants (seeds 201-220) over 50 sessions. Pre-registered in the note before it was built.
// `dotnet run --project samples/FeedStudy -- --learning [--seeds 20] [--sessions 50] [--out dir]`
using System.Globalization;
using System.Text;
using System.Text.Json;
using Creatura;
using Creatura.Worlds;
using Creatura.Worlds.Samples;

static class LearningStudy
{
    public const double AskBelow = 0.6, Discount = 0.85;
    public const int SeedBase = 201;
    private const int MaxTicks = 20;
    public static readonly string[] Designs = ["levin", "bach"];

    public static async Task<int> Run(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i]] = args[i + 1];
        var seeds = Enumerable.Range(SeedBase, int.Parse(opts.GetValueOrDefault("--seeds", "20"))).ToList();
        var sessions = int.Parse(opts.GetValueOrDefault("--sessions", "50"));
        var outDir = Directory.CreateDirectory(opts.GetValueOrDefault("--out", "feed-learning-out")).FullName;
        var barriers = WorldCatalog.TikTok.Barriers.ToDictionary(b => b.Id, _ => true);
        var cast = FeedCast.Load();
        var store = new InMemoryRecordStore();
        var populations = Designs.ToDictionary(d => d, _ => new Population());
        var rows = new List<SessionRow>();
        var learned = new List<ModelRow>();

        var bare = new (string Name, Func<TikTokWorld, IPolicy> Make)[]
        {
            ("max-staytime", w => new MaxStaytimePolicy(w)),
            ("last-resort", w => new LastResortPolicy(w)),
            ("random", w => new RandomPolicy(w)),
            ("oracle", w => new FeedOraclePolicy(w)),
            ("taste-known", w => new TasteKnownPolicy(w)),
        };

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

                foreach (var design in Designs)
                {
                    // Round 3's configuration, for G2.
                    var w3 = new TikTokWorld(barriers, user, seed, s);
                    var key3 = $"{design}+r3/{user.Id}/{(w3.IsMuted ? "muted" : "sound")}";
                    Fade.Apply(store, key3);
                    await Designed(store, design, w3, new ThompsonSeat(w3, store.Load(key3), unchecked(seed * 104_729 + s)), key3);
                    rows.Add(SessionRow.Of($"{design}+r3", archetype.Id, user.Id, seed, s, w3));

                    // The learner.
                    var wl = new TikTokWorld(barriers, user, seed, s);
                    var key = $"{design}+learner/{user.Id}";
                    var model = UserModel.Load(store, key);
                    model.Decay(Discount);
                    var seat = new LearnerSeat(wl, model, populations[design], unchecked(seed * 15_485_863 + s));
                    await Designed(store, design, wl, seat, key);
                    model.Learn(wl, populations[design]);
                    model.Save(store, key);
                    rows.Add(SessionRow.Of($"{design}+learner", archetype.Id, user.Id, seed, s, wl));
                    if (s == 19 || s == sessions - 1) learned.AddRange(ModelRow.Of(design, archetype.Id, user, s, model, populations[design]));
                }
            }
            Console.Error.Write('.');
        }
        Console.Error.WriteLine();

        File.WriteAllText(Path.Combine(outDir, "sessions.json"), JsonSerializer.Serialize(rows));
        File.WriteAllText(Path.Combine(outDir, "learned.json"), JsonSerializer.Serialize(learned));
        var report = LearningReport.Write(rows, learned, cast, sessions, seeds);
        File.WriteAllText(Path.Combine(outDir, "report-learning.md"), report);
        Console.WriteLine(report);
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

    /// <summary>Round 3's layer: report first, an honest failure recorded and not spent.</summary>
    private static async Task Designed(IRecordStore store, string design, TikTokWorld w, IPolicy seat, string key)
    {
        var options = AgentOptions.With(new FeedNotes(), new RecordCarryExtension(store, key, recordHonestFailures: true));
        var layer = Round2.Layer(design) with { HonestFailureRule = HonestFailureRule.RecordOnly };
        var agent = AgentCatalog.Make(design, new AgentBuild(seat, TikTokWorld.Task, options, 1, layer, Store: store, RecordKey: key));
        await agent.RunAsync(w, MaxTicks);
    }
}

/// <summary>The bare chooser that knows each user's true per-kind landed rate, not the session's goal: the best a per-user record can reach.</summary>
sealed class TasteKnownPolicy(TikTokWorld world) : IPolicy
{
    public string Name => "taste-known";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var best = context.Tools.Where(t => TikTokWorld.KindOf(t.Id) is not null)
            .OrderByDescending(t => FeedCast.TrueLandedRate(world.User, TikTokWorld.KindOf(t.Id)!.Value, world.Session)).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
        return Task.FromResult(new PolicySuggestion(best?.Id, "the true per-kind rate"));
    }
}

/// <summary>Each kind's finish rate pooled over every user a design has met (sessions with sound): lever 3. Held in memory for the run, not in a record.</summary>
sealed class Population
{
    public Dictionary<FeedKind, (double Finished, double Played)> Pool { get; } = [];

    public void Add(FeedKind k, bool finished)
    {
        var (f, n) = Pool.GetValueOrDefault(k);
        Pool[k] = (f + (finished ? 1 : 0), n + 1);
    }

    /// <summary>
    /// The prior for a kind, as pre-registered: the pool's rate worth 4 plays once it holds 50 plays of it, else the feed's
    /// prediction worth 2. Each shape is kept at least 0.01 so a rate of 0 or 1 still gives a valid Beta.
    /// </summary>
    public (double A, double B) Prior(FeedKind k, double predicted)
    {
        var (f, n) = Pool.GetValueOrDefault(k);
        var (rate, weight) = n >= 50 ? (f / n, 4.0) : (predicted, 2.0);
        return (Math.Max(0.01, weight * rate), Math.Max(0.01, weight * (1 - rate)));
    }
}

/// <summary>
/// Lever 2: the user model, kept in the user's record (a part of <see cref="AgentRecord"/>): per kind, finished and played
/// counts in sessions with sound; one muted factor for kinds that need sound; the goal mix.
/// </summary>
sealed class UserModel
{
    public const string Part = "feed:user-model";

    public Dictionary<FeedKind, double> Finished { get; set; } = [];

    public Dictionary<FeedKind, double> Played { get; set; } = [];

    public double MutedFinished { get; set; }

    public double MutedExpected { get; set; }

    public Dictionary<FeedGoal, double> Goals { get; set; } = [];

    /// <summary>The feed's last predicted finish seen for each kind: the prior before the pool fills, outside a session.</summary>
    public Dictionary<FeedKind, double> Predicted { get; set; } = [];

    /// <summary>The prior the seat uses for a kind: the pool's, else the feed's prediction.</summary>
    public (double A, double B) PriorFor(FeedKind k, Population population, double? predicted = null) =>
        population.Prior(k, predicted ?? Predicted.GetValueOrDefault(k, 0.5));

    public static UserModel Load(IRecordStore store, string key) => store.Load(key)?.GetPart<UserModel>(Part) ?? new UserModel();

    public void Save(IRecordStore store, string key)
    {
        var record = store.Load(key) ?? new AgentRecord { Key = key };
        record.SetPart(Part, this);
        store.Save(key, record);
    }

    /// <summary>Lever 4: every count multiplied by the discount, at the start of a session.</summary>
    public void Decay(double g)
    {
        foreach (var k in Finished.Keys.ToList()) Finished[k] *= g;
        foreach (var k in Played.Keys.ToList()) Played[k] *= g;
        foreach (var k in Goals.Keys.ToList()) Goals[k] *= g;
        MutedFinished *= g;
        MutedExpected *= g;
    }

    /// <summary>A kind's finish rate with sound, as the posterior mean under the prior.</summary>
    public double FinishMean(FeedKind k, (double A, double B) prior) =>
        (prior.A + Finished.GetValueOrDefault(k)) / (prior.A + prior.B + Played.GetValueOrDefault(k));

    /// <summary>The muted factor: finishes against expected finishes of sound kinds in muted sessions; prior 0.5 worth 2.</summary>
    public double MutedFactor => (MutedFinished + 1) / (MutedExpected + 2);

    /// <summary>The goal mix, as pre-registered: the counts, normalised; every goal equal while there are none.</summary>
    public double GoalWeight(FeedGoal g) => Goals.Values.Sum() is var sum and > 0 ? Goals.GetValueOrDefault(g) / sum : 1.0 / Enum.GetValues<FeedGoal>().Length;

    /// <summary>
    /// After a session: every play teaches the finish rate (a sound kind in a muted session teaches the muted factor;
    /// bait in a late session teaches nothing); the goal mix learns from the answer when asked, else from the kind that served.
    /// Only what a platform observes is read: the plays, the muted state, the late state, the answer and the served fact.
    /// </summary>
    public void Learn(TikTokWorld w, Population population)
    {
        foreach (var p in w.Plays)
        {
            Predicted[p.Kind] = p.PredictedFinish;
            if (p.Kind == FeedKind.Bait && w.IsLate) continue;
            if (p.Muted)
            {
                // A muted session teaches only the muted factor, through the kinds that need sound; taste is learned with sound.
                if (TikTokWorld.NeedsSound(p.Kind))
                {
                    MutedExpected += FinishMean(p.Kind, PriorFor(p.Kind, population, p.PredictedFinish));
                    MutedFinished += p.Finished ? 1 : 0;
                }
                continue;
            }
            Played[p.Kind] = Played.GetValueOrDefault(p.Kind) + 1;
            Finished[p.Kind] = Finished.GetValueOrDefault(p.Kind) + (p.Finished ? 1 : 0);
            population.Add(p.Kind, p.Finished);
        }

        if (LearnerSeat.Answer(w) is { } said) Goals[said] = Goals.GetValueOrDefault(said) + 1;
        else if (w.Plays.LastOrDefault(p => p.Served) is { } served)
        {
            var goals = Enum.GetValues<FeedGoal>().Where(g => TikTokWorld.Serves(served.Kind, g)).ToList();
            foreach (var g in goals) Goals[g] = Goals.GetValueOrDefault(g) + 1.0 / goals.Count;
        }
    }
}

/// <summary>
/// The learner (lever 1, with 2 to 4 underneath): a belief over this session's goal from the learned goal mix, updated by
/// the plays (a finished play that did not serve rules out its goals) and by the answer when asked; it asks once, first,
/// when no goal has belief 0.6; each kind's value is the belief that it serves × a Thompson draw of its finish rate.
/// </summary>
sealed class LearnerSeat(TikTokWorld world, UserModel model, Population population, int seed) : IPolicy
{
    private readonly Random _rng = new(seed);

    public string Name => "learner";

    /// <summary>The user's answer, as the platform heard it, or null when not asked.</summary>
    public static FeedGoal? Answer(TikTokWorld w) =>
        w.Has("goal.known") && w.Notes().FirstOrDefault(n => n.StartsWith("the user said they want to ", StringComparison.Ordinal)) is { } line
            ? Enum.Parse<FeedGoal>(line["the user said they want to ".Length..], ignoreCase: true) : null;

    public Dictionary<FeedGoal, double> Belief()
    {
        if (Answer(world) is { } said) return Enum.GetValues<FeedGoal>().ToDictionary(g => g, g => g == said ? 1.0 : 0);
        var ruledOut = world.Plays.Where(p => p.Finished && !p.Served)
            .SelectMany(p => Enum.GetValues<FeedGoal>().Where(g => TikTokWorld.Serves(p.Kind, g))).ToHashSet();
        var raw = Enum.GetValues<FeedGoal>().ToDictionary(g => g, g => ruledOut.Contains(g) ? 0 : model.GoalWeight(g));
        var sum = raw.Values.Sum();
        return sum <= 0 ? Enum.GetValues<FeedGoal>().ToDictionary(g => g, _ => 1.0 / 3) : raw.ToDictionary(kv => kv.Key, kv => kv.Value / sum);
    }

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var belief = Belief();
        if (belief.Values.Max() < LearningStudy.AskBelow && world.Plays.Count == 0 && context.Tools.Any(t => t.Id == "ask-what-they-want"))
            return Task.FromResult(new PolicySuggestion("ask-what-they-want", $"unsure of the goal ({belief.Values.Max():0.00})"));

        // Every prediction on offer is observable: the model keeps the last one per kind, played or not.
        foreach (var k in world.Batch) if (world.Predicted(k) is { } seen) model.Predicted[k] = seen.Finish;

        var best = context.Tools.Where(t => TikTokWorld.KindOf(t.Id) is { } k && world.Predicted(k) is not null)
            .Select(t =>
            {
                var k = TikTokWorld.KindOf(t.Id)!.Value;
                var prior = model.PriorFor(k, population, world.Predicted(k)!.Finish);
                var finish = Sampling.Beta(_rng, prior.A + model.Finished.GetValueOrDefault(k), prior.B + model.Played.GetValueOrDefault(k) - model.Finished.GetValueOrDefault(k));
                if (world.IsMuted && TikTokWorld.NeedsSound(k)) finish *= model.MutedFactor;
                var serves = belief.Sum(kv => TikTokWorld.Serves(k, kv.Key) ? kv.Value : 0);
                return (Tool: t, Value: serves * finish);
            })
            .OrderByDescending(x => x.Value).ThenBy(x => x.Tool.Id, StringComparer.Ordinal).FirstOrDefault();
        return Task.FromResult(best.Tool is null
            ? new PolicySuggestion(context.Tools.Any(t => t.Id == "end-session") ? "end-session" : null, "nothing to play")
            : new PolicySuggestion(best.Tool.Id, $"value {best.Value:0.00}"));
    }
}

/// <summary>What a learner's model holds against the truth: a kind's finish rate, the muted factor, a goal's weight.</summary>
sealed record ModelRow(string Design, string Archetype, string User, int Session, string What, double Learned, double Truth, double Evidence = 0)
{
    public static IEnumerable<ModelRow> Of(string design, string archetype, FeedUser user, int session, UserModel model, Population population)
    {
        var drifted = user.DriftAt is { } at && session >= at;
        var taste = drifted && user.FinishAfterDrift is { } f ? f : user.Finish;
        foreach (var k in TikTokWorld.Kinds)
        {
            var truth = FeedCast.Clipped(k == FeedKind.Bait ? user.Bait : taste.GetValueOrDefault(k), user.Noise);
            yield return new ModelRow(design, archetype, user.Id, session, $"finish:{k}", model.FinishMean(k, model.PriorFor(k, population)), truth, model.Played.GetValueOrDefault(k));
        }
        var sound = TikTokWorld.Kinds.Where(TikTokWorld.NeedsSound).ToList();
        var mutedTruth = sound.Sum(k => FeedCast.Clipped(taste.GetValueOrDefault(k) * 0.2, user.Noise)) / sound.Sum(k => FeedCast.Clipped(taste.GetValueOrDefault(k), user.Noise));
        yield return new ModelRow(design, archetype, user.Id, session, "muted", model.MutedFactor, mutedTruth);
        var goals = drifted && user.GoalsAfterDrift is { } g ? g : user.Goals;
        foreach (var goal in Enum.GetValues<FeedGoal>())
            yield return new ModelRow(design, archetype, user.Id, session, $"goal:{goal}", model.GoalWeight(goal), goals.GetValueOrDefault(goal));
    }
}

/// <summary>The learning study's report: the headline, the learning curve against the baseline, and G1 to G9 as pre-registered.</summary>
static class LearningReport
{
    private static string P(double x) => $"{x * 100:0}%";
    private static string Pts(double x) => $"{x * 100:+0.0;-0.0;0.0} pts";

    public static string Write(List<SessionRow> rows, List<ModelRow> learned, IReadOnlyList<FeedUser> cast, int sessions, List<int> seeds)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder($"# The learning study: {cast.Count} archetypes x {seeds.Count} fresh variants (seeds {seeds.First()}-{seeds.Last()}) x {sessions} sessions, every barrier on\n\n");
        var policies = rows.Select(r => r.Policy).Distinct().ToList();
        IEnumerable<SessionRow> Of(string p) => rows.Where(r => r.Policy == p);
        sb.AppendLine("| policy | goal met | engagement reported (s) | bait plays | plays | sessions |\n|---|---|---|---|---|---|");
        foreach (var p in policies.OrderByDescending(p => Of(p).Average(r => r.Served ? 1.0 : 0)))
            sb.AppendLine($"| {p} | {P(Of(p).Average(r => r.Served ? 1.0 : 0))} | {Of(p).Average(r => r.Reported):0.0} | {Of(p).Average(r => r.BaitPlays):0.00} | {Of(p).Average(r => r.Plays):0.0} | {Of(p).Count()} |");

        sb.AppendLine("\n## Goal met by blocks of sessions (drifter left out)\n");
        var blocks = Enumerable.Range(0, (sessions + 4) / 5).Select(b => (From: b * 5, To: Math.Min(sessions, b * 5 + 5))).ToList();
        sb.AppendLine("| policy | " + string.Join(" | ", blocks.Select(b => $"{b.From + 1}-{b.To}")) + " |\n|---" + string.Concat(blocks.Select(_ => "|---")) + "|");
        foreach (var p in policies)
            sb.AppendLine($"| {p} | " + string.Join(" | ", blocks.Select(b => P(Of(p).Where(r => r.Archetype != "drifter" && r.Session >= b.From && r.Session < b.To).Average(r => r.Served ? 1.0 : 0)))) + " |");

        var byKey = rows.ToDictionary(r => (r.Policy, r.User, r.Session));
        (double M, double Lo, double Hi) Ci(List<double> d)
        {
            var m = d.Average(); var sd = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / (d.Count - 1)); var h = 1.96 * sd / Math.Sqrt(d.Count);
            return (m, m - h, m + h);
        }
        List<double> Paired(string a, string b) => [.. Of(a).Select(r => (r.Served ? 1.0 : 0) - (byKey[(b, r.User, r.Session)].Served ? 1.0 : 0))];

        sb.AppendLine("\n## The tests, decided as pre-registered\n\n| id | policy | rule | result | verdict |\n|---|---|---|---|---|");
        foreach (var d in LearningStudy.Designs)
        {
            var L = $"{d}+learner";
            var g1 = Ci(Paired(L, "max-staytime"));
            sb.AppendLine($"| G1 | {L} | goals met above max-staytime (paired 95%) | {Pts(g1.M)} [{Pts(g1.Lo)}, {Pts(g1.Hi)}] | {(g1.Lo > 0 ? "holds" : "falls")} |");
            var g2 = Ci(Paired(L, $"{d}+r3"));
            sb.AppendLine($"| G2 | {L} | goals met above {d}+r3 (paired 95%) | {Pts(g2.M)} [{Pts(g2.Lo)}, {Pts(g2.Hi)}] | {(g2.Lo > 0 ? "holds" : "falls")} |");
            var deltas = Of(L).Where(r => r.Archetype != "drifter").GroupBy(r => r.User).Select(g =>
            {
                double Gap(Func<int, bool> when) => g.Where(r => when(r.Session)).Average(r => (r.Served ? 1.0 : 0) - (byKey[("max-staytime", r.User, r.Session)].Served ? 1.0 : 0));
                return Gap(x => x >= 25) - Gap(x => x < 5);
            }).ToList();
            var g3 = Ci(deltas);
            sb.AppendLine($"| G3 | {L} | per user, the gap to max-staytime in sessions 26-{sessions} minus sessions 1-5 (95%, {deltas.Count} users) | {Pts(g3.M)} [{Pts(g3.Lo)}, {Pts(g3.Hi)}] | {(g3.Lo > 0 ? "holds" : "falls")} |");
            double Late(string p) => Of(p).Where(r => r.Session >= 25).Average(r => r.Served ? 1.0 : 0);
            var ratio = Late(L) / Late("taste-known");
            sb.AppendLine($"| G4 | {L} | sessions 26-{sessions}: at least 80% of taste-known's goals met | {P(Late(L))} against {P(Late("taste-known"))}: {ratio * 100:0}% | {(ratio >= 0.8 ? "holds" : "falls")} |");
        }

        string Share(Func<ModelRow, bool> rowFilter, Func<IGrouping<string, ModelRow>, bool> ok, out double share)
        {
            var users = learned.Where(rowFilter).GroupBy(r => r.User).ToList();
            var pass = users.Count(ok);
            share = users.Count == 0 ? 0 : (double)pass / users.Count;
            return $"{pass} of {users.Count} users";
        }
        foreach (var d in LearningStudy.Designs)
        {
            var last = sessions - 1;
            var t5 = Share(r => r.Design == d && r.Session == last && r.Archetype is "learner" or "connector" or "late-scroller" && r.What.StartsWith("finish:", StringComparison.Ordinal),
                g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15), out var s5);
            sb.AppendLine($"| G5 | {d}+learner | every kind's finish rate within 0.15, learner/connector/late scroller, after {sessions} sessions (80%) | {t5} | {(s5 >= 0.8 ? "holds" : "falls")} |");
            var t6 = Share(r => r.Design == d && r.Session == last && r.Archetype == "muted-commuter" && r.What == "muted",
                g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.1), out var s6);
            var m6 = learned.Where(r => r.Design == d && r.Session == last && r.Archetype == "muted-commuter" && r.What == "muted").ToList();
            sb.AppendLine($"| G6 | {d}+learner | the muted factor within 0.1, muted commuter (80%) | {t6}; learned {m6.Average(r => r.Learned).ToString("0.00", ic)} against {m6.Average(r => r.Truth).ToString("0.00", ic)} | {(s6 >= 0.8 ? "holds" : "falls")} |");
            var t7 = Share(r => r.Design == d && r.Session == 19 && r.Archetype == "drifter" && r.What is "finish:Tutorial" or "finish:Creator",
                g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15), out var s7);
            sb.AppendLine($"| G7 | {d}+learner | drifter: tutorial and creator within 0.15 of the new taste by session 20 (80%) | {t7} | {(s7 >= 0.8 ? "holds" : "falls")} |");
            var t8 = Share(r => r.Design == d && r.Session == last && r.Archetype == "noisy" && r.What.StartsWith("finish:", StringComparison.Ordinal),
                g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15), out var s8);
            sb.AppendLine($"| G8 | {d}+learner | noisy: every kind's finish rate within 0.15 of the flat truth (80%) | {t8} | {(s8 >= 0.8 ? "holds" : "falls")} |");
            var t9 = Share(r => r.Design == d && r.Session == last && r.Archetype is "learner" or "connector" or "late-scroller" or "muted-commuter" && r.What.StartsWith("goal:", StringComparison.Ordinal),
                g => g.All(r => Math.Abs(r.Learned - r.Truth) <= 0.15), out var s9);
            sb.AppendLine($"| G9 | {d}+learner | every goal's learned weight within 0.15 (80%) | {t9} | {(s9 >= 0.8 ? "holds" : "falls")} |");
        }

        // Diagnostics, not pre-registered: they decide nothing.
        var asked = rows.Where(r => r.Policy.EndsWith("+learner", StringComparison.Ordinal)).ToList();
        sb.AppendLine($"\n## Diagnostics (not pre-registered; they decide nothing)\n\nThe learner asked in {P(asked.Average(r => r.Asked ? 1.0 : 0))} of its sessions (first 5: {P(asked.Where(r => r.Session < 5).Average(r => r.Asked ? 1.0 : 0))}; last 5: {P(asked.Where(r => r.Session >= sessions - 5).Average(r => r.Asked ? 1.0 : 0))}).\n");
        sb.AppendLine("| design | finish rates: kinds with 3+ discounted plays | error | kinds with fewer | error |\n|---|---|---|---|---|");
        foreach (var d in LearningStudy.Designs)
        {
            var fin = learned.Where(r => r.Design == d && r.Session == sessions - 1 && r.What.StartsWith("finish:", StringComparison.Ordinal)).ToList();
            var many = fin.Where(r => r.Evidence >= 3).ToList(); var few = fin.Where(r => r.Evidence < 3).ToList();
            string E(List<ModelRow> x) => x.Count == 0 ? "n/a" : x.Average(r => Math.Abs(r.Learned - r.Truth)).ToString("0.00", ic);
            sb.AppendLine($"| {d} | {P((double)many.Count / fin.Count)} of kind-user pairs | {E(many)} | {P((double)few.Count / fin.Count)} | {E(few)} |");
        }

        sb.AppendLine("\n## Learned against true, mean absolute error after the last session\n\n| design | archetype | finish rates | muted factor | goal mix |\n|---|---|---|---|---|");
        foreach (var d in LearningStudy.Designs)
        foreach (var u in cast)
        {
            var at = learned.Where(r => r.Design == d && r.Archetype == u.Id && r.Session == sessions - 1).ToList();
            string E(string prefix) => at.Where(r => r.What.StartsWith(prefix, StringComparison.Ordinal)) is var x && x.Any() ? x.Average(r => Math.Abs(r.Learned - r.Truth)).ToString("0.00", ic) : "n/a";
            sb.AppendLine($"| {d} | {u.Id} | {E("finish:")} | {E("muted")} | {E("goal:")} |");
        }
        return sb.ToString();
    }
}
