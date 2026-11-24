using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Creatura.Graph.Bench;

/// <summary>
/// The four arms of issue #110. They differ in one thing only — how they store and retrieve — and are given the same
/// corpus, the same question, the same budget and the same answerer. Whatever they cost to build is measured beside
/// what they achieve, because a memory that has to be indexed is not free and hypothesis H07 is about exactly that.
/// </summary>
public static partial class Arms
{
    /// <summary>Arm A: no persistent memory. The control every other arm has to beat, and the one that says whether the others are worth anything.</summary>
    public static IMemoryArm None() => new NoMemory();

    /// <summary>Arm B: conventional text retrieval, TF-IDF over the corpus lines.</summary>
    public static IMemoryArm Text() => new TextMemory();

    /// <summary>Arm C: the cognitive graph, retrieved by bounded traversal from the question's own terms.</summary>
    public static IMemoryArm Graph(GraphCaller caller, GraphSchema? schema = null) => new GraphMemory(caller, schema);

    /// <summary>Arm D: graph first, text filling whatever budget is left. Two retrievals, one budget.</summary>
    public static IMemoryArm Hybrid(GraphCaller caller, GraphSchema? schema = null) => new HybridMemory(caller, schema);

    /// <summary>The four arms in the order the rows report them.</summary>
    public static IReadOnlyList<IMemoryArm> All(GraphCaller caller, GraphSchema? schema = null) =>
        [None(), Text(), Graph(caller, schema), Hybrid(caller, schema)];

    // ---------------------------------------------------------------- tokenizing, shared by B and D

    /// <summary>Lowercase word tokens, stopwords and single characters dropped. Shared so that B and D retrieve over the same words.</summary>
    public static IReadOnlyList<string> Tokens(string text) =>
        [.. Word().Split(text.ToLowerInvariant()).Where(t => t.Length > 1 && !Stopwords.Contains(t, StringComparer.Ordinal))];

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "to", "in", "on", "for", "and", "or", "is", "are", "was", "were", "at", "by", "it",
        "be", "as", "this", "that", "with", "from", "who", "what", "when", "which", "may", "can", "does",
    };

    [GeneratedRegex(@"[^a-z0-9_.:]+")]
    private static partial Regex Word();

    /// <summary>The terms a question is asking about, which is all any arm gets to know about it.</summary>
    public static IReadOnlyList<string> QueryTokens(Question question)
    {
        var text = string.Join(" ",
            [question.Ask, question.Start, .. question.Relations, .. question.Options, question.Requires, .. question.Themes]);
        return Tokens(text).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A graph edge back as the corpus line it came from. Arm C is scored on the same text shape as arm B, so structure cannot help it at answer time.</summary>
    public static string Render(GraphEdge edge)
    {
        var stamps = edge.Warrant.Stamps;
        var valid = stamps.ValidFrom is null && stamps.ValidUntil is null
            ? ""
            : $" @{Format(stamps.ValidFrom)}..{Format(stamps.ValidUntil)}";
        var status = edge.Warrant.Verification switch
        {
            VerificationStatus.Verified => " !verified",
            VerificationStatus.Observed => " !observed",
            VerificationStatus.Inferred => " !inferred",
            VerificationStatus.Hypothesis => " !hypothesis",
            _ => " !claimed",
        };
        return $"{edge.Subject} -> {edge.Relation} -> {edge.Object}{valid}{status}";

        static string Format(DateTimeOffset? at) => at is { } value ? value.ToString("O") : "";
    }
}

/// <summary>Arm A. Retrieves nothing, ever, and costs nothing: the baseline the others have to earn their keep against.</summary>
public sealed class NoMemory : IMemoryArm
{
    public string Name => "A none";

    public ArmCost LastCost { get; private set; } = ArmCost.None;

    public ArmCost Learn(IReadOnlyList<Statement> corpus) => LastCost = ArmCost.None;

    public IReadOnlyList<RetrievedItem> Retrieve(Question question, int budget)
    {
        LastCost = ArmCost.None;
        return [];
    }
}

/// <summary>
/// Arm B: the corpus as a bag of words, retrieved by TF-IDF against the question's terms. This is the honest
/// conventional baseline — no embeddings, because a dense arm would need an embedding model and would then differ
/// from the others by more than its memory. Ties break on statement id, so the same corpus and question retrieve the
/// same items every time.
/// </summary>
public sealed class TextMemory : IMemoryArm
{
    private readonly List<Statement> _corpus = [];
    private readonly Dictionary<string, HashSet<string>> _index = new(StringComparer.Ordinal);
    private double _learnMilliseconds;

    public string Name => "B text";

    public ArmCost LastCost { get; private set; } = ArmCost.None;

    /// <summary>How many distinct statements the index holds, for the report.</summary>
    public int Indexed => _corpus.Count;

