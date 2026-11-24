using System.Globalization;
using System.Text;
using Creatura.Bench;

namespace Creatura.Graph.Bench;

/// <summary>A hypothesis scored: the verdict, and the numbers that decided it. A verdict without its numbers is not checkable.</summary>
public sealed record GraphVerdict(HypothesisVerdict Verdict, string Finding)
{
    public static GraphVerdict Held(string finding) => new(HypothesisVerdict.Held, finding);

    public static GraphVerdict Failed(string finding) => new(HypothesisVerdict.Failed, finding);

    public static GraphVerdict NotRun(string finding) => new(HypothesisVerdict.NotRun, finding);
}

/// <summary>
/// A pre-registered hypothesis: what it predicts, what would refute it, the numeric threshold that decides between
/// the two, and the scorer. All four are written before any run, because choosing a threshold after seeing the
/// numbers is how an experiment ends up reporting what its author wanted.
/// </summary>
public sealed record GraphHypothesis(
    string Id,
    string Statement,
    string Falsifier,
    string Threshold,
    Func<IReadOnlyList<ScenarioResult>, GraphVerdict> Score);

/// <summary>The pre-registered pass rule and thresholds, in one place so the report can quote them rather than paraphrase them.</summary>
public static class Preregistration
{
    /// <summary>An arm passes a scenario when the answer is right, every statement the answer needed was retrieved, and nothing forbidden was.</summary>
    public const string PassRule = "answer correct AND evidence recall 1.0 AND zero violations";

    /// <summary>How much better an arm has to be, in absolute pass rate, for "improves" to be true.</summary>
    public const double ImprovementThreshold = 0.25;

    /// <summary>How much less context an arm has to hand back for "reduces tokens" to be true.</summary>
    public const double ContextReduction = 0.10;

    /// <summary>The gate from issue #110.</summary>
    public const string DecisionGate = "zero critical security violations in tested fixtures";

    /// <summary>Which scenarios are the critical security fixtures.</summary>
    public static readonly IReadOnlyList<string> SecurityScenarios = ["G09", "G10"];

    /// <summary>Whether the run clears the gate: no arm may surface forbidden knowledge in a security fixture.</summary>
    public static bool GateCleared(IReadOnlyList<ScenarioResult> results) =>
        !results.Any(r => SecurityScenarios.Contains(r.Scenario, StringComparer.Ordinal) && r.ForbiddenHits > 0);

    /// <summary>Every security violation in the run, named. Reported whether or not the gate cleared, because a cleared gate that was nearly missed is worth knowing about.</summary>
    public static IReadOnlyList<ScenarioResult> SecurityViolations(IReadOnlyList<ScenarioResult> results) =>
        [.. results.Where(r => SecurityScenarios.Contains(r.Scenario, StringComparer.Ordinal) && r.Violations > 0)
            .OrderBy(r => r.Scenario, StringComparer.Ordinal).ThenBy(r => r.Arm, StringComparer.Ordinal)];
}

/// <summary>
/// The thirteen hypotheses of issue #109. H08 and H12 need two different language models and are registered as not
/// run rather than quietly dropped: an untested hypothesis that disappears from the report is a hypothesis that was
/// quietly refuted by inconvenience.
/// </summary>
public static class GraphHypotheses
{
    private const string Graph = "C graph";
    private const string Text = "B text";
    private const string Hybrid = "D hybrid";
    private const string None = "A none";

