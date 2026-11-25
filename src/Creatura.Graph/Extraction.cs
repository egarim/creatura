using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Creatura.Graph;

/// <summary>What one pass of extraction produced, including what it could not read. The unread lines are part of the result: an extraction error is a bug to be priced, not a line to be dropped quietly.</summary>
public sealed record Extraction(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, IReadOnlyList<string> Skipped)
{
    public static readonly Extraction Empty = new([], [], []);

    public int Count => Nodes.Count + Edges.Count;
}

/// <summary>
/// Reads knowledge out of text. An implementation may use a model; this repository ships one that does not, because a
/// fixture whose graph depends on a model's mood is not a fixture, and because extraction cost and extraction error
/// have to be measurable separately from retrieval's (issue #109). An extractor never sees an oracle answer: it reads
/// the corpus the agent would have read, and the ground truth stays in the test.
/// </summary>
public interface IKnowledgeExtractor
{
    string Name { get; }

    Extraction Extract(string text, GraphProvenance provenance, GraphScope scope);
}

/// <summary>
/// How an entity id is normalized, so that two mentions of one thing become one node. Trim, collapse inner whitespace,
/// case-fold, and bound the length: an identity that grew without limit would let a corpus mint a node per mention and
/// call it knowledge. Applied by the extractor and available to a fixture, so that both agree on what one entity is.
/// </summary>
public static partial class GraphText
{
    public const int MaxIdLength = 200;

    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var collapsed = Whitespace().Replace(id.Trim(), " ");
        return collapsed.Length <= MaxIdLength
            ? collapsed.ToLowerInvariant()
            : collapsed[..MaxIdLength].ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// The deterministic extractor: one statement per line, in the notation the design document already writes its
/// examples in, so that a fixture reads like the thing it describes and nothing has to be inferred.
/// <code>
/// # a comment, or a blank line
/// service:orders : Application (semantic)
/// service:orders -&gt; ownedBy -&gt; team:payments @2026-01-01T00:00:00Z..2026-06-30T00:00:00Z [onboarding v3] !observed
/// </code>
/// A node line is <c>id : EntityType (category)</c>, where the category may be left out — and the colon has to have a
/// space either side of it, because ids are namespaced with colons of their own (<c>service:orders</c>) and a bare one
/// would be read as a separator. A relationship line is
/// <c>subject -&gt; relation -&gt; object</c>, with three optional trailing qualifiers in this order: <c>@from..to</c>
/// or <c>@instant</c> for validity, <c>[corpus]</c> for the document it came from, and <c>!status</c> for how well it
/// claims to be supported. An endpoint that was never declared as a node is declared here, as a <c>thing</c>: a
/// dangling relationship is worse than an untyped entity. A subject or object may not contain <c>@</c>, <c>[</c> or
/// <c>!</c>, which is what makes the qualifiers readable.
/// </summary>
public sealed partial class LineExtractor : IKnowledgeExtractor
{
    private readonly GraphSchema _schema;

    /// <param name="schema">The relations in force, so that a relationship takes the category its relation was declared with rather than being called semantic by default.</param>
    public LineExtractor(GraphSchema? schema = null) => _schema = schema ?? new GraphSchema();

    public string Name => "lines";

