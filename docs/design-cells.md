# Design: identity, vessel and the cell network

**Status:** design note; the five open decisions were answered by the owner on 2026-10-28 (see the end); the first slice
is built (see "What gets built first"). Written 2026-10-28 on the owner's laptop, after the framework's 0.1.0.

## The problem, in the owner's words

> I run agents that run tools on several computers, all with different specs, OS and so on. The setup can be
> confusing and messy, and since the agents depend on tools like Codex and Claude Code to keep their state, it gets
> really messy to know who is working on what and the path of that work. So I want to give the agents a strong
> identity linked to the hardware and the network, and a broadcast and communication network for the agents to
> communicate as cells. The network is an abstraction that can be implemented in-process, on a local network, or on
> the real internet where each node is somewhere else on the planet.

What that looks like on this machine today:

| tool | where an agent's history lives | what the location says | what it does not say |
|---|---|---|---|
| Claude Code | `~/.claude/projects/<folder path, dashes for slashes>/<session id>.jsonl` | which folder it ran in | which machine, which agent, which task, whether it is still working |
| Codex | `~/.codex/sessions/<year>/<month>/<day>/rollout-<time>-<id>.jsonl` | when it started | which folder, which machine, which task |

Each tool keeps its own record, on its own machine, keyed by something other than the agent. Answering "who is
working on what" means visiting every machine and reading every tool's files. The design below makes an agent say it
itself, in a form any other agent, or the owner, can read and check.

## Three principles carried over from the framework

1. **Judge by effect, not by report.** A cell's claim that it is working on something is a claim. What the ledger
   trusts is a signed entry from a known vessel, and the goal is evidence the cell cannot author.
2. **The sign belongs to whoever reads it.** A cell writes its monstra; it cannot take them back. The ledger is
   append-only, and the reader decides what it means.
3. **Start from identity and the record, not from transport.** The network is the replaceable part. Who a cell is,
   what it says it is doing, and where that record lives are the hard part, so they come first and must work with
   no network at all.

## The parts

### Identity: who, vessel, where

`Identity` already has *who* (role, principal, open commitments, scope) and a `Vessel`. It gains two things.

- **`CreaturaId`**: the creature's own stable id, created once and kept in its `AgentRecord`. It survives a move to
  another vessel, a change of seat, or a change of host tool. It answers "the same agent?" across machines and tools.
- **`CellKey`**: a signing key pair generated **on the vessel** and never copied off it. A cell is one creatura on
  one vessel; its key signs everything it publishes. The private key lives in a file readable only by its owner
  (`chmod 600`), or in the platform key store where one exists. Algorithm: ECDSA P-256, which .NET ships without a
  package, so `Creatura.Cells` stays package-free like the Core.

### Vessel: the hardware and the model

`Vessel` today describes the model (name, protocol, window, speed, price) and has three loose strings for machine,
OS and network. It becomes two typed halves:

- **`Hardware`**: a `VesselId` derived from the platform's hardware identifier (macOS `IOPlatformUUID`, Linux
  `/etc/machine-id`, Windows `MachineGuid`), **hashed with a per-owner salt** so the raw identifier never leaves the
  machine or appears in a log; plus chip, cores, memory, OS and version. A `VesselProbe` reads these per platform.
- **`Model`**: the existing model fields, unchanged.

A creatura can change model on the same hardware (new seat, same vessel id) or move hardware (new vessel id, same
creatura id). Both show up in its record as events, not as a new agent.

### Where: the network position

`Position` records how the cell is reachable: in-process (a process and a channel), on the mesh (node name and mesh
address, read from `tailscale status`), or on the internet (a relay address). It is a fact the cell reports and a
peer can check, not configuration.

### The work ledger

The part that answers "who is working on what, and the path of that work". An append-only log of signed entries,
one sequence per cell:

| entry | says |
|---|---|
| `Hello` | this cell exists: creatura id, vessel, position, public key |
| `Claim` | I am working on this: goal text, repository and branch or folder, host tool and session id |
| `Progress` | a monstrum: a trace row, a stress level, a blocked or escalated step |
| `HandOff` | I am stopping; this is where the work stands and why |
| `Release` | done, with the evidence the goal cites (facts, a commit, a test result) |
| `Heartbeat` | still alive, with the vessel's load |

Each entry carries the cell's id, its own sequence number and its signature. Two ledgers merge by taking the union
of entries and dropping any that fail verification. That is all the agreement the ledger needs: no leader, no
consensus, and it converges whatever order the entries arrive in. The *path of the work* is the chain of a claim's
entries, readable afterwards like a route, and the Augur can read it like any other trace.

### The cell network

