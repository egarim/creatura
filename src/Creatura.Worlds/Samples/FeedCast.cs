using System.Text.Json;
using System.Text.Json.Serialization;
using Creatura;

namespace Creatura.Worlds.Samples;

/// <summary>
/// The synthetic users of the virtual TikTok (docs/design-axi.md, "The synthetic users"): the six archetypes as JSON
/// profiles (Samples/FeedUsers, embedded), seeded variants of them, the checks a user must pass (<see cref="Load"/>
/// refuses an invalid profile; the tests check every variant and that the oracle beats random), and the truth the study
/// compares the recommender's learning with.
/// </summary>
public static class FeedCast
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    /// <summary>The archetypes in a fixed order: the learner, the connector, the late scroller, the muted commuter, the drifter, the noisy one.</summary>
    public static readonly string[] Archetypes = ["learner", "connector", "late-scroller", "muted-commuter", "drifter", "noisy"];

    /// <summary>Seeds for the variants a policy may be tuned on, and for the ones the hypotheses are decided on. Split before anything is recorded.</summary>
    public static readonly IReadOnlyList<int> TrainSeeds = [.. Enumerable.Range(1, 20)];

    public static readonly IReadOnlyList<int> TestSeeds = [.. Enumerable.Range(101, 20)];

    public static FeedUser Parse(string json) => JsonSerializer.Deserialize<FeedUser>(json, Json) ?? throw new FormatException("not a user profile");

    /// <summary>The six archetypes, read from the embedded profiles.</summary>
    public static IReadOnlyList<FeedUser> Load()
    {
        var assembly = typeof(FeedCast).Assembly;
        return [.. Archetypes.Select(id =>
        {
            using var stream = assembly.GetManifestResourceStream($"FeedUsers/{id}.json") ?? throw new InvalidOperationException($"no embedded profile {id}.json");
            var user = Parse(new StreamReader(stream).ReadToEnd());
            return Problems(user) is { Count: > 0 } problems ? throw new FormatException($"{id}.json: {string.Join("; ", problems)}") : user;
        })];
    }

    /// <summary>
    /// A variant of an archetype for one seed: each chance moved by up to 0.1 (bait too), the rest kept. The same seed gives
    /// the same variant; its id says which (<c>learner-s103</c>). Drawn without the session's randomness.
    /// </summary>
    public static FeedUser Variant(FeedUser user, int seed)
    {
        var rng = new Random(unchecked(seed * 92_821 + user.Id.Aggregate(17, (h, c) => h * 31 + c)));
        double Jitter(double p) => p <= 0 ? 0 : Math.Clamp(p + (rng.NextDouble() * 2 - 1) * 0.1, 0.01, 0.99);
        Dictionary<FeedKind, double> Taste(IReadOnlyDictionary<FeedKind, double> t)
        {
            // A flat taste (the noisy user) moves as one, so every variant of it is flat too.
            if (t.Where(kv => kv.Key != FeedKind.Bait).Select(kv => kv.Value).Distinct().Count() == 1)
            {
                var offset = (rng.NextDouble() * 2 - 1) * 0.1;
                return t.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value <= 0 ? 0 : Math.Clamp(kv.Value + offset, 0.01, 0.99));
            }
            return t.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => Jitter(kv.Value));
        }
        return user with
        {
            Id = $"{user.Id}-s{seed}",
            Finish = Taste(user.Finish),
            FinishAfterDrift = user.FinishAfterDrift is null ? null : Taste(user.FinishAfterDrift),
            Bait = Jitter(user.Bait),
        };
    }

    /// <summary>What is wrong with a profile; empty when it is valid.</summary>
    public static IReadOnlyList<string> Problems(FeedUser u)
    {
        var problems = new List<string>();
        void Chance(string name, double p) { if (p is < 0 or > 1 || double.IsNaN(p)) problems.Add($"{name} is {p}, not a chance between 0 and 1"); }
        void Goals(string name, IReadOnlyDictionary<FeedGoal, double> g)
        {
            foreach (var goal in g.Keys.Where(k => !Enum.IsDefined(k))) problems.Add($"{name} has a goal that does not exist: {(int)goal}");
            if (Math.Abs(g.Values.Sum() - 1) > 1e-6) problems.Add($"{name} sum to {g.Values.Sum():F4}, not 1");
            foreach (var (goal, w) in g) Chance($"{name}.{goal}", w);
        }
        void Taste(string name, IReadOnlyDictionary<FeedKind, double> t)
        {
            foreach (var k in TikTokWorld.Kinds.Where(k => !t.ContainsKey(k))) problems.Add($"{name} has no {k}");
            foreach (var (k, p) in t) Chance($"{name}.{k}", p);
        }

        if (string.IsNullOrWhiteSpace(u.Id)) problems.Add("no id");
        Goals("goals", u.Goals);
        Taste("finish", u.Finish);
        Chance("bait", u.Bait);
        Chance("muted", u.Muted);
        Chance("late", u.Late);
        Chance("noise", u.Noise);
        if (u.SkimAfter < 1) problems.Add("skimAfter is below 1");
        if (u.TiredAfter < 1) problems.Add("tiredAfter is below 1");
        if (u.DriftAt is < 1) problems.Add($"driftAt is {u.DriftAt}: a drift starts at session 1 or later");
        if (u.DriftAt is not null && u.FinishAfterDrift is null && u.GoalsAfterDrift is null) problems.Add("driftAt without anything to drift to");
        if (u.DriftAt is null && (u.FinishAfterDrift is not null || u.GoalsAfterDrift is not null)) problems.Add("a taste after drift with no driftAt");
        if (u.FinishAfterDrift is { } f) Taste("finishAfterDrift", f);
        if (u.GoalsAfterDrift is { } g2) Goals("goalsAfterDrift", g2);
        return problems;
    }

    /// <summary>
    /// The truth the learning is compared with: the chance a play of this kind serves the user in a session (it finishes
    /// and the kind serves the session's goal), averaged over the user's goals and over muted and unmuted sessions,
    /// before skimming. Bait never serves. This is what a record's landed rate for the kind should approach.
    /// </summary>
    public static double TrueLandedRate(FeedUser u, FeedKind kind, int session = 0, bool mutedBarrier = true)
    {
        if (kind == FeedKind.Bait) return 0;
        var drifted = u.DriftAt is { } at && session >= at;
        var taste = drifted && u.FinishAfterDrift is { } f ? f : u.Finish;
        var goals = drifted && u.GoalsAfterDrift is { } g ? g : u.Goals;
        var m = mutedBarrier ? u.Muted : 0;
        var p = taste.GetValueOrDefault(kind);
        var finish = TikTokWorld.NeedsSound(kind) ? (1 - m) * Clipped(p, u.Noise) + m * Clipped(p * 0.2, u.Noise) : Clipped(p, u.Noise);
        return goals.Sum(kv => TikTokWorld.Serves(kind, kv.Key) ? kv.Value * finish : 0);
    }

    /// <summary>
    /// The mean of clamp(p + U(-n, n), 0, 1): the chance the world really draws with, noise and its clipping at 0 and 1
    /// included (TikTokWorld.ChanceToFinish). Without clipping it is p.
    /// </summary>
    public static double Clipped(double p, double n)
    {
        if (n <= 0) return Math.Clamp(p, 0, 1);
        // E[clamp(X, 0, 1)] for X uniform on [p - n, p + n]: integrate the clamp piecewise.
        double F(double x) => x <= 0 ? 0 : x >= 1 ? x - 0.5 : x * x / 2;   // the antiderivative of clamp(x, 0, 1)
        return (F(p + n) - F(p - n)) / (2 * n);
    }

    /// <summary>
    /// The same, unmuted: the user's taste for the kind, without the context. The difference from
    /// <see cref="TrueLandedRate"/> is what the muted commuter's context takes away (T5).
    /// </summary>
    public static double TasteLandedRate(FeedUser u, FeedKind kind, int session = 0) => TrueLandedRate(u, kind, session, mutedBarrier: false);

    /// <summary>
    /// One session played by a chooser outside any Creatura loop: a new batch each tick, one play or none, until the user
    /// is served, tired, or <paramref name="maxPlays"/> plays. For checking users, not for the study.
    /// </summary>
    public static bool Session(FeedUser u, int seed, int session, Func<TikTokWorld, Random, string?> choose, IReadOnlyDictionary<string, bool> barriers, int maxPlays = 5)
    {
        var world = new TikTokWorld(barriers, u, seed, session);
        var pick = new Random(unchecked(seed * 31 + session));
        for (var tick = 1; tick <= maxPlays * 4 && world.Plays.Count < maxPlays && !world.Has(TikTokWorld.Served) && !world.Has("user.tired"); tick++)
        {
            world.BeginTick(tick);
            if (choose(world, pick) is { } id) world.Act(id);
        }
        return world.Has(TikTokWorld.Served);
    }

    /// <summary>The oracle's choice: the offered kind most likely to serve this session's goal now, read from the truth; none when nothing offered can.</summary>
    public static string? OracleChoice(TikTokWorld w, Random _) => OracleChoice(w, w.Batch);

    /// <summary>The same among the given kinds only: what the oracle chooses when a loop lets it see fewer.</summary>
    public static string? OracleChoice(TikTokWorld w, IEnumerable<FeedKind> kinds) =>
        kinds.Select(k => (Kind: k, Value: TikTokWorld.Serves(k, w.Goal) ? w.ExpectedChance(k) : 0))
            .Where(x => x.Value > 0).OrderByDescending(x => x.Value).ThenBy(x => x.Kind).Select(x => TikTokWorld.ToolOf(x.Kind)).FirstOrDefault();

    /// <summary>A random play among the kinds offered.</summary>
    public static string? RandomChoice(TikTokWorld w, Random rng)
    {
        var kinds = w.Batch.OrderBy(k => k).ToList();
        return kinds.Count == 0 ? null : TikTokWorld.ToolOf(kinds[rng.Next(kinds.Count)]);
    }

    /// <summary>The share of sessions served within <paramref name="maxPlays"/> plays, over the given seeds and sessions.</summary>
    public static double ServedRate(FeedUser u, Func<TikTokWorld, Random, string?> choose, IReadOnlyDictionary<string, bool> barriers, IEnumerable<int> seeds, int sessions = 10, int maxPlays = 5)
    {
        var results = seeds.SelectMany(seed => Enumerable.Range(0, sessions).Select(s => Session(u, seed, s, choose, barriers, maxPlays))).ToList();
        return results.Count == 0 ? 0 : (double)results.Count(x => x) / results.Count;
    }
}

/// <summary>
/// The oracle as a seat: it reads the user's truth and plays, greedily, the visible kind most likely to serve this
/// session's goal now. A strong reference, not a proven optimum: it does not plan ahead, and the draw can still go against it.
/// </summary>
public sealed class FeedOraclePolicy(TikTokWorld world) : IPolicy
{
    public string Name => "oracle";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var visible = context.Tools.Select(t => TikTokWorld.KindOf(t.Id)).OfType<FeedKind>();
        var choice = FeedCast.OracleChoice(world, visible);
        return Task.FromResult(choice is not null
            ? new PolicySuggestion(choice, "the truth says this serves the goal now")
            : new PolicySuggestion(null, "nothing on offer serves the goal; wait for the next batch"));
    }
}