    public Extraction Extract(string text, GraphProvenance provenance, GraphScope scope)
    {
        if (string.IsNullOrWhiteSpace(text)) return Extraction.Empty;
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        var skipped = new List<string>();
        var sequence = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (Edge().Match(line) is { Success: true } edge)
            {
                var subject = GraphText.Normalize(edge.Groups["subject"].Value);
                // The relation keeps the spelling the corpus gave it: the schema matches case-insensitively, and a
                // note that reads "ownedby" is worse for a model than one that reads "ownedBy". Ids are the thing
                // that has to be folded, because two spellings of one entity have to become one node.
                var relation = edge.Groups["relation"].Value.Trim();
                var obj = GraphText.Normalize(edge.Groups["object"].Value);
                if (subject.Length == 0 || relation.Length == 0 || obj.Length == 0)
                {
                    skipped.Add(line);
                    continue;
                }

                var category = _schema.Relation(relation)?.Category ?? GraphCategory.Semantic;
                var warrant = Warrant(provenance, scope, edge.Groups["valid"].Value, edge.Groups["corpus"].Value, edge.Groups["status"].Value);
                Declare(nodes, subject, category, warrant);
                Declare(nodes, obj, category, warrant);
                edges.Add(new GraphEdge($"{subject}-{relation}-{obj}#{++sequence}", subject, relation, obj, category)
                {
                    Warrant = warrant,
                    Summary = GraphRedaction.Summary($"{subject} {relation} {obj}"),
                });
                continue;
            }

            if (Node().Match(line) is { Success: true } node)
            {
                var id = GraphText.Normalize(node.Groups["id"].Value);
                if (id.Length == 0)
                {
                    skipped.Add(line);
                    continue;
                }

                var type = node.Groups["type"].Success ? node.Groups["type"].Value.Trim() : "thing";
                var category = node.Groups["category"].Success ? ParseCategory(node.Groups["category"].Value) : GraphCategory.Semantic;
                // Written into a dictionary rather than a list, so that an explicit declaration replaces the endpoint
                // the relationships above implied instead of standing beside it.
                nodes[id] = new GraphNode(id, type, category) { Warrant = Warrant(provenance, scope, null, null, null) };
                continue;
            }

            skipped.Add(line);
        }