    public ArmCost Learn(IReadOnlyList<Statement> corpus)
    {
        var clock = Stopwatch.StartNew();
        _corpus.Clear();
        _index.Clear();
        foreach (var statement in corpus)
        {
            _corpus.Add(statement);
            foreach (var token in Arms.Tokens(statement.Text))
            {
                if (!_index.TryGetValue(token, out var ids)) _index[token] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(statement.Id);
            }
        }

        clock.Stop();
        _learnMilliseconds = clock.Elapsed.TotalMilliseconds;
        return LastCost = new ArmCost(_learnMilliseconds, 0, 0, 0, 0, 0, 0);
    }

    public IReadOnlyList<RetrievedItem> Retrieve(Question question, int budget)
    {
        var clock = Stopwatch.StartNew();
        var terms = Arms.QueryTokens(question);
        var total = Math.Max(1, _corpus.Count);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (!_index.TryGetValue(term, out var ids) || ids.Count == 0) continue;
            var idf = Math.Log((double)total / ids.Count) + 1.0;
            foreach (var id in ids) scores[id] = scores.TryGetValue(id, out var had) ? had + idf : idf;
        }

        var picked = scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(budget)
            .Select(kv => _corpus.First(s => string.Equals(s.Id, kv.Key, StringComparison.Ordinal)))
            .Select(s => new RetrievedItem(s.Id, s.Text))
            .ToList();

        clock.Stop();
        LastCost = new ArmCost(_learnMilliseconds, clock.Elapsed.TotalMilliseconds, picked.Count,
            picked.Sum(p => p.Text.Length), 0, 0, 0);
        return picked;
    }
}

/// <summary>
/// Arm C: the corpus read into the cognitive graph, retrieved by bounded traversal from the question's own terms.
/// The graph is built through <see cref="KnowledgePipeline"/>, so every statement arrives with the provenance and the
/// scope the corpus gave it, and the access policy applies to the retrieval exactly as it would in a deployment —
/// which is what G10 measures, and why this arm can return less than arm B for the same corpus.
/// </summary>
public sealed class GraphMemory : IMemoryArm
{
    private readonly GraphCaller _caller;
    private readonly GraphSchema _schema;
    private InMemoryGraphStore _store = new();
    private ICognitiveGraph _graph = null!;
    private double _learnMilliseconds;
    private int _nodes;
    private int _edges;

    public GraphMemory(GraphCaller caller, GraphSchema? schema = null)
    {
        _caller = caller;
        _schema = schema ?? GraphSchema.Benchmark();
        _store = new InMemoryGraphStore(_schema);
        _graph = _store.Open(caller);
    }

    public string Name => "C graph";

    public ArmCost LastCost { get; private set; } = ArmCost.None;

    /// <summary>The store behind the arm, so a scenario can assert on what the graph itself holds — contradictions, tombstones, the audit trail.</summary>
    public InMemoryGraphStore Store => _store;

    public ICognitiveGraph Graph => _graph;

    public ArmCost Learn(IReadOnlyList<Statement> corpus)
    {
        var clock = Stopwatch.StartNew();
        _store = new InMemoryGraphStore(_schema);
        _graph = _store.Open(_caller);
        var pipeline = new KnowledgePipeline(_store, new LineExtractor(_schema));
        foreach (var statement in corpus)
        {
            var provenance = new GraphProvenance(statement.Kind, statement.Id) { Corpus = statement.Id };
            pipeline.Ingest(_caller, statement.Text, provenance, statement.Scope);
        }

        clock.Stop();
        _learnMilliseconds = clock.Elapsed.TotalMilliseconds;
        _nodes = _store.NodeCount;
        _edges = _store.EdgeCount;
        return LastCost = new ArmCost(_learnMilliseconds, 0, 0, 0, _nodes, _edges, 0);
    }

    public IReadOnlyList<RetrievedItem> Retrieve(Question question, int budget)
    {
        var clock = Stopwatch.StartNew();
        var picked = GraphRetrieval.Retrieve(_graph, question, budget, Depth, out var deepest);
        clock.Stop();
        LastCost = new ArmCost(_learnMilliseconds, clock.Elapsed.TotalMilliseconds, picked.Count,
            picked.Sum(t => t.Text.Length), _nodes, _edges, deepest);
        return picked;
    }

    /// <summary>How far out a retrieval walks. Deliberately shallow: the question names its own neighbourhood.</summary>
    public int Depth { get; init; } = 3;
}