    public static IReadOnlyList<GraphHypothesis> All { get; } =
    [
        new("H01",
            "Structured relationships improve multi-hop reasoning: the graph arm answers the multi-hop scenarios more often than the text arm does.",
            "the graph arm's pass rate on G01 and G12 is not far enough above the text arm's, so the relationships carried nothing the keywords did not",
            Improvement("C", "B", "G01", "G12"),
            rows => Beats(rows, Graph, Text, "G01", "G12")),

        new("H02",
            "Episodic memory reduces repeated mistakes: the graph arm diagnoses the new incident instead of repeating the previous incident's answer.",
            "the graph arm does not beat the text arm on G02, or it repeats the previous cause",
            Improvement("C", "B", "G02"),
            rows => Beats(rows, Graph, Text, "G02")),

        new("H03",
            "Temporal relationships improve reasoning about changing environments: the graph arm names whoever held it at the moment asked for.",
            "the graph arm does not beat the text arm on G03, so the validity window carried nothing",
            Improvement("C", "B", "G03"),
            rows => Beats(rows, Graph, Text, "G03")),

        new("H04",
            "Explicit task dependencies improve execution planning: the graph arm refuses the premature deployment.",
            "the graph arm does not beat the text arm on G05",
            Improvement("C", "B", "G05"),
            rows => Beats(rows, Graph, Text, "G05")),

        new("H05",
            "Shared knowledge improves collaboration: an agent resumes from another's verified note instead of redoing the work.",
            "the graph arm does not beat the memoryless arm on G06, so the handed-over knowledge went unused",
            Improvement("C", "A", "G06"),
            rows => Beats(rows, Graph, None, "G06")),

        new("H06",
            "Structured environmental knowledge improves tool selection: the graph arm picks the tool that actually provides the capability.",
            "the graph arm does not beat the text arm on G07",
            Improvement("C", "B", "G07"),
            rows => Beats(rows, Graph, Text, "G07")),

        new("H07",
            "Cognitive graphs reduce unnecessary token consumption: the graph arm hands back less context for the same or better answers.",
            "the graph arm's mean retrieved context is not far enough below the text arm's, so the structure bought a precision it did not deliver",
            $"chars(C) <= chars(B) x (1 - {Preregistration.ContextReduction.ToString("0.00", CultureInfo.InvariantCulture)}) over every scenario",
            rows =>
            {
                var graph = Mean(rows, Graph, r => r.Cost.Characters);
                var text = Mean(rows, Text, r => r.Cost.Characters);
                if (graph is null || text is null || text.Value <= 0)
                    return GraphVerdict.NotRun("no rows for both arms");
                var reduction = 1.0 - graph.Value / text.Value;
                return Decide(reduction >= Preregistration.ContextReduction,
                    $"the graph arm hands back {graph.Value:F0} characters against the text arm's {text.Value:F0}: {reduction:P1} fewer, threshold {Preregistration.ContextReduction:P0}");
            }),

        new("H08",
            "A smaller model with structured memory outperforms a larger model without it on selected tasks.",
            "the smaller model with the graph does not beat the larger model without it on the scenarios both run",
            "needs two model seats of different size; the deterministic harness runs neither",
            _ => GraphVerdict.NotRun("not run: needs two model seats of different size")),

        new("H09",
            "Hybrid graph and text retrieval outperforms either on its own.",
            "the hybrid arm's overall pass rate is below the better of the two arms it combines, so combining them cost more than it added",
            "pass(D) >= max(pass(B), pass(C)) over every scenario",
            rows =>
            {
                var hybrid = PassRate(rows, Hybrid);
                var text = PassRate(rows, Text);
                var graph = PassRate(rows, Graph);
                if (hybrid is null || text is null || graph is null) return GraphVerdict.NotRun("no rows for all three arms");
                var better = Math.Max(text.Value, graph.Value);
                return Decide(hybrid.Value >= better,
                    $"hybrid passes {hybrid:P0} against text {text:P0} and graph {graph:P0}; it needed at least {better:P0}");
            }),

        new("H10",
            "Cognitive graphs introduce unnecessary overhead for simple tasks: on the single-hop lookup the graph costs more and answers no better.",
            "on G11 the graph arm is not slower to build and retrieve than the text arm, or it passes more often — either way the overhead bought something",
            "cost(C) > cost(B) AND pass(C) <= pass(B) on G11",
            rows =>
            {
                var graphCost = Mean(rows, Graph, r => r.Cost.TotalMilliseconds, "G11");
                var textCost = Mean(rows, Text, r => r.Cost.TotalMilliseconds, "G11");
                var graphPass = PassRate(rows, Graph, "G11");
                var textPass = PassRate(rows, Text, "G11");
                if (graphCost is null || textCost is null || graphPass is null || textPass is null)
                    return GraphVerdict.NotRun("no G11 rows for both arms");
                return Decide(graphCost.Value > textCost.Value && graphPass.Value <= textPass.Value,
                    $"on G11 the graph costs {graphCost.Value:F2} ms against the text arm's {textCost.Value:F2} ms, and passes {graphPass:P0} against {textPass:P0}");
            }),

        new("H11",
            "Accumulated experience improves performance on previously unseen but related tasks, without reusing knowledge that has gone stale.",
            "the graph arm does not beat the text arm on G13, or it answers from the expired window",
            Improvement("C", "B", "G13"),
            rows => Beats(rows, Graph, Text, "G13")),

        new("H12",
            "Knowledge can be transferred between agents using different language models, with its provenance intact.",
            "an agent on one model cannot use knowledge another model discovered, or the provenance is lost in the handoff",
            "needs two model providers; the deterministic half (export, import, provenance preserved) is asserted in the tests, the model half is not run",
            _ => GraphVerdict.NotRun("not run: needs two model providers; the deterministic handoff is covered by tests")),

        new("H13",
            "Structured provenance improves the detection of contradictory or unreliable information: the graph arm preserves the conflict and refuses the poisoned and the coincidental.",
            "the graph arm does not beat the text arm on G04, G08 and G09 together, or it violates a security fixture",
            Improvement("C", "B", "G04", "G08", "G09"),
            rows => Beats(rows, Graph, Text, "G04", "G08", "G09")),
    ];

