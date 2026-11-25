using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Creatura.Graph.Bench;

/// <summary>One arm on one variant of one scenario: the answer, whether it was right, what the retrieval surfaced, what it cost, and every rule it broke.</summary>
public sealed record ScenarioResult(
    string Scenario,
    string Title,
    string Hypothesis,
    string Arm,
    int Variant,
    int Seed,
    string? Answer,
    string? Expected,
    bool AnswerCorrect,
    int RequiredTotal,
    int RequiredFound,
    int ForbiddenHits,
    int ForbiddenTextHits,
    ArmCost Cost,
    IReadOnlyList<string> Retrieved)
{
    /// <summary>How much of the evidence the answer needed actually arrived. An answer that is right without it is a guess that happened to land.</summary>
    public double EvidenceRecall => RequiredTotal == 0 ? 1.0 : (double)RequiredFound / RequiredTotal;

    public int Violations => ForbiddenHits + ForbiddenTextHits;

    /// <summary>
    /// The pre-registered pass rule: the right answer, all of the evidence that answer needed, and nothing forbidden
    /// surfaced. Requiring the evidence as well as the answer is what stops an arm from passing by luck, and requiring
    /// zero violations is what stops an arm from passing by leaking.
    /// </summary>
    public bool Passed => AnswerCorrect && EvidenceRecall >= 1.0 && Violations == 0;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One line of the raw result file. Redacted before it is written, because a result file leaves the machine.</summary>
    public string ToJsonl()
    {
        var safe = this with
        {
            Answer = Redact(Answer),
            Expected = Redact(Expected),
            Retrieved = [.. Retrieved.Select(Cells.Scrubber.Scrub)],
        };
        return JsonSerializer.Serialize(safe, Json);

        static string? Redact(string? text) => text is null ? null : Cells.Scrubber.Scrub(text);
    }
}

/// <summary>What a run is told to do before it is told what it found.</summary>
public sealed record RunOptions
{
    /// <summary>How many name-permuted variants of each scenario to run. 1 runs the fixture as written.</summary>
    public int Variants { get; init; } = 1;

    /// <summary>The published seed. Every variant of every scenario is derived from it and from nothing else.</summary>
    public int Seed { get; init; } = 20261125;

    /// <summary>Only these scenario ids; all of them when null.</summary>
    public IReadOnlyList<string>? Only { get; init; }