```csharp
public interface ICellNetwork
{
    Task PublishAsync(SignedEntry entry, CancellationToken ct);                          // broadcast to every cell
    Task SendAsync(CellAddress to, SignedEntry entry, CancellationToken ct);             // to one cell
    IAsyncEnumerable<SignedEntry> ReceiveAsync(CancellationToken ct);                    // everything addressed to this cell or broadcast
    Task<IReadOnlyList<SignedEntry>> SyncAsync(CellAddress peer, LedgerCursor since, CancellationToken ct);   // catch up after being away
}
```

Delivery is at least once; entries are idempotent by (cell, sequence), so duplicates are harmless and a cell that was
offline catches up with `SyncAsync`. Three implementations, in this order:

1. **`InProcessCellNetwork`**: channels in one process. Tests and simulations; no sockets.
2. **`MeshCellNetwork`**: HTTP on the mesh address only (never a public interface), peers discovered from the mesh's
   own peer list. The mesh already authenticates machines; the cell signature authenticates the agent on them.
3. **`RelayCellNetwork`**: for cells off the mesh, a small relay that stores and forwards signed entries it cannot
   forge. Designed last, because the mesh already reaches every machine the owner has.

Biology, kept as vocabulary: a **cell** is one creatura on one vessel; a **tissue** is the cells on one vessel or on
one project; the **organism** is the owner's whole fleet. Levin's stress bus becomes literal: a blocked cell's
`Progress` entry carries its stress, and its tissue can see it.

### How host tools join

The first agents on the network are not Creatura's own; they are the owner's Claude Code and Codex sessions. They join
through their hooks, calling a small command:

```bash
creatura cell claim --goal "migration step 16" --repo egarim/biological-agents --branch creatura-core
creatura cell progress --row "golden diff empty"
creatura cell release --evidence "529 tests green; commit 9d8616a"
```

Claude Code's hooks (session start, after a tool, stop) and Codex's notify command can call it. The host tool keeps
its own history as it does now; the ledger records who, on which vessel, claimed what, and how it ended.

## What gets built first

A package-free `Creatura.Cells` project, offline, testable without a network:

1. `CreaturaId`, `CellKey`, `VesselProbe` (macOS first, Linux and Windows after), `Position`, `CellIdentity`.
2. `SignedEntry`, the six entry kinds, `WorkLedger` with verify and merge, a `JsonLedgerStore`.
3. `ICellNetwork` and `InProcessCellNetwork`.
4. A `creatura cell` command: `whoami`, `claim`, `progress`, `release`, `ledger`.

Pre-registered tests, written before the code:

| id | claim | falsifier |
|---|---|---|
| C1 | two cells on one in-process network that each claim work end with identical ledgers, whatever order entries arrive | any difference after both have synced |
| C2 | an entry altered after signing is rejected on merge | a tampered entry accepted |
| C3 | a creatura moved to another vessel keeps its id and record and gets a new vessel id and key | a new creatura id, or the old key still valid for the new vessel |
| C4 | the raw hardware identifier never appears in an entry, a log line or a file the cell writes | the identifier found anywhere outside the probe |
| C5 | a cell offline during three claims catches up with one sync and ends identical | a missing or duplicated entry after sync |

The mesh implementation and the host-tool hooks come after the owner has read the first slice running.

## Slice 3: the ledger in SQLite, synced through SyncFramework

Slice 2 left two costs in every hook run: the JSON store rewrites the whole file, and loading it checks both
signatures of every entry again. One hook also dropped an event when the lock stayed held too long. Slice 3 adds:

- **`ILedgerStore`**, which `JsonLedgerStore` and the new store both implement.
- **`SqliteLedgerStore`** (project `Creatura.Cells.Sqlite`, package `Microsoft.Data.Sqlite`), one row per entry
  keyed by (cell, sequence), WAL mode. A save writes only the entries it has not seen. A conflict keeps the smaller
  hash, the rule `WorkLedger.Merge` uses. An entry is verified once, when it enters a ledger from outside, and
  loading checks each row's hash and the owner's trust without checking the signatures again. `VerifyOnLoad` checks
  everything. An empty database imports `ledger.json` through a full merge.
- **A SyncFramework transport** (project `Creatura.Cells.Sync`, package `BIT.Data.Sync` 3.0.0 from
  egarim/SyncFramework). One signed entry is one delta: its operation is the entry's canonical JSON and its identity is the
  cell. `LedgerDeltaProcessor` parses each delta and merges it through `WorkLedger.Merge`. Nothing in a delta is ever
  executed. The server is a relay and trusts nothing: every cell verifies what it pulls.
- **The command** takes `--store sqlite` (default `json`). The hooks installed on the owner's laptop stay on JSON until the
  owner moves them.

Pre-registered tests, written before the code:

