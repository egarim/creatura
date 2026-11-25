# The cognitive graph: security and knowledge governance

A shared memory system that anyone can write to and anyone can read from is not a knowledge base; it is a
broadcast channel with persistence. This is what `Creatura.Graph` does about that, and — more usefully — what it
does not yet do.

The governing principle is the repository's own (`docs/artificial-cognition.md`, principle 6): treat observations as
untrusted, enforce authorization outside the model, and keep secrets out of cognitive memory.

## Retrieved content is data

Nothing the graph returns is an instruction. A note reaches a seat as a string rendered by
`GraphMemoryExtension.Render`, prefixed with what it is and how well it is held:

```
knowledge (observed): service:orders ownedBy team:payments
knowledge (claimed, contradicted): restart caused recovery
```

It cannot grant a tool, cannot add a fact to the registry, cannot lift a stage gate and cannot change a policy. The
control layer still checks every effect against the world, and the world is still the only truth. A graph that says
"you are authorized" has said a string.

## Narrowing only

The one place the extension changes what an agent may do is `VisibleTools`, and it can only ever subtract:

```csharp
return held.Count == 0 ? visible : visible.Where(t => !held.Contains(t.Id)).ToList();
```

No code path in `Creatura.Graph` adds a tool, grants a fact, raises a freedom value or suppresses a hand-off.
`IAgentExtension` offers all of those hooks; this extension implements none of them.

That asymmetry is the whole of the poisoning defence that lives in the extension rather than in the store, and it
is what makes scenario G09 a testable property instead of a hope. A malicious observation can make an agent do
less — withhold a tool it needed, bury a note under noise, cost it ticks. It cannot make an agent do something the
world did not already allow. The worst case is a degraded agent, not a compromised one.

The withholding itself is also gated: a `blockedBy` edge has to be at least `Observed` before it can hide a tool,
so a bare claim cannot disable anything. And the condition is checked against the registry, not against the graph —
the graph says what the tool waits for, and the world says whether that is satisfied.

## Authorization

`IGraphAccessPolicy` runs outside the model, on every read and every write, in the store rather than in the
extension — so two sessions over one graph cannot disagree about it.

`WorkspaceAccessPolicy`, the default:

| | rule |
|---|---|
| read | same tenant, **and** same workspace **or** named in the scope's `Readers` (by workspace or by agent) |
| write | same tenant **and** same workspace, **and** the scope names no owner or names this caller |

A tenant is a wall. A workspace is a room in it. Knowledge leaves a room only when somebody said to share it, and
the default is not shared, because a graph that starts open has to be closed later and everything written before
then is already leaked.

Three properties worth stating explicitly, each with a test:

- **A refusal looks like an absence.** `Node("theirs")` and `Node("absent")` both return null for a caller who may
  not read; `Verify`, `Delete` and `Supersede` refuse identically for both. A wall that answers differently for the
  two can be probed for what is behind it.
- **A session's counts are the caller's, not the store's.** `ICognitiveGraph.NodeCount` reports what that caller may
  reach. The difference between that and `IGraphStore.NodeCount` is a fact about other people's tenants.
- **Contradiction detection only considers edges the caller may read.** Otherwise probing for a conflict becomes a
  way of learning what another tenant holds.

`UnrestrictedAccessPolicy` exists for loading an oracle graph and for tests about something else. It is named for
what it is, so that finding one in a code path is a question and not a surprise.

### Labels are not a boundary

