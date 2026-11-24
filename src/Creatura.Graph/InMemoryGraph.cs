using System.Diagnostics;

namespace Creatura.Graph;

/// <summary>One line of the store's own account of what was done to it. Auditability without this is a claim; with it, a deletion and the reason for it survive the deletion.</summary>
public sealed record GraphAuditEntry(DateTimeOffset At, string Caller, MutationKind Kind, string Id, string? Reason)
{
    public string Text => Reason is null
        ? $"{Caller} {Kind.ToString().ToLowerInvariant()} {Id}"
        : $"{Caller} {Kind.ToString().ToLowerInvariant()} {Id}: {Reason}";
}

/// <summary>
/// The graph in memory: dictionaries and two adjacency indexes, every read filtered by an access policy and every
/// write authorized and redacted before it is stored. In memory because that is what an experiment needs — a fixture
/// is rebuilt per run, so a database would only add something to reset — and because issue #109 is explicit that no
/// storage engine is chosen before the benchmarks say which one is worth paying for. What a SQLite or a Postgres
/// provider would implement is <see cref="IGraphStore"/>; the contract is the deliverable, this is the first provider.
/// <para>
/// With no schema every relation is uninterpreted: nothing is exclusive, so nothing contradicts, and nothing is
/// temporal. Pass <see cref="GraphSchema.Benchmark"/> for the relations the fixtures and the documentation use.
/// </para>
/// </summary>
public sealed class InMemoryGraphStore : IGraphStore
{
    /// <summary>What a deleted piece of knowledge is filed under. The id survives so the deletion can be proved; nothing else does.</summary>
    public const string Tombstone = "deleted";

    /// <summary>Why a write whose identifier looked like a credential was refused. The identifier itself is never repeated.</summary>
    public const string RefusedSecret = "an identifier shaped like a credential is refused rather than stored";

    private readonly object _gate = new();
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphEdge> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _out = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _in = new(StringComparer.Ordinal);
    private readonly List<GraphAuditEntry> _audit = [];

    public InMemoryGraphStore(GraphSchema? schema = null, IGraphAccessPolicy? policy = null)
    {
        Schema = schema ?? new GraphSchema();
        Policy = policy ?? WorkspaceAccessPolicy.Instance;
    }

    public GraphSchema Schema { get; }

    public IGraphAccessPolicy Policy { get; }

    public int NodeCount { get { lock (_gate) return _nodes.Values.Count(Held); } }

    public int EdgeCount { get { lock (_gate) return _edges.Values.Count(Held); } }

    /// <summary>The tombstones: knowledge that was deleted, which is how "it is gone" is proved rather than asserted.</summary>
    public int TombstoneCount { get { lock (_gate) return _nodes.Values.Count(Gone) + _edges.Values.Count(Gone); } }

    /// <summary>
    /// Everything the store has been asked to do, in order. A persistent provider would put this in the ledger, where
    /// it is signed; here it is a list, and it is unbounded because a research graph is small and a truncated audit is
    /// worse than a large one.
    /// </summary>
    public IReadOnlyList<GraphAuditEntry> Audit { get { lock (_gate) return [.. _audit]; } }

    public ICognitiveGraph Open(GraphCaller caller) => new GraphSession(this, caller);

    private static bool Held(GraphNode node) => node.Warrant.State != GraphState.Deleted;

    private static bool Held(GraphEdge edge) => edge.Warrant.State != GraphState.Deleted;

    private static bool Gone(GraphNode node) => node.Warrant.State == GraphState.Deleted;

    private static bool Gone(GraphEdge edge) => edge.Warrant.State == GraphState.Deleted;

    // ---------------------------------------------------------------- reads

    internal GraphNode? NodeAs(GraphCaller caller, string id)
    {
        lock (_gate) return _nodes.TryGetValue(id, out var node) && Readable(caller, node.Warrant) ? node : null;
    }

    internal GraphEdge? EdgeAs(GraphCaller caller, string id)
    {
        lock (_gate) return _edges.TryGetValue(id, out var edge) && Readable(caller, edge) ? edge : null;
    }

    internal IReadOnlyList<GraphNode> NodesAs(GraphCaller caller, GraphQuery query)
    {
        lock (_gate)
        {
            return _nodes.Values
                .Where(n => Readable(caller, n.Warrant) && Matches(n, query))
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .Take(query.Bound)
                .ToList();
        }
    }

    internal IReadOnlyList<GraphEdge> EdgesAs(GraphCaller caller, GraphQuery query)
    {
        lock (_gate)
        {
            return _edges.Values
                .Where(e => Readable(caller, e) && Matches(e, query))
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .Take(query.Bound)
                .ToList();
        }
    }