| id | claim | falsifier |
|---|---|---|
| C6 | a ledger saved to SQLite and loaded back holds the same entries; a save after one append writes one row | a different digest, or more than one row written |
| C7 | a row altered in the database is left out on load; with `VerifyOnLoad`, a row re-hashed after a change is rejected too | an altered row loaded |
| C8 | an empty database imports `ledger.json` with the same digest and refuses what the JSON load refuses | a different digest, or a rejected entry imported |
| C9 | two cells that each claim work converge through `SyncFrameworkServer` and the in-memory client, in either sync order | any difference after both have synced twice |
| C10 | a delta altered after signing, one that is not an entry, and one endorsed by an untrusted owner never reach a ledger; a valid entry relayed under another identity is accepted | a bad delta merged, or a valid one refused |
| C11 | a cell offline during three claims catches up with one sync and ends identical | a missing or duplicated entry |
| C12 | a cell that pulls before it ever pushed still pushes all its own entries | an own entry never reaching the server |

And one measurement, not a test: hook runs with each store on today's ledger and on a ledger of 2,000 entries.

### Results (2026-10-28)

C6 to C12 pass, offline, against the in-memory server. Each test was also run against a deliberate break of what it
guards: the processor skipping signatures, the load skipping the row hash, the save resending every entry, the
conflict rule reversed, and the cursor guard removed. Two breaks survived the first version of the tests. A save
that resent everything still changed only one row, so C6 now also counts the entries a save sends. Nothing raced
two writers for one sequence, so a conflict test was added. Every break now fails a test.

C12 found a flaw in SyncFramework's `MemoryDeltaStore`: a client that pulled before its first push got the server's
index as its push cursor and never pushed its own deltas. The fix is egarim/SyncFramework#27, merged; creatura's
interim guard is gone, and C12 passes against SyncFramework main without it.

