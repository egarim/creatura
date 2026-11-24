namespace Creatura.Graph;

/// <summary>
/// The bounds every read honours, in one place so that an experiment can state what it allowed and a reader can check
/// it. The defaults are the Core's own, not new numbers: <see cref="Planner.Find"/> searches to
/// <c>maxDepth: 8</c>, <c>DreamSleepExtension</c> stops at depth 8 or 64 solutions, and <c>HandOffPayload.MaxNotes</c>
/// is 4000 characters. An unbounded walk over a graph that has a cycle in it is the one way an optional extension can
/// hang an agent, so the bound is not a tuning knob.
/// </summary>
public static class GraphBounds
{
    /// <summary>How many results one query returns unless it asks for fewer.</summary>
    public const int DefaultResultLimit = 100;

    /// <summary>The most any query may ask for. A retrieval that needs more than this is injecting the graph into the prompt, which is the thing the graph exists to avoid.</summary>
    public const int MaxResultLimit = 10_000;

    public const int DefaultMaxDepth = 8;

    public const int DefaultMaxNodes = 256;

    public const int DefaultMaxEdges = 1_024;

    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromSeconds(2);

    /// <summary>How much of a summary is kept. A seat reads a line, not a document.</summary>
    public const int MaxSummaryLength = 240;

    /// <summary>How much of one attribute or evidence line is kept, matching the ledger's own note limit.</summary>
    public const int MaxTextLength = 4_000;
}

/// <summary>Who is asking. An agent is never the authority on what it may read; this is the input to the policy that decides.</summary>
public sealed record GraphCaller(string Agent, string Tenant = "default", string Workspace = "default")
{
    public static readonly GraphCaller Benchmark = new("benchmark", "default");

    public GraphScope Scope => new(Tenant, Workspace);

    public override string ToString() => $"{Agent}@{Tenant}/{Workspace}";
}

/// <summary>
/// Who may read and revise what. Kept as an interface because authorization is the part of this that has to be
/// replaceable: an experiment needs a policy it can state, a deployment needs one it can audit, and neither should
/// have to take the other's. The policy runs outside the model, so retrieved content is data and an agent that reads
/// "everyone may read this" out of the graph has read a string and nothing more.
/// </summary>
public interface IGraphAccessPolicy
{
    string Name { get; }

    bool MayRead(GraphCaller caller, GraphScope scope);

    bool MayWrite(GraphCaller caller, GraphScope scope);
}

/// <summary>
/// The default policy: a tenant is a wall, a workspace is a room in it, and knowledge is shared out of a room only
/// when somebody said to share it. Reading needs the same tenant and either the same workspace or a place on the
/// scope's reader list; revising needs the same room and, when the knowledge names an owner, to be that owner.
/// Deliberately not the permissive one — a graph that starts open has to be closed later, and knowledge written
/// before it was closed is already leaked.
/// </summary>
public sealed class WorkspaceAccessPolicy : IGraphAccessPolicy
{
    public static readonly WorkspaceAccessPolicy Instance = new();

    public string Name => "workspace";

    public bool MayRead(GraphCaller caller, GraphScope scope)
    {
        if (!SameTenant(caller, scope)) return false;
        if (string.Equals(caller.Workspace, scope.Workspace, StringComparison.Ordinal)) return true;
        return scope.Readers.Contains(caller.Workspace, StringComparer.Ordinal)
               || scope.Readers.Contains(caller.Agent, StringComparer.Ordinal);
    }

    public bool MayWrite(GraphCaller caller, GraphScope scope)
    {
        if (!SameTenant(caller, scope)) return false;
        if (!string.Equals(caller.Workspace, scope.Workspace, StringComparison.Ordinal)) return false;
        return scope.Owner.Length == 0 || string.Equals(scope.Owner, caller.Agent, StringComparison.Ordinal);
    }

    private static bool SameTenant(GraphCaller caller, GraphScope scope) =>
        string.Equals(caller.Tenant, scope.Tenant, StringComparison.Ordinal);
}

/// <summary>
/// A policy that admits everybody, for loading an experiment's oracle graph and for a test that is about something
/// else. Named for what it is so that finding one in a code path is a question and not a surprise.
/// </summary>
public sealed class UnrestrictedAccessPolicy : IGraphAccessPolicy
{
    public static readonly UnrestrictedAccessPolicy Instance = new();