    public static GraphHypothesis Find(string id) =>
        All.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"no hypothesis '{id}'", nameof(id));

    // ---------------------------------------------------------------- scorers

    private static string Improvement(string better, string worse, params string[] scenarios) =>
        $"pass({better}) - pass({worse}) >= {Preregistration.ImprovementThreshold.ToString("0.00", CultureInfo.InvariantCulture)} on {string.Join("+", scenarios)}";

    private static GraphVerdict Beats(IReadOnlyList<ScenarioResult> rows, string better, string worse, params string[] scenarios)
    {
        var first = PassRate(rows, better, scenarios);
        var second = PassRate(rows, worse, scenarios);
        if (first is null || second is null) return GraphVerdict.NotRun($"no rows for {better} or {worse} on {string.Join("+", scenarios)}");
        var delta = first.Value - second.Value;
        return Decide(delta >= Preregistration.ImprovementThreshold,
            $"{better} passes {first:P0} against {worse}'s {second:P0} on {string.Join("+", scenarios)}: {delta:+0.00;-0.00;0.00}, threshold {Preregistration.ImprovementThreshold:+0.00;-0.00;0.00}");
    }

    private static GraphVerdict Decide(bool held, string finding) => held ? GraphVerdict.Held(finding) : GraphVerdict.Failed(finding);

    private static double? PassRate(IReadOnlyList<ScenarioResult> rows, string arm, params string[] scenarios)
    {
        var taken = Select(rows, arm, scenarios);
        return taken.Count == 0 ? null : taken.Count(r => r.Passed) / (double)taken.Count;
    }

    private static double? Mean(IReadOnlyList<ScenarioResult> rows, string arm, Func<ScenarioResult, double> measure, params string[] scenarios)
    {
        var taken = Select(rows, arm, scenarios);
        return taken.Count == 0 ? null : taken.Average(measure);
    }

    private static List<ScenarioResult> Select(IReadOnlyList<ScenarioResult> rows, string arm, string[] scenarios) =>
        [.. rows.Where(r => string.Equals(r.Arm, arm, StringComparison.Ordinal)
                            && (scenarios.Length == 0 || scenarios.Contains(r.Scenario, StringComparer.Ordinal)))];
}

