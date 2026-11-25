namespace Creatura.Graph;

/// <summary>
/// The six logical categories of knowledge (docs/cognitive-graph/architecture.md). One enum, not six stores: a
/// relationship usually crosses them — a task an agent experienced in an environment, discovered from a colleague —
/// and splitting the storage would split the traversal that is the whole reason to have a graph.
/// </summary>
public enum GraphCategory
{
    /// <summary>Facts and concepts: an application uses a database, a library depends on a package.</summary>
    Semantic,

    /// <summary>Experience: an agent hit an incident, an action produced an observation, a run produced a result.</summary>
    Episodic,

    /// <summary>What caused what, and how well that is supported. A relationship in time is not one of these.</summary>
    Causal,

    /// <summary>Objectives, their dependencies and how they ended: a deployment waits on a migration.</summary>
    Task,

    /// <summary>Who there is and who they are to each other: an agent collaborates with an agent, a human supervises.</summary>
    Social,

    /// <summary>The world the agent acts in: a device hosts a runtime, a runtime provides a capability.</summary>
    Environmental,
}

/// <summary>
/// How well a piece of knowledge is supported, as a ladder: an agent's claim never outranks an observation, and
/// neither outranks a verification. The Core already keeps this distinction three times over — <see cref="ActionVerdict"/>
/// for one action, <see cref="Hotz.Verdict"/> for one claim, and the bench's outcome class <c>real</c> against
/// <c>claimed</c> for a whole run — and this is the same rule for knowledge that outlives the run. What a model
/// asserts is <see cref="Claimed"/> and never <see cref="Observed"/>: only something the world showed is observed,
/// and only something checked against evidence its asserter could not author is verified.
/// </summary>
public enum VerificationStatus
{
    /// <summary>Proposed to be tested. Carries no weight and is never offered as an answer.</summary>
    Hypothesis = 0,

    /// <summary>Derived from other knowledge. As good as what it was derived from, and no better.</summary>
    Inferred = 1,

    /// <summary>An agent said so. The default, because an unattributed assertion is the least trusted thing there is.</summary>
    Claimed = 2,

    /// <summary>The world showed it: an effect landed, a fixture says so, a registry holds it.</summary>
    Observed = 3,

    /// <summary>Checked against evidence the asserter could not author, by somebody other than the asserter.</summary>
    Verified = 4,
}

/// <summary>
/// Where knowledge is in its life. Separate from <see cref="VerificationStatus"/> on purpose: a contradiction is not a
/// weaker claim, it is a claim in conflict, and the conflict is the finding — so it is kept, never quietly replaced.
/// </summary>
public enum GraphState
{
    Active,

    /// <summary>Held, and contradicted by something at least as well supported. Both sides stay readable.</summary>
    Contradicted,

    /// <summary>A later revision answers this one. Readable, but a retrieval that did not ask for it does not get it.</summary>
    Superseded,

    /// <summary>Gone. The tombstone keeps that it existed and nothing else: deletion has to be provable and the content has to be unretrievable, which is only possible by keeping one and dropping the other.</summary>
    Deleted,
}

/// <summary>
/// When knowledge was written, when the world showed it, and the period it holds for. Four stamps, because collapsing
/// them is how a stale belief survives: a note written today about an ownership that ended last year was
/// <em>written</em> now, <em>observed</em> then, and is valid only over the period it was true for. Nothing here reads
/// a clock — a caller passes the moment it is asking about, as <c>OfferLedger</c> does for an offer's expiry
/// ("readers compare <c>GoalOffer.ExpiresAt</c> with theirs").
/// </summary>
public sealed record GraphStamp
{
    /// <summary>The whole of time: no bound is a bound that always holds.</summary>
    public static readonly GraphStamp Always = new();

    /// <summary>When the world showed it. <see cref="DateTimeOffset"/> compares instants, so an offset is not a bug.</summary>
    public DateTimeOffset Observed { get; init; }