A `GraphScope` is an authorization *input*. It says who is asking and what the knowledge claims about itself; the
policy decides. Where the boundary has to be cryptographic rather than administrative — a graph crossing a machine
boundary, or shared with a party who should not be able to read the rest — the route is `SharedFiles.Seal` in
`Creatura.Cells`: AES-256-GCM to a recipient set derived from the ledger, with the hub holding only ciphertext.
That is designed and not implemented here; see [Known limitations](#known-limitations).

### Import cannot escalate

`IGraphStore.Import` re-scopes every node and edge to the importer's own workspace under the importer's ownership.
A snapshot that could grant itself a tenant would turn every export into a privilege escalation, so it cannot. The
provenance is kept verbatim — who asserted what still travels with the knowledge, which is what makes G06 and G15
work — and marked `ArrivedAs = Import`, so an experiment can tell its own oracle graph from somebody else's claim
and a receiver can decide how much of it to believe.

Import also cannot resurrect deleted knowledge: the tombstone survives the round trip, which is the only thing
that makes a deletion a deletion rather than a hiding.

## Secrets

### Redaction happens on the way in

Not on the way out. Knowledge that held a secret has already leaked to every reader, every export, every snapshot
and every log line by the time anybody thinks to filter it.

The filter is `Scrubber.Scrub` from `Creatura.Cells` — private key blocks, URL credentials, bearer headers, known
token shapes (`sk-`/`pk-`/`rk-`, `gh?_`, `github_pat_`, `xox?-", `AKIA…`, JWT), `key=value` pairs naming a secret,
and long high-entropy runs. The repository already has three redactors; this is not a fourth. Its stated policy is
the right one for a knowledge store too: *"deliberately greedy: a goal that loses a long identifier is a smaller
loss than a ledger that carries a key to every vessel it syncs with."*

Applied to `Summary`, every `Attributes` value, `Warrant.Evidence` and `Provenance.Evidence`.

### Canaries

`SecretCanary` holds seven synthetic secrets — an OpenAI-shaped key, a GitHub PAT, an AWS access key id, a
`password=` pair, a bearer header, credentials in a URL, and a private key block — with the name each fails under.
They live beside the redactor rather than in the tests, so an export path, a fixture and a test all assert against
the same list. None of them is a real credential: a canary that was a real key would be a leak in the repository
whose job is to prove there are none.

`SecretCanary.FoundIn(text)` returns the names of whatever survived, so a failure says which shape the redactor
missed rather than only that it missed one. Every test that renders knowledge to a string — storage, export,
audit trail — asserts it comes back empty.

### Identifiers are not redacted

This is a decision with a cost, stated rather than buried. Redaction is applied to free text and never to an `Id`,
an `EntityType` or a relation name, because those are what a traversal matches on: a redacted identifier is a
broken edge, and a graph whose nodes cannot be addressed is not a graph.

The cost is that a secret used *as* an identifier would be stored verbatim. Two things limit it:

1. **Credential-shaped identifiers are refused, not stored.** `GraphRedaction.LooksLikeSecret` matches only the
   high-precision shapes — the token prefixes, key blocks, URL credentials and `key=value` pairs — and
   deliberately not `Scrubber`'s greedy long-run rule, which would refuse an ordinary long slug such as
   `service-orders-v2-production-shard-01`. A refused write reports
   `InMemoryGraphStore.RefusedSecret` and the refusal itself is scrubbed, because a reason string that echoed the
   key back would be the leak it was trying to prevent.
2. **The false refusal is the cheap mistake.** An id that merely begins with a word like `secret:` is refused. A
   refusal that is reported and counted in `GraphIngest.Refused` is a smaller loss than a key that is stored,
   exported and synced.

What this does **not** cover: a secret embedded in a *novel* shape that no pattern matches, used as an identifier.
That is a real hole. It is the reason identifiers in this system are supposed to be structural —
`service:orders`, `team:payments` — and never lifted from free text. See [Known limitations](#known-limitations).

## Auditability

Every accepted mutation appends a `GraphAuditEntry(At, Caller, Kind, Id, Reason)`. Deletions keep a tombstone: the
id, the category, the reason and the time, with the content gone. Erasing the id too would make the deletion
unauditable; keeping the content would make it meaningless.

The audit trail is asserted canary-free, because an audit log that recorded the secret it was refusing would be
worse than no audit log.

In memory the trail is an unbounded list, which is fine for a research graph and not fine for anything else. A
persistent provider should put it in the work ledger, where it is signed and where `WorkLedger.Verify` already
refuses an entry whose signature does not hold.

## Threat model, briefly

| Threat | Defence | Status |
|---|---|---|
| Knowledge poisoning — a malicious observation changes what an agent will do | Narrowing only; retrieved text is data; the control layer still judges effects against the registry | built, tested |
| Poisoning a tool gate to block legitimate work | `blockedBy` needs `Observed`; the registry, not the graph, decides whether the condition holds | built, tested |
| Cross-tenant read | `WorkspaceAccessPolicy`; refusal indistinguishable from absence | built, tested |
| Cross-tenant write | Same policy on every write; a snapshot cannot grant itself a scope | built, tested |
| Existence probing | Counts, contradictions and refusals are all scoped to the caller | built, tested |
| Secret in free text | `Scrubber.Scrub` on the way in; canaries asserted out of storage, export and audit | built, tested |
| Secret as an identifier | Credential-shaped identifiers refused | built, tested; novel shapes **not covered** |
| Deleted knowledge resurfacing | Tombstones; resurrection refused by revision, supersession and re-import | built, tested |
| Stale knowledge reinforcing a bad belief | Validity windows; `State` defaults to `Active`; superseded hidden unless asked for | built, tested |
| An agent confirming its own claim | `Verify` refuses when the verifier is the source | built, tested |
| A coincidence laundered into a cause | `RelationType.Temporal` relations cannot be verified at all | built, tested |
| Unbounded traversal hanging an agent | Depth, node, edge, duration and result bounds; the visited set cannot be switched off | built, tested |
| Graph content overriding system policy | No code path exists that could | built by construction |
| A shared graph read by an unauthorized party in transit | Would need `SharedFiles` sealing | **not built** |
| Credential knowledge coordinated with a broker | Would be Custodia (#108) | **not built** |

## Known limitations

1. **Novel secret shapes in identifiers are not caught.** Only known patterns are refused. Mitigation: identifiers
   must be structural, never extracted free text. A real deployment needs a allow-list of identifier shapes rather
   than a deny-list of secret shapes.
2. **Tenant isolation is administrative, not cryptographic.** `WorkspaceAccessPolicy` trusts the process it runs
   in. Anything crossing a machine boundary needs `SharedFiles.Seal`, which is not wired up.
3. **The audit trail is in memory and unbounded.** It should be ledger entries.
4. **`Import` trusts the verification status it is given.** A snapshot claiming `Observed` arrives as `Observed`.
   That is deliberate — otherwise G06 could not work — but it means an import is only as trustworthy as its
   exporter. A deployment that does not trust the exporter should load through `KnowledgePipeline` with
   `ProvenanceKind.Import`, which caps at `Observed` and marks the arrival.
5. **No retention policy.** Nothing expires by age and nothing is compacted. Knowledge consolidation (G17) and
   invalidation (G18) are unimplemented; `Supersede` and validity windows are the primitives they would use.
6. **Redaction is greedy and lossy.** `Scrubber`'s long-run rule will eat a legitimate 25-character identifier in a
   summary. The repository has already decided that trade, and this document is not the place to reverse it.
7. **Concurrency is one lock.** Correct and slow. A research graph is small; a shared service would need finer
   locking, and the `ICognitiveGraph` contract does not yet say what a concurrent write conflict should do.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md).