Hook runs (`creatura cell hook`, Release build, median of 10, the owner's laptop):

| entries | store | PostToolUse | Stop (writes one entry) | Stop max |
|---|---|---|---|---|
| 23 | json | 65 ms | 322 ms | 419 ms |
| 23 | sqlite | 69 ms | 339 ms | 395 ms |
| 2,000 | json | 66 ms | 887 ms | 1,001 ms |
| 2,000 | sqlite | 65 ms | 352 ms | 421 ms |

At today's size the stores cost the same. At 2,000 entries SQLite keeps a write at its small-ledger cost and JSON
takes 2.5 times longer. A write on a small ledger takes about 250 ms more than a PostToolUse run, which writes nothing. Both
stores show it, so it lies elsewhere in the write path, not yet measured. That is the next cost to look at, and it
is not part of this slice.

## Slice 4: the mesh, between the owner's laptop and the second machine

Two vessels keep one ledger over the mesh (decision 4: the owner's laptop and the second machine, never the servers). Checked
2026-10-28: the second machine answers the owner's laptop over the mesh directly through the home network, in about 20 ms. It has the
.NET 10 SDK on ARM64, and the laptop's firewall is off. So the second machine only makes outgoing connections, and neither
machine needs a firewall change.

- **A hub, not peer to peer.** One cell runs `Creatura.Cells.Hub`, an ASP.NET Core host of SyncFramework's
  `SyncControllerBase` at `/sync`. Every cell, the hub's own included, syncs with it. Peer to peer can come later; a
  hub needs only one listening machine.
- **The hub trusts nothing it is sent.** Other fleet machines are on the mesh too (decision 1 says the mesh admits a
  machine, not an entry). The hub's delta store verifies every pushed entry against the owner keys it trusts, and
  stores it only if it verifies, once per entry and hash. It numbers deltas from its own SQLite file, so the numbering
  survives a restart; the stock memory store restarts at 1. It answers loopback and the mesh range (100.64.0.0/10)
  only, and binds to the address it is given.
- **Joining without moving the owner key.** The owner key stays on the owner's vessel.
  1. A new vessel runs `creatura cell request`. That creates a vessel salt but no owner key, opens the cell, and
     writes a request holding only public values.
  2. The owner's vessel runs `creatura cell endorse --request <file>`, which writes the endorsement.
  3. The new vessel runs `creatura cell accept --endorsement <file>`, which checks the endorsement covers its key,
     keeps it and writes the hello.
  The creatura on the new vessel has its own creatura id, even under the same name; one creatura across two vessels
  is later work.
- **Sync keeps its cursors.** `creatura cell sync --hub <url>` pushes what the hub has not had from this home, pulls
  what it has not seen, and keeps both cursors in `<home>/sync/` between runs. It relays every entry its ledger holds,
  not only its own, so a ledger spreads even when its writer is offline.

Pre-registered tests, written before the code:

| id | claim | falsifier |
|---|---|---|
| C13 | two homes converge through the hub over real HTTP | any difference after both have synced twice |
| C14 | the hub stores an entry that does not verify, or whose owner it does not trust, never; a valid one pushed twice, once | a bad entry stored, or a duplicate |
| C15 | a client whose cursor predates a hub restart still receives what is pushed after it | an entry pushed after the restart never reaching that client |
| C16 | a second sync from the same home pushes and pulls only what is new | anything sent or received twice |
| C17 | a vessel joins by request, endorse and accept, and its entries reach the owner's ledger through the hub; an endorsement for another key or vessel is refused, and entries endorsed by another owner reach neither the hub nor the owner's ledger | a refused join, a wrong endorsement kept, or another owner's entry taken |
| C18 | the hub answers loopback and the mesh range only | an answer to another address |

And one live run, not a test: the hub on the owner's laptop at its mesh address, the second machine joins, claims and syncs, the owner's laptop
syncs, and both ledgers end with the same digest.

### Results (2026-10-28)

C13 to C18 pass. Each was also run against a deliberate break of what it guards:
- the hub storing without verifying (C14 and C17 fail);
- the hub ignoring a client's cursor (C16 fails);
- the hub taking the delta id from the sender (C14 fails);
- a sync pushing back what it just pulled (C13 and C16 fail);
- numbering that starts again at each start, as the stock memory store's does (C15 fails).

Writing C17 found that `creatura cell ledger` trusted only the home's own owner key, so a joined vessel, which has
none, refused every entry. It now trusts the owner who endorsed the cell, as every other command did.

The live run, on 2026-10-28:
1. The command was copied to the second machine (Windows 11 on ARM64, 2.9 MB with only its own SQLite library).
2. The second machine wrote a request, the laptop's owner key endorsed it, and the second machine accepted it. Its cell is
   `cell-8bfe17b74d04cbcb`, and it reads its own mesh position, 100.100.1.3.
3. The hub ran on the owner's laptop bound to 100.100.1.5:5181 only. Loopback on the owner's laptop got no answer, and the second machine got
   one over the mesh.
4. The second machine claimed work and sent 2 entries. The owner's laptop sent its 50 and received those 2. The second machine's second
   sync received the 50 and sent nothing.
5. Both ledgers ended with 52 entries from 2 cells and the same digest, `e74833b1b8a0782d`.

The hub was stopped afterwards. Running it as a service, and syncing from the hooks, are not part of this slice.

### The hub without a home

A hub on a server must not hold the owner's private key. `--trust <file>` gives it the owner public keys to trust,
one per line, and `--data <dir>` says where its database lives. `creatura cell owner-key --out <file>`, on the
owner's vessel, writes that file. `--home` still works on the owner's own vessel.

| id | claim | falsifier |
|---|---|---|
| C19 | a hub given only a file of owner public keys trusts exactly those owners and needs no private key; a file with anything else in it is refused | a hub that reads a private key, trusts an owner not in the file, or starts on a malformed file |

A hub's numbering belongs to its database. A client keeps its cursors per hub address, so a fresh database at an
address clients have used before leaves their cursors ahead of it. Give a new hub a new address, or clear the
clients' `<home>/sync/` files for that address.

C19 passes. Live, on 2026-10-28, the hub ran on the owner's laptop from a file of the owner's public key with `--data`, and no
home. The second machine wrote a heartbeat and synced, and the owner's laptop synced. The second machine synced again, and both ledgers ended
with 55 entries and the digest `9f1b4623b9f333d5`.

## Decided (owner, 2026-10-28)

1. **Root of trust: both.** An owner key signs each vessel once (the ledger's trust); the mesh admits the machine (the
   transport's trust).
2. **The ledger lives on every cell**, whole; compaction later if it grows.
3. **Claude Code joins first**, through its hooks.
4. **Mesh test bed: the owner's laptop and the second machine.** Never production servers, never the VPN endpoints.
5. **The command is `creatura cell`.**

## Open decisions (as first written)

1. **Root of trust.** Who vouches that a vessel is the owner's? (a) An owner key on one machine that signs each
   vessel once, like the critic's owner token; (b) the mesh: a vessel is the owner's if the mesh admits it, read from
   its peer identity; (c) both, the owner key for the ledger and the mesh for transport. Recommendation: (c).
2. **Where the ledger lives.** Every cell keeps the whole ledger (simple, small for one owner's fleet), or one hub
   keeps it and cells keep their own entries. Recommendation: every cell, with compaction later if it grows.
3. **The first host tool to join.** Claude Code (hooks are richest) or Codex. Recommendation: Claude Code.
4. **The test bed for the mesh slice.** Which two machines first. Recommendation: the owner's laptop and the second machine, both the
   owner's own, never production servers or the VPN endpoints.
5. **Name of the command.** `creatura cell …`, or a shorter word of its own.

## What this note does not cover

Model seats on remote vessels (a cell asking another vessel's model), task assignment between cells (who should take
the next job), and anything that changes a server, a firewall or the mesh's configuration. Each of those is a later
note, and the last needs the owner's approval per machine.