    /// <summary>When this record was written, which is not when the world showed it. Null when nobody recorded the difference.</summary>
    public DateTimeOffset? Written { get; init; }

    /// <summary>The start of the period this holds for; null means it always held.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>The end of the period this holds for, exclusive; null means it still holds.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    public static GraphStamp At(DateTimeOffset observed) => new() { Observed = observed };

    /// <summary>Whether the period covers a moment. Only the validity window is asked: when something was written has no bearing on when it was true.</summary>
    public bool HoldsAt(DateTimeOffset at) =>
        (ValidFrom is null || at >= ValidFrom) && (ValidUntil is null || at < ValidUntil);

    /// <summary>Whether this says anything about time at all. Knowledge with no window is not temporal knowledge, and a query that asked for a moment should not be told it matches.</summary>
    public bool IsTemporal => ValidFrom is not null || ValidUntil is not null;

    public GraphStamp From(DateTimeOffset validFrom) => this with { ValidFrom = validFrom };

    public GraphStamp Until(DateTimeOffset validUntil) => this with { ValidUntil = validUntil };
}

/// <summary>How a piece of knowledge got here. Recorded because it changes what it may be used for, not because it is interesting.</summary>
public enum ProvenanceKind
{
    /// <summary>Hand-written ground truth for an experiment. An upper bound on what the architecture could do, never a measurement of what it does.</summary>
    Oracle,

    /// <summary>Read out of a corpus by an extractor. Its errors are the extractor's, and are priced separately from retrieval's.</summary>
    Extraction,

    /// <summary>The world showed it: an effect landed, a fixture state says so.</summary>
    Observation,

    /// <summary>An agent asserted it. Never rises above <see cref="VerificationStatus.Claimed"/> on the strength of being asserted.</summary>
    Agent,

    /// <summary>Derived from other knowledge that is already in the graph.</summary>
    Inference,

    /// <summary>Carried over from another agent, another model or another store, provenance and all.</summary>
    Import,
}

/// <summary>
/// Where a piece of knowledge came from, and what the source actually said. <see cref="EntryId"/> is the strong form:
/// a ledger entry is signed, content-addressed and globally unique as <c>"cell#sequence"</c>, so knowledge that came
/// through a cell needs no new identity model and no new crypto (docs/design-cells.md). Knowledge that did not come
/// through a cell names its source and nothing more, and stays an agent's claim.
/// </summary>
public sealed record GraphProvenance(ProvenanceKind Kind, string Source)
{
    /// <summary>An assertion from nobody: the default, so that a missing provenance is visible rather than plausible.</summary>
    public static readonly GraphProvenance Unattributed = new(ProvenanceKind.Agent, "");

    /// <summary>The ledger entry that carries it, <c>"cell#sequence"</c>; null when it did not come through a cell.</summary>
    public string? EntryId { get; init; }

    /// <summary>The entry's content hash, so a provenance can be checked against the ledger it names.</summary>
    public string? EntryHash { get; init; }

    /// <summary>The corpus, fixture or document it was read from, with its version — so a result can be reproduced from the same words.</summary>
    public string? Corpus { get; init; }

    /// <summary>What the source said, redacted. Untrusted data: it is evidence about the world, never an instruction to the agent.</summary>
    public string? Evidence { get; init; }

    /// <summary>The model behind an agent claim or an extraction, with its version. Knowledge belongs to the ecosystem, so the provider is recorded and never depended on.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// How it got here, when that differs from what it is: <see cref="ProvenanceKind.Import"/> on knowledge that
    /// arrived in somebody else's snapshot, with <see cref="Kind"/> still saying what it originally was. Keeping the
    /// two apart is what lets an experiment refuse to treat imported knowledge as its own oracle graph while still
    /// letting one agent hand a discovery to another.
    /// </summary>
    public ProvenanceKind? ArrivedAs { get; init; }

    /// <summary>Whether this is ground truth written for an experiment rather than knowledge anybody discovered.</summary>
    public bool IsOracle => Kind == ProvenanceKind.Oracle && ArrivedAs is null;
}