        return new Extraction([.. nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal)], edges, skipped);
    }

    /// <summary>Adds an endpoint nobody declared, so that no relationship in the graph points at nothing.</summary>
    private static void Declare(Dictionary<string, GraphNode> nodes, string id, GraphCategory category, GraphWarrant warrant)
    {
        if (id.Length == 0 || nodes.ContainsKey(id)) return;
        nodes[id] = new GraphNode(id, "thing", category) { Warrant = warrant };
    }

    private static GraphWarrant Warrant(GraphProvenance provenance, GraphScope scope, string? valid, string? corpus, string? status)
    {
        var stamps = GraphStamp.Always;
        if (!string.IsNullOrWhiteSpace(valid))
        {
            var parts = valid.Split("..", 2);
            if (parts.Length == 2 && TryStamp(parts[0], out var from) && TryStamp(parts[1], out var until))
                stamps = new GraphStamp { Observed = from, ValidFrom = from, ValidUntil = until };
            else if (TryStamp(valid, out var at))
                stamps = GraphStamp.At(at);
        }

        return GraphWarrant.Plain with
        {
            Stamps = stamps,
            Scope = scope,
            Verification = Ceiling(provenance.Kind, ParseStatus(status)),
            Provenance = string.IsNullOrWhiteSpace(corpus) ? provenance : provenance with { Corpus = corpus.Trim() },
        };
    }

    /// <summary>
    /// The strongest thing a statement may call itself, given where it came from. What a corpus says about its own
    /// reliability is capped by whoever is vouching for the corpus: an experiment's oracle graph is ground truth
    /// because the experiment says so in code and not because a line said <c>!verified</c>; an extraction is a claim
    /// until something checks it; and knowledge that arrived from another agent is an observation at best, because
    /// verification is an act by a named verifier against evidence that verifier could read, and neither of those
    /// travels inside a document.
    /// </summary>
    public static VerificationStatus Ceiling(ProvenanceKind kind, VerificationStatus claimed)
    {
        var ceiling = kind switch
        {
            ProvenanceKind.Oracle => VerificationStatus.Verified,
            ProvenanceKind.Observation => VerificationStatus.Observed,
            ProvenanceKind.Import => VerificationStatus.Observed,
            _ => VerificationStatus.Claimed,
        };
        return claimed <= ceiling ? claimed : ceiling;
    }

    private static bool TryStamp(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at);

    public static VerificationStatus ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "hypothesis" => VerificationStatus.Hypothesis,
        "inferred" => VerificationStatus.Inferred,
        "claimed" => VerificationStatus.Claimed,
        "observed" => VerificationStatus.Observed,
        "verified" => VerificationStatus.Verified,
        _ => VerificationStatus.Claimed,
    };

    public static GraphCategory ParseCategory(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        "semantic" => GraphCategory.Semantic,
        "episodic" => GraphCategory.Episodic,
        "causal" => GraphCategory.Causal,
        "task" => GraphCategory.Task,
        "social" => GraphCategory.Social,
        "environmental" or "environment" => GraphCategory.Environmental,
        _ => GraphCategory.Semantic,
    };

    [GeneratedRegex(@"^(?<id>.+?)\s+:\s+(?<type>[^(]+?)\s*(?:\((?<category>[a-z]+)\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Node();

    [GeneratedRegex(@"^(?<subject>.+?)\s*->\s*(?<relation>.+?)\s*->\s*(?<object>[^@\[!]+?)\s*(?:@(?<valid>[^\s\[]+))?\s*(?:\[(?<corpus>[^\]]+)\])?\s*(?:!(?<status>[a-z]+))?\s*$")]
    private static partial Regex Edge();
}

/// <summary>
/// What one pass of the pipeline did, and what it cost. Cost is part of the result and not a diagnostic: a retrieval
/// that has to build a graph first is only cheaper than the alternative once the building is counted, which is the
/// whole of hypothesis H07 and the reason H10 exists.
/// </summary>
public sealed record GraphIngest(
    int Added,
    int Revised,
    int Contradicted,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<GraphMutation> Refused,
    double Milliseconds)
{
    public static readonly GraphIngest Nothing = new(0, 0, 0, [], [], 0);

    public int Taken => Added + Revised;

    /// <summary>Whether anything the pipeline was given failed to arrive, for whatever reason.</summary>
    public bool Lossy => Skipped.Count > 0 || Refused.Count > 0;
}

/// <summary>
/// The acquisition pipeline, in the order issue #109 gives it: observe, extract, normalize, find what is already
/// known, notice conflicts, attach provenance, decide how much to believe it, apply the policy, store — and only then
/// is it available to retrieve. The middle steps are one call rather than nine because splitting them would let a
/// caller store knowledge that never went through the steps in between, and the steps in between are the ones that
/// keep a graph from becoming a pile of assertions.
/// </summary>
public sealed class KnowledgePipeline
{
    private readonly IGraphStore _store;

    public KnowledgePipeline(IGraphStore store, IKnowledgeExtractor? extractor = null)
    {
        _store = store;
        Extractor = extractor ?? new LineExtractor(store.Schema);
    }

    public IGraphStore Store => _store;

    public IKnowledgeExtractor Extractor { get; }

    /// <summary>
    /// Reads a corpus into the graph as this caller. What the caller may not write is refused and reported rather than
    /// dropped, because a policy that worked silently could be neither tested nor audited.
    /// </summary>
    public GraphIngest Ingest(GraphCaller caller, string text, GraphProvenance provenance, GraphScope? scope = null)
    {
        var clock = Stopwatch.StartNew();
        var place = scope ?? caller.Scope;
        var extraction = Extractor.Extract(text, provenance, place);
        var graph = _store.Open(caller);
        var added = 0;
        var revised = 0;
        var contradicted = 0;
        var refused = new List<GraphMutation>();

        foreach (var node in extraction.Nodes)
            Tally(graph.Put(node), node.Id);
        foreach (var edge in extraction.Edges)
        {
            var mutation = graph.Put(edge);
            Tally(mutation, edge.Id);
            // Read the state back rather than parsing the reason string: the store decides what a contradiction is.
            if (graph.Edge(edge.Id)?.Warrant.State == GraphState.Contradicted) contradicted++;
        }

        clock.Stop();
        return new GraphIngest(added, revised, contradicted, extraction.Skipped, refused, clock.Elapsed.TotalMilliseconds);

        void Tally(GraphMutation mutation, string id)
        {
            switch (mutation.Kind)
            {
                case MutationKind.Added: added++; break;
                case MutationKind.Revised: revised++; break;
                case MutationKind.Refused: refused.Add(mutation with { Id = id }); break;
            }
        }
    }
}