    public string Name => "unrestricted";

    public bool MayRead(GraphCaller caller, GraphScope scope) => true;

    public bool MayWrite(GraphCaller caller, GraphScope scope) => true;
}

/// <summary>
/// What a read asks for. Every field narrows; an empty query is the whole readable graph up to
/// <see cref="Limit"/>. <see cref="State"/> defaults to <see cref="GraphState.Active"/> because a retrieval that did
/// not ask for superseded knowledge should not get it — and <see cref="GraphState.Deleted"/> is never returned
/// whatever is asked, which is the only thing that makes deletion mean anything (scenario G10).
/// </summary>
public sealed record GraphQuery
{
    public static readonly GraphQuery All = new();

    public GraphCategory? Category { get; init; }

    public string? EntityType { get; init; }

    public string? Relation { get; init; }

    public string? Subject { get; init; }

    public string? Object { get; init; }

    /// <summary>The weakest verification worth having. A question that needs a fact should ask for <see cref="VerificationStatus.Observed"/> and get nothing rather than get a rumour.</summary>
    public VerificationStatus? MinVerification { get; init; }

    public GraphState? State { get; init; } = GraphState.Active;

    /// <summary>The moment the knowledge has to hold at; null asks for anything, whenever it was true.</summary>
    public DateTimeOffset? At { get; init; }

    public ProvenanceKind? Provenance { get; init; }

    public string? Corpus { get; init; }

    /// <summary>Whether to follow a relation backwards through its schema inverse. On by default: a graph that only answers the way it was written is half a graph.</summary>
    public bool FollowInverse { get; init; } = true;

    public int Limit { get; init; } = GraphBounds.DefaultResultLimit;

    public int Bound => Math.Clamp(Limit, 1, GraphBounds.MaxResultLimit);
}

/// <summary>Why a traversal stopped before it was finished. Recorded because "the answer was not there" and "the answer was beyond the bound" are different findings, and an experiment that cannot tell them apart cannot report a negative result honestly.</summary>
public enum Truncation
{
    None,

    /// <summary>The depth limit was reached. There may be more beyond it.</summary>
    Depth,

    Nodes,

    Edges,

    /// <summary>The time limit was reached.</summary>
    Duration,

    /// <summary>Enough results were collected; the walk was never going to be finished anyway.</summary>
    Limit,
}

/// <summary>What a traversal may cost. Every bound has a default and every one of them is honoured, including by a graph with a cycle in it: the visited set is not optional and cannot be switched off.</summary>
public sealed record GraphTraversalOptions
{
    public static readonly GraphTraversalOptions Default = new();

    public int MaxDepth { get; init; } = GraphBounds.DefaultMaxDepth;

    public int MaxNodes { get; init; } = GraphBounds.DefaultMaxNodes;

    public int MaxEdges { get; init; } = GraphBounds.DefaultMaxEdges;

    public TimeSpan MaxDuration { get; init; } = GraphBounds.DefaultMaxDuration;

    /// <summary>How many steps the walk hands back. Bounded again by <see cref="GraphBounds.MaxResultLimit"/>.</summary>
    public int Limit { get; init; } = GraphBounds.DefaultResultLimit;

    /// <summary>Whether to follow a relation backwards through its schema inverse.</summary>
    public bool FollowInverse { get; init; } = true;

    /// <summary>A caller's own edge predicate, as <c>RoadMap.Path</c> takes <c>usable</c>: the graph is static knowledge and whether an edge may be used right now is the caller's business.</summary>
    public Func<GraphEdge, bool>? Usable { get; init; }

    public VerificationStatus? MinVerification { get; init; }

    /// <summary>The moment every step has to hold at.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>
    /// The order to expand in. Ordinal by edge id when null, so that a walk over the same graph is the same walk every
    /// time and a result can be reproduced from a seed and a fixture version alone.
    /// </summary>
    public Func<GraphEdge, double>? Rank { get; init; }

    public GraphTraversalOptions WithDepth(int maxDepth) => this with { MaxDepth = maxDepth };
}