/// <summary>
/// How well supported a piece of knowledge is, as counts and a rate computed from them — never a stored float, which
/// is the one number in a system like this that drifts away from the evidence behind it without anybody noticing.
/// The shape is the Core's own: <see cref="SkillBelief"/>(Supported, Attempted) and <c>MemoryUse</c>(Shown, Good).
/// </summary>
public sealed record GraphSupport(int Supporting, int Total)
{
    public static readonly GraphSupport None = new(0, 0);

    /// <summary>0.5 with no evidence, matching <see cref="SkillBelief.Reliability"/>'s prior: an untested belief is not a false one.</summary>
    public double Confidence => Total == 0 ? 0.5 : (double)Supporting / Total;

    public GraphSupport With(bool held) => new(Supporting + (held ? 1 : 0), Total + 1);
}

/// <summary>
/// Who a piece of knowledge belongs to and who may read it. The Core already reserves the field and no design fills
/// it (<see cref="Identity.Scope"/>: "a light cone's scope, a tenant, a site"; <see cref="LightCone.Scope"/>, which
/// nothing constructs); this is what it would hold. A label is an authorization <em>input</em>, not a boundary: the
/// policy decides, and the policy runs outside the model, so an agent cannot talk its way past it. Where the boundary
/// has to be cryptographic rather than administrative, seal the export to a recipient set the way
/// <c>SharedFiles.Seal</c> does (docs/design-shared-files.md).
/// </summary>
public sealed record GraphScope(string Tenant, string Workspace = "default")
{
    public static readonly GraphScope Default = new("default");

    /// <summary>Who may revise it. Empty means anyone who may write in the workspace.</summary>
    public string Owner { get; init; } = "";

    /// <summary>Who may read it beyond its own workspace. Empty means the workspace alone, which is the default because a share is a decision.</summary>
    public IReadOnlyList<string> Readers { get; init; } = [];

    /// <summary>Whether the scope names the same tenant and workspace as another.</summary>
    public bool SamePlace(GraphScope other) =>
        string.Equals(Tenant, other.Tenant, StringComparison.Ordinal) &&
        string.Equals(Workspace, other.Workspace, StringComparison.Ordinal);

    public GraphScope SharedWith(params string[] readers) => this with { Readers = [.. readers] };

    public override string ToString() => Workspace == "default" ? Tenant : $"{Tenant}/{Workspace}";
}

/// <summary>
/// Everything a piece of knowledge carries beside its content, shared by nodes and edges because a relationship is as
/// much a claim as an entity is. <see cref="Plain"/> is the least trusting default there is — an unattributed agent
/// claim, active, at revision one, holding for all time — so that forgetting to say where knowledge came from leaves
/// it weak rather than leaving it looking strong.
/// </summary>
public sealed record GraphWarrant
{
    public static readonly GraphWarrant Plain = new();

    public GraphStamp Stamps { get; init; } = GraphStamp.Always;

    public GraphProvenance Provenance { get; init; } = GraphProvenance.Unattributed;

    public VerificationStatus Verification { get; init; } = VerificationStatus.Claimed;

    public GraphSupport Support { get; init; } = GraphSupport.None;

    public GraphScope Scope { get; init; } = GraphScope.Default;

    public GraphState State { get; init; } = GraphState.Active;

    /// <summary>Which revision of itself this is. A revision never overwrites the reasoning that produced the last one; it answers it.</summary>
    public long Revision { get; init; } = 1;

    /// <summary>The evidence identifiers behind the verification status. Redacted text, never a secret and never a payload.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>The id of the knowledge that answers this one, when <see cref="State"/> is <see cref="GraphState.Superseded"/>.</summary>
    public string? SupersededBy { get; init; }

    /// <summary>Whether a retrieval that asked for a moment should be given this.</summary>
    public bool HoldsAt(DateTimeOffset at) => Stamps.HoldsAt(at);

    public bool IsRetrievable => State is GraphState.Active or GraphState.Contradicted;
}