    /// <summary>Only these arm names; all four when null.</summary>
    public IReadOnlyList<string>? Arms { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>
/// Runs the scenarios against the four arms and scores them. Every arm is built fresh per scenario, handed the same
/// corpus, asked the same question under the same budget, and scored by the same oracle and the same answerer. The
/// only thing that differs between arms is the memory.
/// </summary>
public static class Runner
{
    public static IReadOnlyList<ScenarioResult> Run(RunOptions? options = null)
    {
        var run = options ?? new RunOptions();
        var scenarios = Scenarios.All.Where(s => run.Only is null || run.Only.Contains(s.Id, StringComparer.OrdinalIgnoreCase));
        var results = new List<ScenarioResult>();
        foreach (var scenario in scenarios)
        for (var variant = 0; variant < Math.Max(1, run.Variants); variant++)
        {
            var seed = run.Seed + variant * 101;
            var case_ = variant == 0 ? scenario : Variants.Of(scenario, variant, seed);
            foreach (var arm in Arms.All(Scenarios.Caller))
            {
                if (run.Arms is not null && !run.Arms.Contains(arm.Name, StringComparer.OrdinalIgnoreCase)) continue;
                results.Add(One(case_, arm, variant, seed));
                run.Log?.Invoke($"{case_.Id} v{variant} {arm.Name,-9} {(results[^1].Passed ? "pass" : "FAIL")} {results[^1].Answer ?? "-"}");
            }
        }

        return results;
    }

    /// <summary>One arm on one scenario: learn the corpus, answer the question, score it against the oracle.</summary>
    public static ScenarioResult One(GraphScenario scenario, IMemoryArm arm, int variant, int seed)
    {
        arm.Learn(scenario.Corpus);
        var retrieved = arm.Retrieve(scenario.Question, scenario.Budget);
        var answer = Answerer.Solve(scenario.Question, retrieved);
        var ids = retrieved.Select(r => r.StatementId).ToList();
        var oracle = scenario.Oracle;
        var required = oracle.Required.Count(id => ids.Contains(id));
        var forbidden = oracle.Forbidden.Count(id => ids.Contains(id));
        var text = string.Join("\n", retrieved.Select(r => r.Text));
        var forbiddenText = oracle.ForbiddenText.Count(t => text.Contains(t, StringComparison.Ordinal));
        return new ScenarioResult(
            scenario.Id, scenario.Title, scenario.Hypothesis, arm.Name, variant, seed,
            answer, oracle.ExpectedAnswer,
            string.Equals(answer, oracle.ExpectedAnswer, StringComparison.Ordinal),
            oracle.Required.Count, required, forbidden, forbiddenText,
            arm.LastCost, ids);
    }

    /// <summary>Writes the raw results as JSONL, one line per run, and returns the path.</summary>
    public static string WriteJsonl(IReadOnlyList<ScenarioResult> results, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var sb = new StringBuilder();
        foreach (var result in results) sb.Append(result.ToJsonl()).Append('\n');
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}

/// <summary>
/// A variant of a scenario, made by permuting the names the fixture says are interchangeable. The seed is published
/// and nothing else is random, so a variant is reproducible from the scenario and the seed alone.
/// <para>
/// This is a weaker form of variant than an independently generated corpus, and the report says so: permuting names
/// checks that a result is not an artifact of one entity's spelling, and does not check that it survives a different
/// corpus shape. The effective sample size stays the number of scenarios, not scenarios times variants.
/// </para>
/// </summary>
public static class Variants
{
    /// <summary>The scenario with its interchangeable names permuted. Variant 0 is the fixture as written.</summary>
    public static GraphScenario Of(GraphScenario scenario, int variant, int seed)
    {
        if (variant == 0 || scenario.NameGroups.Count == 0) return scenario;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var random = new Random(seed);
        foreach (var group in scenario.NameGroups)
        {
            var shuffled = group.OrderBy(_ => random.Next()).ToList();
            // A shuffle that happened to leave the order alone is not a variant, so rotate it: every variant has to
            // differ from the fixture, or it is the fixture counted twice.
            if (shuffled.Count > 1 && shuffled.SequenceEqual(group, StringComparer.Ordinal))
                shuffled = [.. group.Skip(1), group[0]];
            for (var i = 0; i < group.Count; i++)
                if (!string.Equals(group[i], shuffled[i], StringComparison.Ordinal)) map[group[i]] = shuffled[i];
        }

        if (map.Count == 0) return scenario;
        var pattern = Names(map.Keys);
        return scenario with
        {
            Version = $"{scenario.Version}+v{variant}",
            Corpus = [.. scenario.Corpus.Select(s => s with { Text = pattern.Replace(s.Text, m => map[m.Value]) })],
            Question = new Question(pattern.Replace(scenario.Question.Ask, m => map[m.Value]), scenario.Question.Kind)
            {
                Start = Replace(scenario.Question.Start),
                Relations = scenario.Question.Relations,
                At = scenario.Question.At,
                Options = [.. scenario.Question.Options.Select(Replace)],
                Requires = scenario.Question.Requires,
                Themes = scenario.Question.Themes,
            },
            Oracle = new ScenarioOracle(scenario.Oracle.ExpectedAnswer is { } expected ? Replace(expected) : null)
            {
                Required = scenario.Oracle.Required,
                Forbidden = scenario.Oracle.Forbidden,
                ForbiddenText = scenario.Oracle.ForbiddenText,
                UncertaintyMatters = scenario.Oracle.UncertaintyMatters,
            },
        };

        string Replace(string text) => pattern.Replace(text, m => map[m.Value]);
    }

    /// <summary>One pass over every name at once, so that swapping two names does not swap one of them twice. Longest first, so a name that contains another is matched whole.</summary>
    private static Regex Names(IEnumerable<string> names) =>
        new(string.Join("|", names.OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).Select(Regex.Escape)));
}

/// <summary>The aggregate the report prints, over whatever rows it is given.</summary>
public static class Summary
{
    /// <summary>One line per arm: how often it passed, how much evidence it found, how many rules it broke, and what it cost.</summary>
    public static IReadOnlyList<ArmSummary> ByArm(IReadOnlyList<ScenarioResult> results) =>
        results.GroupBy(r => r.Arm)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ArmSummary(
                g.Key,
                g.Count(),
                g.Count(r => r.Passed),
                g.Count(r => r.Passed) / (double)g.Count(),
                g.Count(r => r.AnswerCorrect) / (double)g.Count(),
                g.Average(r => r.EvidenceRecall),
                g.Sum(r => r.Violations),
                g.Average(r => r.Cost.LearnMilliseconds),
                g.Average(r => r.Cost.RetrieveMilliseconds),
                g.Average(r => r.Cost.Characters),
                g.Average(r => r.Cost.Items),
                g.Max(r => r.Cost.MaxDepth)))
            .ToList();

    /// <summary>One line per scenario per arm, because an aggregate over fourteen different questions means very little.</summary>
    public static IReadOnlyList<CellSummary> ByScenario(IReadOnlyList<ScenarioResult> results) =>
        results.GroupBy(r => (r.Scenario, r.Arm))
            .OrderBy(g => g.Key.Scenario, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Arm, StringComparer.Ordinal)
            .Select(g => new CellSummary(g.Key.Scenario, g.Key.Arm, g.Count(), g.Count(r => r.Passed),
                g.Average(r => r.EvidenceRecall), g.Sum(r => r.Violations),
                g.Average(r => r.Cost.LearnMilliseconds + r.Cost.RetrieveMilliseconds),
                g.Average(r => r.Cost.Characters)))
            .ToList();

    /// <summary>The rows where an arm broke a rule, with which rule it broke. Failures are reported beside successes.</summary>
    public static IReadOnlyList<ScenarioResult> Violations(IReadOnlyList<ScenarioResult> results) =>
        [.. results.Where(r => r.Violations > 0).OrderBy(r => r.Scenario, StringComparer.Ordinal).ThenBy(r => r.Arm, StringComparer.Ordinal)];
}

public sealed record ArmSummary(
    string Arm, int Runs, int Passed, double PassRate, double AnswerRate, double EvidenceRecall,
    int Violations, double LearnMilliseconds, double RetrieveMilliseconds, double Characters, double Items, int MaxDepth);

public sealed record CellSummary(
    string Scenario, string Arm, int Runs, int Passed, double EvidenceRecall, int Violations,
    double Milliseconds, double Characters);
