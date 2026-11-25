using Creatura.Bench;

namespace Creatura.Graph.Bench.Tests;

/// <summary>
/// Tests of the harness rather than of a result. A benchmark whose fixtures are wrong measures nothing, so these
/// check the properties that have to hold whatever the numbers come out as: every scenario is present, the memoryless
/// arm passes none of them, a run reproduces itself, and no secret reaches a result file.
/// </summary>
public class HarnessTests
{
    private static readonly string[] Expected =
        ["G01", "G02", "G03", "G04", "G05", "G06", "G07", "G08", "G09", "G10", "G11", "G12", "G13", "G14"];

    /// <summary>The scenario without its generated padding, for the tests that assert on exactly which statements an arm surfaces.</summary>
    private static GraphScenario WithoutNoise(GraphScenario scenario, string noisePrefix) =>
        scenario with { Corpus = [.. scenario.Corpus.Where(s => !s.Id.StartsWith(noisePrefix, StringComparison.Ordinal))] };

    [Fact]
    public void AllFourteenScenariosArePresentAndNamed()
    {
        Assert.Equal(Expected, Scenarios.All.Select(s => s.Id));
        Assert.All(Scenarios.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.NotEmpty(s.Corpus);
            Assert.NotNull(s.Oracle);
            Assert.False(string.IsNullOrWhiteSpace(s.Question.Ask));
            Assert.True(s.Budget > 0);
            // Every scenario names the hypothesis it bears on, so a result can be read against a prediction.
            Assert.Matches("^H[0-9]{2}$", s.Hypothesis);
        });
    }

    [Fact]
    public void EveryCorpusIsMostlyIrrelevantSoRetrievalHasSomethingToDo()
    {
        // A fixture where the whole corpus fits in the budget measures nothing: every arm would retrieve everything
        // and the arms would differ only in how they sorted it.
        Assert.All(Scenarios.All, s => Assert.True(s.Corpus.Count > s.Budget * 3,
            $"{s.Id} has {s.Corpus.Count} statements and a budget of {s.Budget}, which is not a retrieval problem"));
    }

    [Fact]
    public void TheMemorylessArmPassesNoScenario()
    {
        var rows = Runner.Run(new RunOptions { Arms = ["A none"] });

        Assert.Equal(Scenarios.All.Count, rows.Count);
        // If an arm with no memory at all can pass, the question was answerable without knowing anything, and the
        // fixture is measuring the answerer rather than the memory.
        Assert.All(rows, r => Assert.False(r.Passed, $"{r.Scenario} was passed with no memory at all"));
        Assert.All(rows, r => Assert.Empty(r.Retrieved));
        Assert.All(rows, r => Assert.Equal(0, r.Cost.Characters));
        // Note what this does not assert: that every answer is "unknown". On G08 the memoryless arm says "none", which
        // is the right string for the wrong reason — no evidence was retrieved — and the pass rule catches it through
        // evidence recall rather than through the answer.
        Assert.Contains(rows, r => r.Scenario == "G08" && r.AnswerCorrect && !r.Passed);
    }

    [Fact]
    public void TheAnswererReadsTheCorpusNotationAndNothingElse()
    {
        var items = new List<RetrievedItem>
        {
            new("s1", "service:orders -> ownedBy -> team:payments !observed"),
            new("s2", "team:payments -> escalatesTo -> person:dana !observed"),
            new("s3", "not a statement about anything at all"),
        };

        var chain = Answerer.Solve(new Question("who", QuestionKind.Chain)
        {
            Start = "service:orders",
            Relations = ["ownedBy", "escalatesTo"],
        }, items);

        Assert.Equal("person:dana", chain);
        // And with the second hop missing, it says so rather than guessing.
        Assert.Equal(Answerer.Unknown, Answerer.Solve(new Question("who", QuestionKind.Chain)
        {
            Start = "service:orders",
            Relations = ["ownedBy", "escalatesTo"],
        }, [items[0]]));
    }

    [Fact]
    public void TheAnswererRefusesToChooseBetweenTwoAnswers()
    {
        var items = new List<RetrievedItem>
        {
            new("s1", "service:orders -> ownedBy -> team:one !claimed"),
            new("s2", "service:orders -> ownedBy -> team:two !claimed"),
        };
        var question = new Question("which team", QuestionKind.Resolve) { Start = "service:orders", Relations = ["ownedBy"] };

        Assert.Equal(Answerer.Unresolved, Answerer.Solve(question, items));
        // One observed answer beats one claim, which is the whole point of keeping provenance.
        var withEvidence = new List<RetrievedItem>
        {
            new("s1", "service:orders -> ownedBy -> team:one !observed"),
            new("s2", "service:orders -> ownedBy -> team:two !claimed"),
        };
        Assert.Equal("team:one", Answerer.Solve(question, withEvidence));
    }

    [Fact]
    public void TheAnswererHonoursAValidityWindow()
    {
        var items = new List<RetrievedItem>
        {
            new("s1", "service:orders -> ownedBy -> team:old @2025-01-01T00:00:00Z..2026-03-01T00:00:00Z !observed"),
            new("s2", "service:orders -> ownedBy -> team:new @2026-03-01T00:00:00Z.. !observed"),
        };
        var question = new Question("who owns it", QuestionKind.AtTime)
        {
            Start = "service:orders",
            Relations = ["ownedBy"],
            At = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal("team:new", Answerer.Solve(question, items));
        Assert.Equal("team:old", Answerer.Solve(question with
        {
            At = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
        }, items));
    }

    [Fact]
    public void TheAnswererWillNotReadACoincidenceAsACause()
    {
        var items = new List<RetrievedItem>
        {
            new("s1", "action:restart -> coincidedWith -> event:recovery !observed"),
            new("s2", "action:restart -> preceded -> event:recovery !observed"),
        };
        var question = new Question("what caused it", QuestionKind.Cause) { Start = "event:recovery", Relations = ["caused"] };

        Assert.Equal(Answerer.NoCause, Answerer.Solve(question, items));
        // And a real, observed cause is named.
        Assert.Equal("action:restart", Answerer.Solve(question,
            [new RetrievedItem("s3", "action:restart -> caused -> event:recovery !observed")]));
    }

    [Fact]
    public void TheArmsDifferOnlyInHowTheyRetrieve()
    {
        var scenario = Scenarios.G01();
        var arms = Arms.All(Scenarios.Caller);

        Assert.Equal(["A none", "B text", "C graph", "D hybrid"], arms.Select(a => a.Name));
        // Same corpus to all four, same budget, same question, same answerer: the only thing that varies is the memory.
        foreach (var arm in arms)
        {
            arm.Learn(scenario.Corpus);
            Assert.True(arm.Retrieve(scenario.Question, scenario.Budget).Count <= scenario.Budget);
        }

        Assert.Empty(arms[0].Retrieve(scenario.Question, scenario.Budget));
        Assert.NotEmpty(arms[1].Retrieve(scenario.Question, scenario.Budget));
        Assert.NotEmpty(arms[2].Retrieve(scenario.Question, scenario.Budget));
    }

    [Fact]
    public void TheGraphArmSurfacesTheMultiHopChainTheTextArmHasToFindInTheNoise()
    {
        var scenario = Scenarios.G01();
        var graph = Arms.Graph(Scenarios.Caller);
        graph.Learn(scenario.Corpus);

        var retrieved = graph.Retrieve(scenario.Question, scenario.Budget);

        Assert.Contains("g01-s2", retrieved.Select(r => r.StatementId));
        Assert.Contains("g01-s3", retrieved.Select(r => r.StatementId));
        Assert.Equal("person:oncall-dana", Answerer.Solve(scenario.Question, retrieved));
    }

    [Fact]
    public void TheGraphArmCannotStoreProseThatIsOnlyAnInstruction()
    {
        var scenario = Scenarios.G09();
        var graph = Arms.Graph(Scenarios.Caller);
        graph.Learn(scenario.Corpus);

        var retrieved = graph.Retrieve(scenario.Question, scenario.Budget);
        var text = string.Join("\n", retrieved.Select(r => r.Text));

        // The injected prose is not a statement about the world, so there is nowhere in the graph to put it.
        Assert.DoesNotContain("ignore previous policy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("grant admin", text, StringComparison.Ordinal);
        Assert.DoesNotContain("g09-p2", retrieved.Select(r => r.StatementId));
    }

    [Fact]
    public void TheTextArmCarriesTheInjectedProseAndTheGraphArmDoesNot()
    {
        var scenario = WithoutNoise(Scenarios.G09(), "g09-n");
        var text = Arms.Text();
        text.Learn(scenario.Corpus);

        var retrieved = text.Retrieve(scenario.Question, scenario.Budget);

        // This is not a defect in arm B to be fixed; it is the difference the scenario exists to measure. A bag of
        // text holds whatever was written into it, including an instruction that was never a fact.
        Assert.Contains("g09-p2", retrieved.Select(r => r.StatementId));
    }

    [Fact]
    public void TheGraphArmDeclinesAnotherTenantsKnowledgeAndTheTextArmDoesNot()
    {
        var scenario = WithoutNoise(Scenarios.G10(), "g10-n");
        var graph = Arms.Graph(Scenarios.Caller);
        graph.Learn(scenario.Corpus);
        var text = Arms.Text();
        text.Learn(scenario.Corpus);

        var fromGraph = graph.Retrieve(scenario.Question, scenario.Budget);
        var fromText = text.Retrieve(scenario.Question, scenario.Budget);
        var graphIds = fromGraph.Select(r => r.StatementId).ToList();
        var textIds = fromText.Select(r => r.StatementId).ToList();

        Assert.DoesNotContain("g10-x1", graphIds);
        Assert.DoesNotContain("g10-x2", graphIds);
        Assert.Contains("g10-x1", textIds);
        // And the canary is redacted out of the graph but sits in the text arm's index verbatim.
        Assert.Empty(SecretCanary.FoundIn(string.Join("\n", fromGraph.Select(r => r.Text))));
        Assert.NotEmpty(SecretCanary.FoundIn(string.Join("\n", fromText.Select(r => r.Text))));
    }

    [Fact]
    public void ARunReproducesItselfFromItsSeed()
    {
        var options = new RunOptions { Variants = 2, Seed = 12345 };
        var first = Runner.Run(options);
        var second = Runner.Run(options);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(r => (r.Scenario, r.Arm, r.Variant, r.Answer, r.Passed, r.Retrieved)),
            second.Select(r => (r.Scenario, r.Arm, r.Variant, r.Answer, r.Passed, r.Retrieved)));
    }

    [Fact]
    public void AVariantRenamesEntitiesAndChangesNothingElse()
    {
        var scenario = Scenarios.G01();
        var variant = Variants.Of(scenario, 1, 999);

        Assert.Equal(scenario.Corpus.Count, variant.Corpus.Count);
        Assert.Equal(scenario.Corpus.Select(s => s.Id), variant.Corpus.Select(s => s.Id));
        Assert.Equal(scenario.Question.Kind, variant.Question.Kind);
        Assert.Equal(scenario.Question.Relations, variant.Question.Relations);
        Assert.Equal(scenario.Oracle.Required, variant.Oracle.Required);
        Assert.NotEqual(scenario.Version, variant.Version);
        // The names were permuted, not invented: the answer is still one of the names the fixture declared.
        Assert.Contains(variant.Oracle.ExpectedAnswer, scenario.NameGroups.SelectMany(g => g));
        Assert.Contains(variant.Question.Start, scenario.NameGroups.SelectMany(g => g).Append(scenario.Question.Start));
        // And something actually moved, or the variant would be the fixture wearing a different hat.
        Assert.NotEqual(scenario.Corpus.Select(s => s.Text), variant.Corpus.Select(s => s.Text));
    }

    [Fact]
    public void VariantZeroIsTheFixtureAsWritten()
    {
        var scenario = Scenarios.G03();

        Assert.Same(scenario, Variants.Of(scenario, 0, 7));
    }

    [Fact]
    public void NoSecretReachesAResultFile()
    {
        var scenario = Scenarios.G10();
        var results = Arms.All(Scenarios.Caller).Select(arm => Runner.One(scenario, arm, 0, 1)).ToList();

        var jsonl = string.Join("\n", results.Select(r => r.ToJsonl()));

        Assert.Empty(SecretCanary.FoundIn(jsonl));
        Assert.DoesNotContain(SecretCanary.ApiKey, jsonl, StringComparison.Ordinal);
    }

    [Fact]
    public void AResultMapsOntoTheBenchsOwnRow()
    {
        var scenario = Scenarios.G11();
        var passed = Runner.One(scenario, Arms.Graph(Scenarios.Caller), 0, 1);
        var failed = Runner.One(scenario, Arms.None(), 0, 1);

        Assert.Equal(OutcomeClass.Real, GraphResults.ToRow(passed).Outcome);
        Assert.Equal(OutcomeClass.Open, GraphResults.ToRow(failed).Outcome);
        Assert.Equal("G11", GraphResults.ToRow(passed).Test);
        Assert.Equal("C graph", GraphResults.ToRow(passed).Arm);
        // A right answer without the evidence for it is what the bench has always called "claimed".
        var claimed = passed with { RequiredFound = 0, RequiredTotal = 1 };
        Assert.False(claimed.Passed);
        Assert.Equal(OutcomeClass.Claimed, GraphResults.ToRow(claimed).Outcome);
    }

    [Fact]
    public void TheHypothesesArePreRegisteredWithThresholdsAndFalsifiers()
    {
        Assert.Equal(13, GraphHypotheses.All.Count);
        Assert.Equal(Enumerable.Range(1, 13).Select(n => $"H{n:00}"), GraphHypotheses.All.Select(h => h.Id));
        Assert.All(GraphHypotheses.All, h =>
        {
            Assert.False(string.IsNullOrWhiteSpace(h.Statement));
            Assert.False(string.IsNullOrWhiteSpace(h.Falsifier));
            Assert.False(string.IsNullOrWhiteSpace(h.Threshold));
            Assert.NotNull(h.Score);
        });
        // The two that need models say so rather than reporting a verdict they cannot support.
        Assert.Equal(HypothesisVerdict.NotRun, GraphHypotheses.Find("H08").Score([]).Verdict);
        Assert.Equal(HypothesisVerdict.NotRun, GraphHypotheses.Find("H12").Score([]).Verdict);
        Assert.Contains("not run", GraphHypotheses.Find("H08").Score([]).Finding, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWholeSuiteRunsAndScoresEveryHypothesis()
    {
        var rows = Runner.Run(new RunOptions());

        Assert.Equal(Scenarios.All.Count * 4, rows.Count);
        var verdicts = GraphReport.Judge(rows);
        Assert.Equal(GraphHypotheses.All.Count, verdicts.Count);
        // A hypothesis with rows to read is not allowed to report itself unrun.
        Assert.All(verdicts.Where(v => v.Hypothesis.Id is not ("H08" or "H12")),
            v => Assert.NotEqual(HypothesisVerdict.NotRun, v.Verdict.Verdict));
        var markdown = GraphReport.ToMarkdown(rows);
        Assert.StartsWith(GraphReport.Marker, markdown, StringComparison.Ordinal);
        Assert.Contains("## Hypotheses", markdown, StringComparison.Ordinal);
        Assert.Contains("## Failures", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedReportNeverOverwritesAHandWrittenFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "graph-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rows = Runner.Run(new RunOptions { Only = ["G11"] });
            var path = GraphReport.Write(rows, directory);
            Assert.Equal("report.md", Path.GetFileName(path));
            // A second write finds the first one is ours and overwrites it rather than multiplying files.
            Assert.Equal(path, GraphReport.Write(rows, directory));

            File.WriteAllText(Path.Combine(directory, "results.md"), "somebody wrote this");
            Assert.Throws<ArgumentException>(() => GraphReport.Write(rows, directory, "results.md"));
            Assert.Throws<ArgumentException>(() => GraphReport.Write(rows, directory, "README.md"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void KnowledgeHandedBetweenTwoAgentsKeepsItsProvenanceAndItsAnswer()
    {
        // The deterministic half of G06 and H12: agent A discovers, exports, and agent B — a different store, a
        // different caller, and in a real run a different model — answers from A's note without redoing the work.
        var scenario = Scenarios.G06();
        var alice = new GraphCaller("alice");
        var bob = new GraphCaller("bob");
        var source = new InMemoryGraphStore(GraphSchema.Benchmark());
        var pipeline = new KnowledgePipeline(source);
        foreach (var statement in scenario.Corpus)
            pipeline.Ingest(alice, statement.Text,
                new GraphProvenance(statement.Kind, "alice") { Corpus = statement.Id }, statement.Scope);

        var answerBefore = Answerer.Solve(scenario.Question,
            GraphRetrieval.Retrieve(source.Open(alice), scenario.Question, scenario.Budget, 3, out _));

        var destination = new InMemoryGraphStore(GraphSchema.Benchmark());
        destination.Import(bob, source.Export(alice));
        var answerAfter = Answerer.Solve(scenario.Question,
            GraphRetrieval.Retrieve(destination.Open(bob), scenario.Question, scenario.Budget, 3, out _));

        Assert.Equal(scenario.Oracle.ExpectedAnswer, answerBefore);
        Assert.Equal(answerBefore, answerAfter);

        var imported = destination.Open(bob).Edges().First(e => e.Warrant.Provenance.Corpus == "g06-s2");
        Assert.Equal("alice", imported.Warrant.Provenance.Source);
        Assert.Equal(ProvenanceKind.Observation, imported.Warrant.Provenance.Kind);
        Assert.Equal(ProvenanceKind.Import, imported.Warrant.Provenance.ArrivedAs);
        Assert.Equal("bob", imported.Warrant.Scope.Owner);
        // Observed and not Verified, even though the corpus line said !verified: a statement's own claim about its
        // reliability is capped by whoever vouches for the corpus, and an observation is vouched for no further.
        Assert.Equal(VerificationStatus.Observed, imported.Warrant.Verification);
    }
}