    internal int CountAs(GraphCaller caller, bool nodes)
    {
        lock (_gate)
        {
            return nodes
                ? _nodes.Values.Count(n => Readable(caller, n.Warrant))
                : _edges.Values.Count(e => Readable(caller, e));
        }
    }

    internal IReadOnlyList<GraphEdge> AdjacentAs(GraphCaller caller, string nodeId, GraphQuery query)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(nodeId, out var node) || !Readable(caller, node.Warrant)) return [];
            var found = new List<GraphEdge>();
            foreach (var edge in Outgoing(nodeId))
                if (Readable(caller, edge) && Matches(edge, query)) found.Add(edge);
            if (query.FollowInverse)
                foreach (var (edge, inverse) in Incoming(nodeId))
                {
                    if (inverse is null || !Readable(caller, edge) || !Matches(edge, query)) continue;
                    found.Add(edge.Reversed(inverse));
                }

            return found.OrderBy(e => e.Id, StringComparer.Ordinal).Take(query.Bound).ToList();
        }
    }

    /// <summary>
    /// A breadth-first walk. The visited set is not optional and cannot be switched off, so a cycle costs one visit and
    /// not the run; every other bound stops the walk and says which one stopped it, because "the answer was not there"
    /// and "the answer was past the bound" are different findings and an experiment that cannot tell them apart cannot
    /// report a negative result honestly.
    /// </summary>
    internal GraphWalk WalkAs(GraphCaller caller, string from, GraphTraversalOptions options)
    {
        var clock = Stopwatch.StartNew();
        lock (_gate)
        {
            if (!_nodes.TryGetValue(from, out var start) || !Readable(caller, start.Warrant)) return GraphWalk.Empty;
            var steps = new List<GraphStep> { new(0, start, null) };
            var visited = new HashSet<string>(StringComparer.Ordinal) { from };
            var queue = new Queue<(string Id, int Depth)>();
            queue.Enqueue((from, 0));
            var edgesVisited = 0;
            var cut = Truncation.None;
            while (queue.Count > 0 && cut == Truncation.None)
            {
                if (Over(options, clock)) { cut = Truncation.Duration; break; }
                if (steps.Count >= options.Limit) { cut = Truncation.Limit; break; }
                var (id, depth) = queue.Dequeue();
                if (depth >= options.MaxDepth) { cut = Truncation.Depth; continue; }
                foreach (var edge in Expand(caller, id, options))
                {
                    if (++edgesVisited > options.MaxEdges) { cut = Truncation.Edges; break; }
                    var next = edge.Other(id);
                    if (!visited.Add(next)) continue;
                    if (visited.Count > options.MaxNodes) { cut = Truncation.Nodes; break; }
                    if (!_nodes.TryGetValue(next, out var node) || !Readable(caller, node.Warrant)) continue;
                    steps.Add(new GraphStep(depth + 1, node, edge));
                    queue.Enqueue((next, depth + 1));
                    if (Over(options, clock)) { cut = Truncation.Duration; break; }
                    if (steps.Count >= options.Limit) { cut = Truncation.Limit; break; }
                }
            }

            return new GraphWalk(steps, cut, visited.Count, edgesVisited, Elapsed(clock));
        }
    }

    /// <summary>
    /// The shortest chain between two nodes, breadth-first, so the first time the target is reached is the shortest way
    /// there. The walk comes back whether or not it got there: read <see cref="GraphWalk.Reaches"/> for the answer and
    /// <see cref="GraphWalk.TruncatedBy"/> for how much of the graph was searched before the walk gave up.
    /// </summary>
    internal GraphWalk PathAs(GraphCaller caller, string from, string to, GraphTraversalOptions options)
    {
        var clock = Stopwatch.StartNew();
        lock (_gate)
        {
            if (!_nodes.TryGetValue(from, out var start) || !Readable(caller, start.Warrant)) return GraphWalk.Empty;
            if (string.Equals(from, to, StringComparison.Ordinal))
                return new GraphWalk([new GraphStep(0, start, null)], Truncation.None, 1, 0, Elapsed(clock));

            var cameFrom = new Dictionary<string, (string Via, GraphEdge Edge)>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { from };
            var queue = new Queue<(string Id, int Depth)>();
            queue.Enqueue((from, 0));
            var edgesVisited = 0;
            var cut = Truncation.None;
            while (queue.Count > 0 && cut == Truncation.None)
            {
                if (Over(options, clock)) { cut = Truncation.Duration; break; }
                var (id, depth) = queue.Dequeue();
                if (depth >= options.MaxDepth) { cut = Truncation.Depth; continue; }
                foreach (var edge in Expand(caller, id, options))
                {
                    if (++edgesVisited > options.MaxEdges) { cut = Truncation.Edges; break; }
                    var next = edge.Other(id);
                    if (!visited.Add(next)) continue;
                    if (visited.Count > options.MaxNodes) { cut = Truncation.Nodes; break; }
                    if (!_nodes.TryGetValue(next, out var node) || !Readable(caller, node.Warrant)) continue;
                    cameFrom[next] = (id, edge);
                    if (string.Equals(next, to, StringComparison.Ordinal))
                        return Chain(start, to, cameFrom, visited.Count, edgesVisited, clock, Truncation.None);
                    queue.Enqueue((next, depth + 1));
                    if (Over(options, clock)) { cut = Truncation.Duration; break; }
                }
            }

            // Not reached. The walk says how far it got before giving up, so an absent path inside a bound is not
            // reported as an absent path.
            return new GraphWalk([], cut, visited.Count, edgesVisited, Elapsed(clock));
        }
    }

    /// <summary>
    /// The exclusive relations one subject currently holds two ways, recomputed from what is stored rather than kept as
    /// state: a contradiction list that could drift from the edges would be the one number nobody can reproduce. Only
    /// edges the caller may read take part, so that probing for a contradiction cannot become a way of learning what
    /// another tenant holds.
    /// </summary>
    internal IReadOnlyList<GraphContradiction> ContradictionsAs(GraphCaller caller, GraphQuery query)
    {
        lock (_gate)
        {
            // The query's state filter is dropped and replaced by "retrievable": a contradiction IS a state, so a
            // search that asked for active knowledge would filter out the very edges the conflict is made of. What is
            // still excluded is superseded and deleted knowledge, which no longer answers to anything.
            var scope = query with { State = null };
            var found = new List<GraphContradiction>();
            // Grouped on the case-folded relation, because two spellings of one relation are one relation and a
            // conflict between them is still a conflict.
            foreach (var group in _edges.Values
                         .Where(e => Readable(caller, e) && e.Warrant.IsRetrievable && Matches(e, scope) && Schema.Relation(e.Relation) is { Exclusive: true })
                         .GroupBy(e => (e.Subject, Relation: e.Relation.ToLowerInvariant())))
            {
                var held = group.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
                for (var i = 0; i < held.Count; i++)
                for (var j = i + 1; j < held.Count; j++)
                {
                    if (!Overlaps(held[i].Warrant.Stamps, held[j].Warrant.Stamps)) continue;
                    var at = query.At ?? held[i].Warrant.Stamps.Observed;
                    found.Add(new GraphContradiction(group.Key.Subject, held[i].Relation, at, [held[i].Id, held[j].Id]));
                }
            }

            return found
                .OrderBy(c => c.Subject, StringComparer.Ordinal)
                .ThenBy(c => c.Relation, StringComparer.Ordinal)
                .Take(query.Bound)
                .ToList();
        }
    }

    // ---------------------------------------------------------------- writes

    internal GraphMutation PutNodeAs(GraphCaller caller, GraphNode node, DateTimeOffset at)
    {
        node = Clean(node);
        if (node.Schema > GraphSchema.Current) return GraphMutation.Refuse(node.Id, $"schema {node.Schema} is newer than {GraphSchema.Current}");
        // An identifier is never redacted, so one shaped like a credential is refused instead — and the refusal itself
        // is scrubbed, because a reason string that echoed the key back would be the leak it was trying to prevent.
        if (GraphRedaction.LooksLikeSecret(node.Id) || GraphRedaction.LooksLikeSecret(node.EntityType))
            return GraphMutation.Refuse(GraphRedaction.SafeId(node.Id), RefusedSecret);
        lock (_gate)
        {
            if (!Policy.MayWrite(caller, node.Warrant.Scope)) return GraphMutation.Refuse(node.Id, $"{caller} may not write in {node.Warrant.Scope}");
            _nodes.TryGetValue(node.Id, out var existing);
            if (existing is { Warrant.State: GraphState.Deleted })
                return GraphMutation.Refuse(node.Id, "deleted knowledge does not come back under the same id");
            if (existing is not null && !Policy.MayWrite(caller, existing.Warrant.Scope))
                return GraphMutation.Refuse(node.Id, $"{caller} may not revise knowledge held in {existing.Warrant.Scope}");

            var kind = existing is null ? MutationKind.Added : MutationKind.Revised;
            _nodes[node.Id] = existing is null
                ? node with { Schema = GraphSchema.Current }
                : node with
                {
                    Schema = GraphSchema.Current,
                    Warrant = node.Warrant with { Revision = existing.Warrant.Revision + 1, SupersededBy = null },
                };
            _audit.Add(new GraphAuditEntry(at, caller.ToString(), kind, node.Id, null));
            return new GraphMutation(kind, node.Id);
        }
    }

    internal GraphMutation PutEdgeAs(GraphCaller caller, GraphEdge edge, DateTimeOffset at)
    {
        edge = Clean(edge);
        if (edge.Schema > GraphSchema.Current) return GraphMutation.Refuse(edge.Id, $"schema {edge.Schema} is newer than {GraphSchema.Current}");
        if (GraphRedaction.LooksLikeSecret(edge.Id) || GraphRedaction.LooksLikeSecret(edge.Subject)
            || GraphRedaction.LooksLikeSecret(edge.Relation) || GraphRedaction.LooksLikeSecret(edge.Object))
            return GraphMutation.Refuse(GraphRedaction.SafeId(edge.Id), RefusedSecret);
        lock (_gate)
        {
            if (!Policy.MayWrite(caller, edge.Warrant.Scope)) return GraphMutation.Refuse(edge.Id, $"{caller} may not write in {edge.Warrant.Scope}");
            _edges.TryGetValue(edge.Id, out var existing);
            if (existing is { Warrant.State: GraphState.Deleted })
                return GraphMutation.Refuse(edge.Id, "deleted knowledge does not come back under the same id");
            if (existing is not null && !Policy.MayWrite(caller, existing.Warrant.Scope))
                return GraphMutation.Refuse(edge.Id, $"{caller} may not revise knowledge held in {existing.Warrant.Scope}");

            var stored = existing is null
                ? edge with { Schema = GraphSchema.Current }
                : edge with
                {
                    Schema = GraphSchema.Current,
                    Warrant = edge.Warrant with { Revision = existing.Warrant.Revision + 1, SupersededBy = null },
                };

            // An exclusive relation held two ways at once is a contradiction, and both sides stay: replacing the first
            // with the second would launder whichever source spoke last into a fact.
            var rivals = Schema.Relation(edge.Relation) is { Exclusive: true }
                ? Rivals(caller, stored).ToList()
                : [];

            if (existing is not null) Unindex(existing);
            Index(stored);

            if (rivals.Count == 0)
            {
                var kind = existing is null ? MutationKind.Added : MutationKind.Revised;
                _edges[edge.Id] = stored;
                _audit.Add(new GraphAuditEntry(at, caller.ToString(), kind, edge.Id, null));
                return new GraphMutation(kind, edge.Id);
            }

            foreach (var rival in rivals)
                _edges[rival.Id] = rival with { Warrant = rival.Warrant with { State = GraphState.Contradicted } };
            _edges[edge.Id] = stored with { Warrant = stored.Warrant with { State = GraphState.Contradicted } };
            var why = $"contradicts {string.Join(", ", rivals.Select(r => r.Id).OrderBy(r => StringComparer.Ordinal))}";
            _audit.Add(new GraphAuditEntry(at, caller.ToString(), MutationKind.Added, edge.Id, why));
            return new GraphMutation(MutationKind.Added, edge.Id, why);
        }
    }

    internal GraphMutation VerifyAs(GraphCaller caller, string id, string evidence, string verifier, DateTimeOffset at)
    {
        var clean = GraphRedaction.Evidence(evidence);
        if (clean is null) return GraphMutation.Refuse(id, "verification needs evidence");
        lock (_gate)
        {
            var warrant = WarrantOf(caller, id);
            if (warrant is null) return GraphMutation.Refuse(id, "no such knowledge, or none this caller may revise");
            if (!Policy.MayWrite(caller, warrant.Scope)) return GraphMutation.Refuse(id, $"{caller} may not write in {warrant.Scope}");
            // The rule the whole benchmark rests on: an agent may not confirm its own claim.
            if (string.Equals(verifier, warrant.Provenance.Source, StringComparison.Ordinal))
                return GraphMutation.Refuse(id, $"{verifier} asserted this and may not verify it");
            if (_edges.TryGetValue(id, out var edge))
            {
                var relation = Schema.Relation(edge.Relation);
                if (relation is { Temporal: true })
                    return GraphMutation.Refuse(id, $"'{edge.Relation}' is a relationship in time; a coincidence is not a cause and nothing verifies it");
                if (relation is { Verifiable: false })
                    return GraphMutation.Refuse(id, $"'{edge.Relation}' cannot be checked against the world");
            }

            // Verifying one side of a contradiction does not resolve it: a contradiction is answered by superseding one
            // side, never by strengthening the other.
            Revise(id, warrant with { Verification = VerificationStatus.Verified, Evidence = [.. warrant.Evidence, clean] });
            _audit.Add(new GraphAuditEntry(at, caller.ToString(), MutationKind.Revised, id, $"verified by {verifier}"));
            return new GraphMutation(MutationKind.Revised, id, $"verified by {verifier}");
        }
    }

    internal GraphMutation RecordOutcomeAs(GraphCaller caller, string id, bool held, DateTimeOffset at)
    {
        lock (_gate)
        {
            var warrant = WarrantOf(caller, id);
            if (warrant is null) return GraphMutation.Refuse(id, "no such knowledge, or none this caller may revise");
            if (!Policy.MayWrite(caller, warrant.Scope)) return GraphMutation.Refuse(id, $"{caller} may not write in {warrant.Scope}");
            Revise(id, warrant with { Support = warrant.Support.With(held) });
            _audit.Add(new GraphAuditEntry(at, caller.ToString(), MutationKind.Revised, id, held ? "supported" : "contradicted by the outcome"));
            return new GraphMutation(MutationKind.Revised, id, held ? "supported" : "contradicted by the outcome");
        }
    }

    internal GraphMutation SupersedeAs(GraphCaller caller, string id, string byId, DateTimeOffset at)
    {
        lock (_gate)
        {
            var warrant = WarrantOf(caller, id);
            if (warrant is null) return GraphMutation.Refuse(id, "no such knowledge, or none this caller may revise");
            if (!Policy.MayWrite(caller, warrant.Scope)) return GraphMutation.Refuse(id, $"{caller} may not write in {warrant.Scope}");
            if (WarrantOf(caller, byId) is null) return GraphMutation.Refuse(id, $"'{byId}' does not answer it: nothing this caller may read to supersede with");
            Revise(id, warrant with { State = GraphState.Superseded, SupersededBy = byId });
            _audit.Add(new GraphAuditEntry(at, caller.ToString(), MutationKind.Superseded, id, $"answered by {byId}"));
            return new GraphMutation(MutationKind.Superseded, id, $"answered by {byId}");
        }
    }

    internal GraphMutation DeleteAs(GraphCaller caller, string id, string reason, DateTimeOffset at)
    {
        lock (_gate)
        {
            // The raw warrant, so that a second deletion reports "already deleted" rather than pretending the
            // knowledge was never there — the difference between an idempotent delete and a lost one.
            var warrant = RawWarrantOf(caller, id);
            if (warrant is null) return GraphMutation.Refuse(id, "no such knowledge, or none this caller may revise");
            if (!Policy.MayWrite(caller, warrant.Scope)) return GraphMutation.Refuse(id, $"{caller} may not write in {warrant.Scope}");
            if (warrant.State == GraphState.Deleted) return new GraphMutation(MutationKind.Unchanged, id, "already deleted");
            // The tombstone keeps the id and drops everything else. Erasing the id too would make the deletion
            // unauditable; keeping the content would make the deletion meaningless.
            var stone = warrant with { State = GraphState.Deleted, Evidence = [], SupersededBy = null };
            if (_nodes.TryGetValue(id, out var node))
            {
                _nodes[id] = new GraphNode(id, Tombstone, node.Category) { Warrant = stone };
            }
            else if (_edges.TryGetValue(id, out var edge))
            {
                Unindex(edge);
                _edges[id] = new GraphEdge(id, "", Tombstone, "", edge.Category) { Warrant = stone };
            }

            _audit.Add(new GraphAuditEntry(at, caller.ToString(), MutationKind.Deleted, id, reason));
            return new GraphMutation(MutationKind.Deleted, id, reason);
        }
    }

    // ---------------------------------------------------------------- export and import

    public GraphSnapshot Export(GraphCaller caller, GraphQuery? query = null)
    {
        var q = query ?? GraphQuery.All;
        var nodes = NodesAs(caller, q);
        var kept = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.Ordinal);
        // A relationship with an end that did not come through the filter is not exported: a half edge in a snapshot is
        // a claim about a node the reader was not allowed to see.
        var edges = EdgesAs(caller, q).Where(e => kept.Contains(e.Subject) && kept.Contains(e.Object)).ToList();
        var relations = edges
            .Select(e => Schema.Relation(e.Relation))
            .Where(r => r is not null)
            .Select(r => r!)
            .DistinctBy(r => r.Name)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
        return new GraphSnapshot
        {
            Exported = DateTimeOffset.UtcNow,
            ExportedBy = caller.ToString(),
            Scope = caller.Scope,
            Relations = relations,
            Nodes = nodes,
            Edges = edges,
        };
    }

    /// <summary>
    /// Reads a snapshot in as this caller. The scope becomes the importer's own, because a document that could grant
    /// itself a tenant would turn every export into a privilege escalation; the provenance is kept verbatim and marked
    /// as having arrived by import, so who originally asserted what still travels with the knowledge (G06, G15) while
    /// an experiment can still tell its own oracle graph from somebody else's claim.
    /// </summary>
    public GraphImport Import(GraphCaller caller, GraphSnapshot snapshot, DateTimeOffset? at = null)
    {
        var stamp = at ?? DateTimeOffset.UtcNow;
        var added = 0;
        var revised = 0;
        var refused = new List<GraphMutation>();
        var scope = caller.Scope with { Owner = caller.Agent };
        foreach (var node in snapshot.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
            Tally(PutNodeAs(caller, node with { Warrant = Rescope(node.Warrant, scope) }, stamp), node.Id);
        foreach (var edge in snapshot.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
            Tally(PutEdgeAs(caller, edge with { Warrant = Rescope(edge.Warrant, scope) }, stamp), edge.Id);
        return new GraphImport(added, revised, refused);

        void Tally(GraphMutation mutation, string id)
        {
            switch (mutation.Kind)
            {
                case MutationKind.Added: added++; break;
                case MutationKind.Revised: revised++; break;
                default: refused.Add(mutation with { Id = id }); break;
            }
        }
    }

    private static GraphWarrant Rescope(GraphWarrant warrant, GraphScope scope) => warrant with
    {
        Scope = scope,
        Provenance = warrant.Provenance with { ArrivedAs = ProvenanceKind.Import },
    };

    // ---------------------------------------------------------------- the internals

    /// <summary>Whether the policy lets this caller read knowledge with this warrant. Deleted knowledge is never readable, whatever the policy says.</summary>
    private bool Readable(GraphCaller caller, GraphWarrant warrant) =>
        warrant.State != GraphState.Deleted && Policy.MayRead(caller, warrant.Scope);

    private bool Readable(GraphCaller caller, GraphNode node) => Readable(caller, node.Warrant);

    /// <summary>
    /// Whether a relationship is readable, which is one condition more than a node's: neither end may have been
    /// deleted. Deleting a node and leaving the relationships that named it would leak the deleted id, its relations
    /// and its neighbours to anybody who could still read them, which is the deletion G10 asks to be provable.
    /// An endpoint that was never a node is not a leak — the store does not require one — and does not block the read.
    /// </summary>
    private bool Readable(GraphCaller caller, GraphEdge edge) =>
        Readable(caller, edge.Warrant) && !IsDeleted(edge.Subject) && !IsDeleted(edge.Object);

    private bool IsDeleted(string id) => _nodes.TryGetValue(id, out var node) && node.Warrant.State == GraphState.Deleted;

    private static bool Matches(GraphNode node, GraphQuery q) =>
        (q.Category is null || node.Category == q.Category) &&
        (q.EntityType is null || string.Equals(node.EntityType, q.EntityType, StringComparison.Ordinal)) &&
        (q.State is null || node.Warrant.State == q.State) &&
        (q.MinVerification is null || node.Warrant.Verification >= q.MinVerification) &&
        (q.At is null || node.Warrant.Stamps.HoldsAt(q.At.Value)) &&
        (q.Provenance is null || node.Warrant.Provenance.Kind == q.Provenance) &&
        (q.Corpus is null || string.Equals(node.Warrant.Provenance.Corpus, q.Corpus, StringComparison.Ordinal));

    private static bool Matches(GraphEdge edge, GraphQuery q) =>
        (q.Category is null || edge.Category == q.Category) &&
        (q.Relation is null || string.Equals(edge.Relation, q.Relation, StringComparison.OrdinalIgnoreCase)) &&
        (q.Subject is null || string.Equals(edge.Subject, q.Subject, StringComparison.Ordinal)) &&
        (q.Object is null || string.Equals(edge.Object, q.Object, StringComparison.Ordinal)) &&
        (q.State is null || edge.Warrant.State == q.State) &&
        (q.MinVerification is null || edge.Warrant.Verification >= q.MinVerification) &&
        (q.At is null || edge.Warrant.Stamps.HoldsAt(q.At.Value)) &&
        (q.Provenance is null || edge.Warrant.Provenance.Kind == q.Provenance) &&
        (q.Corpus is null || string.Equals(edge.Warrant.Provenance.Corpus, q.Corpus, StringComparison.Ordinal));

    private IEnumerable<GraphEdge> Outgoing(string nodeId) =>
        _out.TryGetValue(nodeId, out var ids) ? ids.Select(EdgeAt).Where(e => e is not null).Select(e => e!) : [];

    private IEnumerable<(GraphEdge Edge, string? Inverse)> Incoming(string nodeId) =>
        _in.TryGetValue(nodeId, out var ids)
            ? ids.Select(EdgeAt).Where(e => e is not null).Select(e => (e!, Schema.Relation(e!.Relation)?.Inverse))
            : [];

    private GraphEdge? EdgeAt(string id) => _edges.TryGetValue(id, out var edge) ? edge : null;

    /// <summary>The edges this caller may cross from a node, in both directions when the schema gives the relation an inverse, ordered by the caller's ranking and then by id so that the same graph walks the same way every time.</summary>
    private IEnumerable<GraphEdge> Expand(GraphCaller caller, string nodeId, GraphTraversalOptions options)
    {
        var found = new List<GraphEdge>();
        foreach (var edge in Outgoing(nodeId))
            if (Crossable(caller, edge, options)) found.Add(edge);
        if (options.FollowInverse)
            foreach (var (edge, inverse) in Incoming(nodeId))
            {
                if (inverse is null || !Crossable(caller, edge, options)) continue;
                found.Add(edge.Reversed(inverse));
            }

        return options.Rank is null
            ? found.OrderBy(e => e.Id, StringComparer.Ordinal)
            : found.OrderBy(options.Rank).ThenBy(e => e.Id, StringComparer.Ordinal);
    }

    private bool Crossable(GraphCaller caller, GraphEdge edge, GraphTraversalOptions options) =>
        Readable(caller, edge) &&
        edge.Warrant.IsRetrievable &&
        (options.MinVerification is null || edge.Warrant.Verification >= options.MinVerification) &&
        (options.At is null || edge.Warrant.Stamps.HoldsAt(options.At.Value)) &&
        (options.Usable?.Invoke(edge) ?? true);

    private void Index(GraphEdge edge)
    {
        if (!_out.TryGetValue(edge.Subject, out var outs)) _out[edge.Subject] = outs = [];
        if (!outs.Contains(edge.Id, StringComparer.Ordinal)) outs.Add(edge.Id);
        if (!_in.TryGetValue(edge.Object, out var ins)) _in[edge.Object] = ins = [];
        if (!ins.Contains(edge.Id, StringComparer.Ordinal)) ins.Add(edge.Id);
    }

    private void Unindex(GraphEdge edge)
    {
        if (_out.TryGetValue(edge.Subject, out var outs)) outs.Remove(edge.Id);
        if (_in.TryGetValue(edge.Object, out var ins)) ins.Remove(edge.Id);
    }

    /// <summary>The readable edges that hold the same exclusive relation for the same subject over an overlapping period, and are not this one.</summary>
    private IEnumerable<GraphEdge> Rivals(GraphCaller caller, GraphEdge edge)
    {
        foreach (var other in Outgoing(edge.Subject))
        {
            if (string.Equals(other.Id, edge.Id, StringComparison.Ordinal)) continue;
            if (!Readable(caller, other) || !other.Warrant.IsRetrievable) continue;
            if (!string.Equals(other.Relation, edge.Relation, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(other.Object, edge.Object, StringComparison.Ordinal)) continue;
            if (!Overlaps(other.Warrant.Stamps, edge.Warrant.Stamps)) continue;
            yield return other;
        }
    }

    /// <summary>Whether two validity periods share a moment. An unbounded period shares one with everything, which is why knowledge with no window is not temporal knowledge.</summary>
    internal static bool Overlaps(GraphStamp a, GraphStamp b)
    {
        var aFrom = a.ValidFrom ?? DateTimeOffset.MinValue;
        var aUntil = a.ValidUntil ?? DateTimeOffset.MaxValue;
        var bFrom = b.ValidFrom ?? DateTimeOffset.MinValue;
        var bUntil = b.ValidUntil ?? DateTimeOffset.MaxValue;
        return aFrom < bUntil && bFrom < aUntil;
    }

    /// <summary>The warrant of a piece of knowledge this caller may both read and find. A read that is refused and a read of something absent return the same null, so an agent cannot probe for what another tenant holds.</summary>
    private GraphWarrant? WarrantOf(GraphCaller caller, string id)
    {
        if (_nodes.TryGetValue(id, out var node) && Readable(caller, node)) return node.Warrant;
        if (_edges.TryGetValue(id, out var edge) && Readable(caller, edge)) return edge.Warrant;
        return null;
    }

    /// <summary>
    /// The warrant of a tombstone as well, so that deleting something twice can be told apart from deleting something
    /// that never existed. Still filtered by the read policy: a tombstone in another tenant is nobody else's business.
    /// </summary>
    private GraphWarrant? RawWarrantOf(GraphCaller caller, string id)
    {
        if (_nodes.TryGetValue(id, out var node) && Policy.MayRead(caller, node.Warrant.Scope)) return node.Warrant;
        if (_edges.TryGetValue(id, out var edge) && Policy.MayRead(caller, edge.Warrant.Scope)) return edge.Warrant;
        return null;
    }

    private void Revise(string id, GraphWarrant warrant)
    {
        if (_nodes.TryGetValue(id, out var node)) _nodes[id] = node with { Warrant = warrant };
        else if (_edges.TryGetValue(id, out var edge)) _edges[id] = edge with { Warrant = warrant };
    }

    private static GraphNode Clean(GraphNode node) => node with
    {
        Summary = GraphRedaction.Summary(node.Summary),
        Attributes = GraphRedaction.Attributes(node.Attributes),
        Warrant = Clean(node.Warrant),
    };

    private static GraphEdge Clean(GraphEdge edge) => edge with
    {
        Summary = GraphRedaction.Summary(edge.Summary),
        Attributes = GraphRedaction.Attributes(edge.Attributes),
        Warrant = Clean(edge.Warrant),
    };

    private static GraphWarrant Clean(GraphWarrant warrant) => warrant with
    {
        Evidence = GraphRedaction.Lines(warrant.Evidence),
        Provenance = warrant.Provenance with { Evidence = GraphRedaction.Evidence(warrant.Provenance.Evidence) },
    };

    private static bool Over(GraphTraversalOptions options, Stopwatch clock) => clock.Elapsed > options.MaxDuration;

    private static double Elapsed(Stopwatch clock)
    {
        clock.Stop();
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>The path back out of the parent map, as real nodes: the walk reports what is in the graph, never a stand-in for it.</summary>
    private GraphWalk Chain(GraphNode start, string to, IReadOnlyDictionary<string, (string Via, GraphEdge Edge)> cameFrom, int nodes, int edges, Stopwatch clock, Truncation cut)
    {
        var links = new List<(string Via, GraphEdge Edge)>();
        var cursor = to;
        while (cameFrom.TryGetValue(cursor, out var link))
        {
            links.Add(link);
            cursor = link.Via;
        }

        links.Reverse();
        var steps = new List<GraphStep> { new(0, start, null) };
        for (var i = 0; i < links.Count; i++)
        {
            var id = links[i].Edge.Other(links[i].Via);
            var node = _nodes.TryGetValue(id, out var found)
                ? found
                : new GraphNode(id, "", links[i].Edge.Category);
            steps.Add(new GraphStep(i + 1, node, links[i].Edge));
        }

        return new GraphWalk(steps, cut, nodes, edges, Elapsed(clock));
    }
}

/// <summary>
/// One caller's view of a store: the same knowledge, filtered to what the access policy admits. Thin on purpose — the
/// policy lives in the store so that two sessions over one graph cannot disagree about it, and the caller is fixed for
/// the life of the session so an agent cannot start asking as somebody else mid-run.
/// </summary>
public sealed class GraphSession : ICognitiveGraph
{
    private readonly InMemoryGraphStore _store;

    public GraphSession(InMemoryGraphStore store, GraphCaller caller)
    {
        _store = store;
        Caller = caller;
    }

    public GraphCaller Caller { get; }

    public IGraphAccessPolicy Policy => _store.Policy;

    public GraphSchema Schema => _store.Schema;

    public int NodeCount => _store.CountAs(Caller, nodes: true);

    public int EdgeCount => _store.CountAs(Caller, nodes: false);

    public GraphNode? Node(string id) => _store.NodeAs(Caller, id);

    public GraphEdge? Edge(string id) => _store.EdgeAs(Caller, id);

    public IReadOnlyList<GraphNode> Nodes(GraphQuery? query = null) => _store.NodesAs(Caller, query ?? GraphQuery.All);

    public IReadOnlyList<GraphEdge> Edges(GraphQuery? query = null) => _store.EdgesAs(Caller, query ?? GraphQuery.All);

    public IReadOnlyList<GraphEdge> Adjacent(string nodeId, GraphQuery? query = null) => _store.AdjacentAs(Caller, nodeId, query ?? GraphQuery.All);

    public GraphWalk Walk(string from, GraphTraversalOptions? options = null) => _store.WalkAs(Caller, from, options ?? GraphTraversalOptions.Default);

    public GraphWalk Path(string from, string to, GraphTraversalOptions? options = null) => _store.PathAs(Caller, from, to, options ?? GraphTraversalOptions.Default);

    public IReadOnlyList<GraphContradiction> Contradictions(GraphQuery? query = null) => _store.ContradictionsAs(Caller, query ?? GraphQuery.All);

    public GraphMutation Put(GraphNode node) => _store.PutNodeAs(Caller, node, DateTimeOffset.UtcNow);

    public GraphMutation Put(GraphEdge edge) => _store.PutEdgeAs(Caller, edge, DateTimeOffset.UtcNow);

    public GraphMutation Verify(string id, string evidence, string verifier) => _store.VerifyAs(Caller, id, evidence, verifier, DateTimeOffset.UtcNow);

    public GraphMutation RecordOutcome(string id, bool held) => _store.RecordOutcomeAs(Caller, id, held, DateTimeOffset.UtcNow);

    public GraphMutation Supersede(string id, string byId) => _store.SupersedeAs(Caller, id, byId, DateTimeOffset.UtcNow);

    public GraphMutation Delete(string id, string reason) => _store.DeleteAs(Caller, id, reason, DateTimeOffset.UtcNow);
}