/// <summary>
/// The graph retrieval on its own, so that it can be pointed at any session — an arm's own store, or a store another
/// agent handed over. Seeded by the question's own terms and by nothing else, because an arm that was told where to
/// look is not being tested on retrieval.
/// </summary>
public static class GraphRetrieval
{
    public static IReadOnlyList<RetrievedItem> Retrieve(ICognitiveGraph graph, Question question, int budget, int depth, out int deepest)
    {
        var options = new GraphTraversalOptions { MaxDepth = depth, MaxNodes = 128, Limit = GraphBounds.MaxResultLimit };
        var picked = new List<RetrievedItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deepestReached = 0;
        var seeds = Seeds(graph, question);
        foreach (var seed in seeds)
        {
            foreach (var step in graph.Walk(seed, options).Steps)
            {
                deepestReached = Math.Max(deepestReached, step.Depth);
                if (step.Via is null || !seen.Add(step.Via.Id)) continue;
                picked.Add(Item(graph, step.Via));
                if (picked.Count >= budget) break;
            }

            if (picked.Count >= budget) break;
        }

        // A question about one entity also wants the relationships into it, which a walk alone reaches only when the
        // schema happens to give the relation an inverse.
        foreach (var seed in seeds)
        {
            if (picked.Count >= budget) break;
            foreach (var edge in graph.Adjacent(seed, GraphQuery.All with { Limit = GraphBounds.MaxResultLimit }))
            {
                if (!seen.Add(edge.Id)) continue;
                picked.Add(Item(graph, edge));
                if (picked.Count >= budget) break;
            }
        }

        deepest = deepestReached;
        return picked;
    }

    /// <summary>The nodes the question's own terms name, falling back to a substring match when nothing names one exactly.</summary>
    private static IReadOnlyList<string> Seeds(ICognitiveGraph graph, Question question)
    {
        var terms = new List<string> { GraphText.Normalize(question.Start) };
        terms.AddRange(question.Options.Select(GraphText.Normalize));
        terms.AddRange(question.Themes.Select(GraphText.Normalize));
        terms.AddRange(Arms.QueryTokens(question).Select(GraphText.Normalize));
        var seeds = terms.Distinct(StringComparer.Ordinal)
            .Where(t => t.Length > 0 && graph.Node(t) is not null)
            .ToList();
        if (seeds.Count > 0) return seeds;
        return [.. terms.Distinct(StringComparer.Ordinal)
            .Where(t => t.Length > 2)
            .SelectMany(t => graph.Nodes(GraphQuery.All with { Limit = GraphBounds.DefaultResultLimit })
                .Where(n => n.Id.Contains(t, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Id))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A retrieved edge as the statement it came from. Always the stored direction: traversal may cross a
    /// relationship backwards through its inverse, but what an arm hands the answerer has to be a statement the
    /// corpus actually made. Handing back "capability:gui providedBy browser:chromium" when the corpus said
    /// "browser:chromium provides capability:gui" would be the arm inventing vocabulary, and the answerer — which
    /// is shared and deliberately dumb — would be right not to understand it.
    /// </summary>
    private static RetrievedItem Item(ICognitiveGraph graph, GraphEdge edge)
    {
        var stored = graph.Edge(edge.Id) ?? edge;
        return new(stored.Warrant.Provenance.Corpus ?? stored.Id, Arms.Render(stored));
    }
}

/// <summary>
/// Arm D: graph and text over one budget, the graph's answer first and text filling what is left. Two retrievals and
/// two indexes to build, which is the cost the arm has to justify — and the reason its rows carry both learn times.
/// </summary>
public sealed class HybridMemory : IMemoryArm
{
    private readonly GraphMemory _graph;
    private readonly TextMemory _text = new();

    public HybridMemory(GraphCaller caller, GraphSchema? schema = null) => _graph = new GraphMemory(caller, schema);

    public string Name => "D hybrid";

    public ArmCost LastCost { get; private set; } = ArmCost.None;

    public InMemoryGraphStore Store => _graph.Store;

    public ArmCost Learn(IReadOnlyList<Statement> corpus)
    {
        var graph = _graph.Learn(corpus);
        var text = _text.Learn(corpus);
        var learn = graph.LearnMilliseconds + text.LearnMilliseconds;
        return LastCost = new ArmCost(learn, 0, 0, 0, graph.Nodes, graph.Edges, 0);
    }

    public IReadOnlyList<RetrievedItem> Retrieve(Question question, int budget)
    {
        var fromGraph = _graph.Retrieve(question, budget);
        var graphCost = _graph.LastCost;
        var seen = new HashSet<string>(fromGraph.Select(i => i.StatementId), StringComparer.Ordinal);
        var picked = fromGraph.Take(budget).ToList();
        var textCost = ArmCost.None;
        if (picked.Count < budget)
        {
            var rest = _text.Retrieve(question, budget - picked.Count);
            textCost = _text.LastCost;
            foreach (var item in rest)
            {
                if (!seen.Add(item.StatementId)) continue;
                picked.Add(item);
                if (picked.Count >= budget) break;
            }
        }

        LastCost = new ArmCost(graphCost.LearnMilliseconds + textCost.LearnMilliseconds,
            graphCost.RetrieveMilliseconds + textCost.RetrieveMilliseconds, picked.Count,
            picked.Sum(p => p.Text.Length), graphCost.Nodes, graphCost.Edges, graphCost.MaxDepth);
        return picked;
    }
}