/// <summary>
/// One entity: a stable id, what kind of thing it is, and which category of knowledge it belongs to. Ids are opaque
/// and reused across revisions, because knowledge that changes its identity when it changes its mind cannot be
/// revised, only duplicated.
/// </summary>
public sealed record GraphNode(string Id, string EntityType, GraphCategory Category)
{
    private static readonly IReadOnlyDictionary<string, string> NoAttributes =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The shape as written today. A store refuses a node from a newer schema rather than misread it, as <see cref="AgentRecord"/> does.</summary>
    public int Schema { get; init; } = GraphSchema.Current;

    public GraphWarrant Warrant { get; init; } = GraphWarrant.Plain;

    /// <summary>Free text and values, redacted on the way in. Never an identifier: an id that got redacted cannot be traversed.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = NoAttributes;

    /// <summary>One line a seat can read. Redacted, bounded, and not the whole of what the node knows.</summary>
    public string? Summary { get; init; }

    public GraphNode WithWarrant(GraphWarrant warrant) => this with { Warrant = warrant };
}

/// <summary>
/// One typed relationship, as subject-relation-object. The shape follows the only graph the repository already has —
/// <c>MapRoad(Id, A, B)</c> in Creatura.Scenarios, with its <c>Touches</c> and <c>Other</c> — and adds the warrant,
/// because a road on a paper map is static knowledge and a relationship an agent believes is a claim.
/// </summary>
public sealed record GraphEdge(string Id, string Subject, string Relation, string Object, GraphCategory Category)
{
    private static readonly IReadOnlyDictionary<string, string> NoAttributes =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public int Schema { get; init; } = GraphSchema.Current;

    public GraphWarrant Warrant { get; init; } = GraphWarrant.Plain;

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = NoAttributes;

    public string? Summary { get; init; }

    public bool Touches(string nodeId) =>
        string.Equals(nodeId, Subject, StringComparison.Ordinal) || string.Equals(nodeId, Object, StringComparison.Ordinal);

    /// <summary>The node at the other end.</summary>
    public string Other(string nodeId) =>
        string.Equals(nodeId, Subject, StringComparison.Ordinal) ? Object
        : string.Equals(nodeId, Object, StringComparison.Ordinal) ? Subject
        : throw new ArgumentException($"'{Id}' does not touch '{nodeId}'", nameof(nodeId));

    public GraphEdge WithWarrant(GraphWarrant warrant) => this with { Warrant = warrant };

    /// <summary>The same relationship read backwards. Only the schema says whether that is a real relation or nonsense.</summary>
    public GraphEdge Reversed(string? inverseRelation) =>
        new(Id, Object, inverseRelation ?? Relation, Subject, Category) { Schema = Schema, Warrant = Warrant, Attributes = Attributes, Summary = Summary };
}

/// <summary>
/// What a relation means, so that a contradiction is a property of the schema and not a guess made from text. Without
/// this the graph either calls every difference a conflict or calls none of them one.
/// </summary>
public sealed record RelationType(string Name, GraphCategory Category)
{
    /// <summary>One subject may hold at most one object at a time. A second is a contradiction, not a choice to be made silently.</summary>
    public bool Exclusive { get; init; }

    /// <summary>The relation read backwards (<c>owns</c> for <c>ownedBy</c>), so a traversal can follow both ways without the store holding both.</summary>
    public string? Inverse { get; init; }

    /// <summary>
    /// A relationship in time that must not be read as a cause. Nothing promotes one of these to
    /// <see cref="GraphCategory.Causal"/>: a coincidence is a coincidence until a discriminating experiment says
    /// otherwise, which is the whole of scenario G08.
    /// </summary>
    public bool Temporal { get; init; }

    /// <summary>Whether a claim in this relation can be checked against the world at all. An unverifiable relation never reaches <see cref="VerificationStatus.Verified"/>.</summary>
    public bool Verifiable { get; init; } = true;
}