/// <summary>The hypotheses scored and rendered, in the shape <see cref="BatteryReport"/> uses: a generated file beside a hand-written account, never over it.</summary>
public static class GraphReport
{
    /// <summary>The first line of every file this writes; a file without it is somebody's and is left alone.</summary>
    public const string Marker = "<!-- generated by Creatura.Graph.Bench; the hand-written account is results.md -->";

    public static IReadOnlyList<(GraphHypothesis Hypothesis, GraphVerdict Verdict)> Judge(IReadOnlyList<ScenarioResult> rows) =>
        [.. GraphHypotheses.All.Select(h => (h, h.Score(rows)))];

    public static string ToMarkdown(IReadOnlyList<ScenarioResult> rows, string title = "Cognitive graph benchmark")
    {
        var inv = CultureInfo.InvariantCulture;
        var verdicts = Judge(rows);
        var gate = Preregistration.GateCleared(rows);
        var sb = new StringBuilder();
        sb.Append(Marker).Append('\n');
        sb.Append("# ").Append(title).Append('\n').Append('\n');
        sb.Append(rows.Count.ToString(inv)).Append(" runs over ")
            .Append(rows.Select(r => r.Scenario).Distinct(StringComparer.Ordinal).Count().ToString(inv)).Append(" scenarios and ")
            .Append(rows.Select(r => r.Arm).Distinct(StringComparer.Ordinal).Count().ToString(inv)).Append(" arms.\n\n");
        sb.Append("Pass rule: `").Append(Preregistration.PassRule).Append("`. Decision gate: ")
            .Append(Preregistration.DecisionGate).Append(": **").Append(gate ? "cleared" : "NOT CLEARED").Append("**.\n\n");

        sb.Append("## Hypotheses\n\n| id | verdict | the numbers that decided it |\n|---|---|---|\n");
        foreach (var (hypothesis, verdict) in verdicts)
            sb.Append("| ").Append(hypothesis.Id).Append(" | **").Append(BatteryReport.Word(verdict.Verdict))
                .Append("** | ").Append(Cell(verdict.Finding)).Append(" |\n");

        sb.Append("\n## Arms\n\n| arm | runs | passed | pass rate | answer rate | evidence recall | violations | learn ms | retrieve ms | chars | items | depth |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var arm in Summary.ByArm(rows))
            sb.Append("| ").Append(arm.Arm).Append(" | ").Append(arm.Runs.ToString(inv))
                .Append(" | ").Append(arm.Passed.ToString(inv))
                .Append(" | ").Append(arm.PassRate.ToString("P0", inv))
                .Append(" | ").Append(arm.AnswerRate.ToString("P0", inv))
                .Append(" | ").Append(arm.EvidenceRecall.ToString("0.00", inv))
                .Append(" | ").Append(arm.Violations.ToString(inv))
                .Append(" | ").Append(arm.LearnMilliseconds.ToString("0.00", inv))
                .Append(" | ").Append(arm.RetrieveMilliseconds.ToString("0.00", inv))
                .Append(" | ").Append(arm.Characters.ToString("0", inv))
                .Append(" | ").Append(arm.Items.ToString("0.0", inv))
                .Append(" | ").Append(arm.MaxDepth.ToString(inv)).Append(" |\n");

        sb.Append("\n## Per scenario\n\n| scenario | ");
        var arms = rows.Select(r => r.Arm).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
        sb.Append(string.Join(" | ", arms)).Append(" |\n");
        sb.Append("|---|").Append(string.Join("|", arms.Select(_ => "---"))).Append("|\n");
        foreach (var scenario in rows.Select(r => r.Scenario).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
        {
            sb.Append("| ").Append(scenario);
            foreach (var arm in arms)
            {
                var cell = rows.Where(r => r.Scenario == scenario && r.Arm == arm).ToList();
                sb.Append(" | ").Append(cell.Count == 0 ? "-" : $"{cell.Count(r => r.Passed)}/{cell.Count.ToString(inv)}");
            }

            sb.Append(" |\n");
        }

        var violations = Preregistration.SecurityViolations(rows);
        sb.Append("\n## Security fixtures\n\n");
        sb.Append(violations.Count == 0
            ? "No arm surfaced forbidden knowledge in G09 or G10.\n"
            : $"{violations.Count.ToString(inv)} runs surfaced knowledge they should not have:\n\n| scenario | arm | forbidden ids | forbidden text |\n|---|---|---|---|\n"
              + string.Join("\n", violations.Select(v =>
                  $"| {v.Scenario} | {v.Arm} | {v.ForbiddenHits} | {v.ForbiddenTextHits} |")));

        var failures = rows.Where(r => !r.Passed).ToList();
        sb.Append("\n## Failures\n\n");
        sb.Append(failures.Count == 0
            ? "Every run passed, which would be a reason to distrust the fixtures.\n"
            : $"{failures.Count.ToString(inv)} of {rows.Count.ToString(inv)} runs failed. The first fifty:\n\n| scenario | arm | answer | expected | recall | violations |\n|---|---|---|---|---|---|\n"
              + string.Join("\n", failures.Take(50).Select(f =>
                  $"| {f.Scenario} | {f.Arm} | {Cell(f.Answer ?? "-")} | {Cell(f.Expected ?? "-")} | {f.EvidenceRecall.ToString("0.00", inv)} | {f.Violations} |")));

        return sb.ToString();
    }

    /// <summary>Writes the report as <c>report.md</c>, never over a hand-written file: the same rule <see cref="BatteryReport"/> follows.</summary>
    public static string Write(IReadOnlyList<ScenarioResult> rows, string directory, string fileName = "report.md")
    {
        if (string.Equals(fileName, "README.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "results.md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{fileName} is hand-written; a generated report goes beside it, never over it", nameof(fileName));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; !Ours(path); n++) path = Path.Combine(directory, $"{stem}-{n}{ext}");
        File.WriteAllText(path, ToMarkdown(rows));
        return path;
    }

    private static bool Ours(string path)
    {
        if (!File.Exists(path)) return true;
        using var reader = new StreamReader(path);
        return reader.ReadLine() == Marker;
    }

    private static string Cell(string? text) => (text ?? "").Replace("|", "\\|").Replace("\n", " ");
}

/// <summary>The bridge back into the bench's own shapes, so these runs can be read by the CSV writers and the ratings without a second format.</summary>
public static class GraphResults
{
    /// <summary>
    /// A result as a bench row. The outcome mapping is deliberate: <c>real</c> is a pass, <c>claimed</c> is the right
    /// answer without the evidence that would justify it — which is exactly what the bench has always meant by it —
    /// and <c>open</c> is a wrong answer. Retrieved characters stand in for prompt tokens, because that is what they
    /// are before a model prices them.
    /// </summary>
    public static Row ToRow(ScenarioResult result, string battery = "graph") => new()
    {
        At = DateTimeOffset.UtcNow,
        Battery = battery,
        Test = result.Scenario,
        Seat = "none",
        World = result.Scenario,
        Agent = result.Arm,
        Extensions = "none",
        Arm = result.Arm,
        Case = result.Variant + 1,
        Repeat = 1,
        Outcome = result.Passed ? OutcomeClass.Real : result.AnswerCorrect ? OutcomeClass.Claimed : OutcomeClass.Open,
        Ticks = result.Cost.Items,
        PromptTokens = result.Cost.Characters,
        Note = result.AnswerCorrect
            ? (result.Violations > 0 ? $"{result.Violations} violation(s)" : "evidence complete")
            : $"answered '{result.Answer ?? "-"}', expected '{result.Expected ?? "-"}'",
    };

    /// <summary>Every result as a row, in the order they were run.</summary>
    public static IReadOnlyList<Row> ToRows(IReadOnlyList<ScenarioResult> results, string battery = "graph") =>
        [.. results.Select(r => ToRow(r, battery))];
}