/// <summary>One step of a walk: how far out it was, what was reached, and the relationship it was reached through (null at the start).</summary>
public sealed record GraphStep(int Depth, GraphNode Node, GraphEdge? Via);

/// <summary>
/// A walk and what it cost. The cost is part of the result and not a diagnostic: whether a graph answers a question
/// cheaply is one of the things under investigation (hypotheses H07 and H10), so a walk that does not report its own
/// depth, size and time cannot be compared with the retrieval it is supposed to beat.
/// </summary>
public sealed record GraphWalk(
    IReadOnlyList<GraphStep> Steps,
    Truncation TruncatedBy,
    int NodesVisited,
    int EdgesVisited,
    double Milliseconds)
{
    public static readonly GraphWalk Empty = new([], Truncation.None, 0, 0, 0);

    public bool Truncated => TruncatedBy != Truncation.None;

    public IReadOnlyList<GraphNode> Nodes => Steps.Select(s => s.Node).ToList();

    public IReadOnlyList<string> Ids => Steps.Select(s => s.Node.Id).ToList();

    /// <summary>The steps at one depth, in the order the walk reached them.</summary>
    public IReadOnlyList<GraphStep> AtDepth(int depth) => Steps.Where(s => s.Depth == depth).ToList();

    /// <summary>The relationships the walk crossed, in order, with repeats: the evidence for whatever it concluded.</summary>
    public IReadOnlyList<GraphEdge> Edges => Steps.Where(s => s.Via is not null).Select(s => s.Via!).ToList();

    /// <summary>The nearest step at an id, or null when the walk did not reach it.</summary>
    public GraphStep? First(string nodeId) => Steps.FirstOrDefault(s => string.Equals(s.Node.Id, nodeId, StringComparison.Ordinal));

    /// <summary>Whether the walk ended at a node: the answer a <see cref="IGraphReader.Path"/> gives, read together with <see cref="TruncatedBy"/>.</summary>
    public bool Reaches(string nodeId) =>
        Steps.Count > 0 && string.Equals(Steps[^1].Node.Id, nodeId, StringComparison.Ordinal);
}

/// <summary>What a write did. <see cref="Refused"/> carries the reason, because a policy that says no without saying why cannot be audited or tested.</summary>
public enum MutationKind
{
    Added,

    /// <summary>The same id again, with new content: the revision went up and the old content is gone, but the old provenance is recorded on the way.</summary>
    Revised,

    Superseded,

    Deleted,

    /// <summary>Nothing was written: the content was already there, or the policy said no.</summary>
    Refused,

    Unchanged,
}

public sealed record GraphMutation(MutationKind Kind, string Id, string? Reason = null)
{
    public bool Applied => Kind is MutationKind.Added or MutationKind.Revised or MutationKind.Superseded or MutationKind.Deleted;

    public static GraphMutation Refuse(string id, string reason) => new(MutationKind.Refused, id, reason);
}

/// <summary>
/// Two claims about one subject in one exclusive relation that cannot both hold at one moment. The evidence is kept on
/// both sides and neither is deleted: the contradiction is the finding, and quietly picking a winner is how a graph
/// launders a bad source into a fact. The Core's <see cref="CoherenceAudit"/> already refuses to resolve its own
/// contradictions; this records which pieces of knowledge are in conflict so that a reader can see them.
/// </summary>
public sealed record GraphContradiction(string Subject, string Relation, DateTimeOffset At, IReadOnlyList<string> EdgeIds)
{
    /// <summary>One line, in the voice the traces use.</summary>
    public string Text => $"{Relation} of {Subject} holds {EdgeIds.Count} ways at {At:O}";
}

/// <summary>What a graph answers. Reads only; every one of them bounded, and every one of them filtered to the caller that opened the session.</summary>
public interface IGraphReader
{
    GraphSchema Schema { get; }

    GraphNode? Node(string id);

    GraphEdge? Edge(string id);

    IReadOnlyList<GraphNode> Nodes(GraphQuery? query = null);

    IReadOnlyList<GraphEdge> Edges(GraphQuery? query = null);

    /// <summary>The relationships out of a node, and — unless the query says otherwise — in through an inverse.</summary>
    IReadOnlyList<GraphEdge> Adjacent(string nodeId, GraphQuery? query = null);