/// <summary>
/// The relations a graph understands, and the schema version they were written under. A relation nobody registered is
/// still storable — a graph that refused unfamiliar knowledge would be useless in exactly the situation knowledge is
/// for — but it is storable as uninterpreted: not exclusive, not invertible, not temporal, and so never a
/// contradiction and never a cause.
/// </summary>
public sealed class GraphSchema
{
    public const int Current = 1;

    /// <summary>
    /// Relations are matched case-insensitively and their spelling is kept, so that a corpus written in
    /// <c>ownedBy</c> and one written in <c>ownedby</c> mean the same relation and the notes a seat reads still look
    /// like words. Entity and node ids stay case-sensitive: they are structural, and folding them would merge two
    /// things that were named apart.
    /// </summary>
    private readonly Dictionary<string, RelationType> _relations = new(StringComparer.OrdinalIgnoreCase);

    public GraphSchema()
    {
    }

    public GraphSchema(IEnumerable<RelationType> relations)
    {
        foreach (var relation in relations) Declare(relation);
    }

    public IReadOnlyList<RelationType> Relations
    {
        get { lock (_relations) return _relations.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList(); }
    }

    public RelationType? Relation(string name)
    {
        lock (_relations) return _relations.TryGetValue(name, out var relation) ? relation : null;
    }

    /// <summary>Adds or replaces a relation's meaning. Returns the schema, so declarations chain.</summary>
    public GraphSchema Declare(RelationType relation)
    {
        lock (_relations) _relations[relation.Name] = relation;
        return this;
    }

    public GraphSchema Declare(string name, GraphCategory category, bool exclusive = false, string? inverse = null, bool temporal = false, bool verifiable = true) =>
        Declare(new RelationType(name, category) { Exclusive = exclusive, Inverse = inverse, Temporal = temporal, Verifiable = verifiable });

    /// <summary>
    /// The relations the benchmark fixtures use (issues #109 and #110). Declared here rather than per fixture so that
    /// every scenario means the same thing by <c>ownedBy</c>, and so that which relations are exclusive is a published
    /// fact about the experiment rather than a choice made inside one test.
    /// </summary>
    public static GraphSchema Benchmark() => new GraphSchema()
        // Semantic.
        .Declare("uses", GraphCategory.Semantic)
        .Declare("dependsOn", GraphCategory.Semantic)
        .Declare("provides", GraphCategory.Semantic, inverse: "providedBy")
        .Declare("exposes", GraphCategory.Semantic)
        // Ownership is single-valued at a moment, which is what makes G03 a temporal question and G04 a contradiction.
        .Declare("ownedBy", GraphCategory.Semantic, exclusive: true, inverse: "owns")
        .Declare("hosts", GraphCategory.Environmental, inverse: "hostedBy")
        .Declare("supports", GraphCategory.Environmental)
        .Declare("contains", GraphCategory.Environmental, inverse: "inside")
        // Episodic.
        .Declare("experienced", GraphCategory.Episodic)
        .Declare("occurredIn", GraphCategory.Episodic)
        .Declare("produced", GraphCategory.Episodic)
        // Causal, and the temporal relations that must not be mistaken for it.
        .Declare("caused", GraphCategory.Causal, inverse: "causedBy")
        .Declare("resolved", GraphCategory.Causal)
        .Declare("preceded", GraphCategory.Causal, temporal: true, verifiable: false)
        .Declare("coincidedWith", GraphCategory.Causal, temporal: true, verifiable: false)
        // Task.
        .Declare("dependsOnTask", GraphCategory.Task, inverse: "unblocks")
        .Declare("blockedBy", GraphCategory.Task, inverse: "blocking")
        .Declare("assignedTo", GraphCategory.Task, exclusive: true, inverse: "assigned")
        // Social.
        .Declare("collaboratesWith", GraphCategory.Social)
        .Declare("supervises", GraphCategory.Social, inverse: "supervisedBy")
        .Declare("responsibleFor", GraphCategory.Social)
        .Declare("escalatesTo", GraphCategory.Social, exclusive: true);
}
