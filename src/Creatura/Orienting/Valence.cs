namespace Creatura.Orienting;

/// <summary>
/// Peterson's four kinds of valence (design-sokolov.md, claim 4), plus relief, the absence of an expected
/// punishment. Positive and negative are separate channels, so an outcome can carry more than one kind.
/// </summary>
[Flags]
public enum ValenceKind
{
    None = 0,

    /// <summary>A consummatory reward: a goal fact landed. It satiates the drive it served.</summary>
    Satisfaction = 1,

    /// <summary>An incentive reward, a cue of satisfaction: a step toward a goal landed. It draws approach.</summary>
    Promise = 2,

    /// <summary>A failure, a lie, or an expected reward that did not come. It stops action; the behaviour extinguishes.</summary>
    Punishment = 4,

    /// <summary>A cue of punishment: a fact the world uses to signal trouble, or a tool that punished before. It pauses.</summary>
    Threat = 8,

    /// <summary>An expected punishment that did not come: counts as a reward (claim 4's cross-rule).</summary>
    Relief = 16,
}

/// <summary>
/// What a world says its facts mean, as data: which facts are goals, which are steps toward them (and toward which
/// goal), and which signal trouble. The framework design moves this into the world's manifest; here it is passed in.
/// A goal fact is also a step for any goal above it (claim 8: reaching a goal at one level counts as a reward one level up).
/// </summary>
public sealed record ValenceTable(
    IReadOnlySet<string> Goals,
    IReadOnlyDictionary<string, string> StepToward,
    IReadOnlySet<string> Trouble)
{
    public static ValenceTable Empty { get; } = new(new HashSet<string>(), new Dictionary<string, string>(), new HashSet<string>());

    /// <summary>
    /// A table read off the tool catalogue, for a task that states none: a goal is a fact some tool grants and no tool
    /// requires (a terminal fact); a step is a fact granted by a tool from which a goal can be reached through the
    /// catalogue, toward the first goal found; trouble is a fact some tool requires, no tool grants and the world did
    /// not start with (the signal facts a failure leaves behind). A task that names its goal predicate has every goal
    /// fact in it; the inference cannot read the predicate, so a world whose goal is not a terminal fact should state
    /// its table.
    /// </summary>
    public static ValenceTable Infer(IEnumerable<Tool> tools, IReadOnlySet<string> initial)
    {
        var list = tools.ToList();
        var granted = list.SelectMany(t => t.Grants).ToHashSet(StringComparer.Ordinal);
        var required = list.SelectMany(t => t.Requires).ToHashSet(StringComparer.Ordinal);
        var goals = granted.Where(f => !required.Contains(f)).ToHashSet(StringComparer.Ordinal);
        var trouble = required.Where(f => !granted.Contains(f) && !initial.Contains(f)).ToHashSet(StringComparer.Ordinal);

        var steps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in granted.Where(f => !goals.Contains(f)).Order(StringComparer.Ordinal))
        {
            // Forward through the catalogue: the tools this fact enables, what they grant, until a goal.
            var seen = new HashSet<string>(StringComparer.Ordinal) { fact };
            var frontier = new Queue<string>([fact]);
            string? goal = null;
            while (frontier.Count > 0 && goal is null)
            {
                var f = frontier.Dequeue();
                foreach (var next in list.Where(t => t.Requires.Contains(f)).SelectMany(t => t.Grants).Order(StringComparer.Ordinal))
                {
                    if (goals.Contains(next))
                    {
                        goal = next;
                        break;
                    }

                    if (seen.Add(next)) frontier.Enqueue(next);
                }
            }

            if (goal is not null) steps[fact] = goal;
        }

        return new ValenceTable(goals, steps, trouble);
    }
}

/// <summary>One outcome, classified. <see cref="Drives"/> names the goals each kind applies to.</summary>
/// <param name="PunishmentWeight">
/// How hard a punishment lands, 0 to 1: an expected reward that did not come is frustration and lands hardest (1.0),
/// a lie from a tool not yet classified less (0.75), an honest failure from one least (0.5). This is the
/// mechanism behind hypothesis S5: the absence of an expected reward is itself a punishment.
/// </param>
public sealed record ValenceReading(ValenceKind Kinds, IReadOnlyDictionary<ValenceKind, IReadOnlyList<string>> Drives, double PunishmentWeight = 0)
{
    public bool Has(ValenceKind kind) => (Kinds & kind) == kind;

    public override string ToString() => Kinds == ValenceKind.None ? "neutral" : Kinds.ToString().ToLowerInvariant();
}

/// <summary>
/// Classify an outcome against what the agent wants, not against what it predicted (claim 3). The inputs are the
/// registry before and after the action, whether the tool reported success, what the agent expected of the tool
/// (a reward, a punishment, or nothing yet), and the table. The rules, in the design's order:
/// a goal fact landing is a satisfaction for that goal and a promise for the goal above it; a step fact landing is a
/// promise for its goal; a report of success with nothing gained, a failure, or an expected reward that did not come
/// is a punishment; a trouble fact landing is a threat; a tool expected to punish that delivered is relief.
/// </summary>
public static class Valence
{
    public enum Expectation
    {
        Unclassified,
        Reward,
        Punishment,
    }

    public static ValenceReading Classify(
        IReadOnlySet<string> before,
        IReadOnlySet<string> after,
        bool reportedSuccess,
        Expectation expected,
        ValenceTable table)
    {
        var gained = after.Where(f => !before.Contains(f)).ToList();
        var drives = new Dictionary<ValenceKind, List<string>>();
        void Add(ValenceKind k, string goal)
        {
            if (!drives.TryGetValue(k, out var list)) drives[k] = list = [];
            if (!list.Contains(goal, StringComparer.Ordinal)) list.Add(goal);
        }

        foreach (var fact in gained)
        {
            if (table.Goals.Contains(fact))
            {
                Add(ValenceKind.Satisfaction, fact);
                if (table.StepToward.TryGetValue(fact, out var parent)) Add(ValenceKind.Promise, parent);
            }
            else if (table.StepToward.TryGetValue(fact, out var goal))
            {
                Add(ValenceKind.Promise, goal);
            }

            if (table.Trouble.Contains(fact)) Add(ValenceKind.Threat, fact);
        }

        var delivered = gained.Any(f => !table.Trouble.Contains(f));
        var weight = 0.0;
        if (!delivered)
        {
            // Nothing the agent wanted came: a lie (success reported, nothing gained), a failure, or the absence of an
            // expected reward. All punish; frustration hardest, then a lie, then an honest failure.
            Add(ValenceKind.Punishment, "");
            weight = expected == Expectation.Reward ? 1.0 : reportedSuccess ? 0.75 : 0.5;
        }
        else if (expected == Expectation.Punishment)
        {
            Add(ValenceKind.Relief, "");
        }

        var kinds = drives.Keys.Aggregate(ValenceKind.None, (a, k) => a | k);
        return new ValenceReading(kinds, drives.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value), weight);
    }
}