    /// <summary>A breadth-first walk from a node, stopping at whichever bound comes first and saying which.</summary>
    GraphWalk Walk(string from, GraphTraversalOptions? options = null);

    /// <summary>
    /// The shortest relationship chain between two nodes. Never null, because "there is no path" and "the search was
    /// cut short by a bound" are different findings and only the second one leaves room for a path that is there:
    /// read <see cref="GraphWalk.Reaches"/> for the answer and <see cref="GraphWalk.TruncatedBy"/> for how much of the
    /// graph was actually searched. Bounded multi-hop, which is what G01 asks for.
    /// </summary>
    GraphWalk Path(string from, string to, GraphTraversalOptions? options = null);

    /// <summary>The exclusive relations a subject currently holds two ways.</summary>
    IReadOnlyList<GraphContradiction> Contradictions(GraphQuery? query = null);

    /// <summary>
    /// How much knowledge this reader may reach, not how much the store holds. The difference is a fact about other
    /// people's tenants, and a size is as good a leak as a summary.
    /// </summary>
    int NodeCount { get; }

    int EdgeCount { get; }
}

/// <summary>What a graph accepts. Every write is authorized before it is applied and redacted before it is stored.</summary>
public interface IGraphWriter
{
    /// <summary>Adds a node, or revises the one already under that id.</summary>
    GraphMutation Put(GraphNode node);

    /// <summary>Adds a relationship, or revises the one already under that id. Adding a second object to an exclusive relation contradicts the first rather than replacing it.</summary>
    GraphMutation Put(GraphEdge edge);

    /// <summary>
    /// Raises knowledge to <see cref="VerificationStatus.Verified"/>. Refused when the verifier is the source: an agent
    /// may not confirm its own claim, which is the same rule the benchmark's oracles exist to enforce and the one
    /// scenario G02 turns on.
    /// </summary>
    GraphMutation Verify(string id, string evidence, string verifier);

    /// <summary>Answers one piece of knowledge with another. Both stay readable; the older one is marked, not removed.</summary>
    GraphMutation Supersede(string id, string byId);

    /// <summary>Drops the content and keeps the tombstone. What was deleted cannot be read back, and that it was deleted can be proved.</summary>
    GraphMutation Delete(string id, string reason);

    /// <summary>Records that the world supported or contradicted a piece of knowledge, moving its confidence without moving its verification.</summary>
    GraphMutation RecordOutcome(string id, bool held);
}

/// <summary>
/// One caller's view of the graph: every read filtered by the access policy and every write authorized by it. This is
/// what an agent gets, and it never gets the store — an agent that could open its own session at another tenant would
/// not be isolated by anything.
/// </summary>
public interface ICognitiveGraph : IGraphReader, IGraphWriter
{
    GraphCaller Caller { get; }

    IGraphAccessPolicy Policy { get; }
}

/// <summary>
/// The whole graph: its schema, its size, and the sessions it opens. A store is a place knowledge is kept, not a thing
/// an agent talks to. In memory today because that is what an experiment needs (a fixture is rebuilt per run, so a
/// database would only add a thing to reset); the contracts are what a SQLite or a Postgres provider would implement,
/// and issue #109 is explicit that no engine is chosen before the benchmarks say which one is worth it.
/// </summary>
public interface IGraphStore
{
    GraphSchema Schema { get; }

    IGraphAccessPolicy Policy { get; }

    /// <summary>How much the store holds altogether, tombstones apart. A session reports only what its caller may reach; the store reports the size, because whoever runs it is allowed to know.</summary>
    int NodeCount { get; }

    int EdgeCount { get; }

    /// <summary>The graph as one caller sees it. The caller is fixed for the session, so an agent cannot ask as somebody else mid-run.</summary>
    ICognitiveGraph Open(GraphCaller caller);

    /// <summary>Everything one caller may read, as a portable document. The export contract a visualizer or another agent would read (§19 of the design); it respects the policy, because an export that did not would be the easiest leak in the system.</summary>
    GraphSnapshot Export(GraphCaller caller, GraphQuery? query = null);

    /// <summary>Reads a snapshot back in, as this caller, refusing what the policy refuses and reporting what it refused.</summary>
    GraphImport Import(GraphCaller caller, GraphSnapshot snapshot, DateTimeOffset? at = null);
}
