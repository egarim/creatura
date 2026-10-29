# Design: the activity stream, and goals offered to cells

Status: decided (owner, 2026-10-29: every recommendation below taken). Both slices are built: the read-only stream
and goals offered to cells. The tests below were written before the code.

## The problem

The cells answer "who is working on what, and the path of that work" ([design-cells.md](design-cells.md)), but only
in a terminal: `creatura cell ledger` prints every entry, `--open` the claims not released, `--path <claim>` one
claim's chain. There is no page to look at, and nothing goes the other way: the ledger records what an agent says it
is doing, and its goal still comes from whoever types into the session. Two things are missing:

1. **A view a person reads at a glance**: what each cell is doing now, what it did, which vessels went quiet.
2. **A way to hand a cell a goal** that is as checkable as everything else in the ledger.

## The ledger is already an activity stream

Each cell keeps an append-only, ordered, signed sequence of "who did what, when". That is what an activity stream is.
This note presents the ledger in the W3C vocabulary for it, [Activity Streams 2.0](https://www.w3.org/TR/activitystreams-core/)
(the format ActivityPub carries), so the view is standard and any reader that knows the vocabulary can read it. It
does not federate: nothing in this note sends an activity off the owner's machines.

### The mapping

| ledger entry | activity | how it reads |
|---|---|---|
| `Hello` | `Join` | "creatura@laptop joined the fleet" |
| `Claim` | `Create`, object a claim named by its goal, with repository, branch, folder, tool and session | "claude-code@laptop started 'migration step 16' in egarim/creatura (main)" |
| `Progress` | `Update`, in the claim's thread; stress and blocked as properties | "golden diff empty" (blocked rows marked) |
| `HandOff` | `Leave`, with the reason | "stopped: waiting for review" |
| `Release` | `creatura:Release` (Activity Streams has no "finished"), the evidence as its result | "finished: 737 tests green; commit 7b7421f" |
| `Warning` | `Flag`, the subject as its object, the evidence as its content | "flagged road:r12: sensor says open, nothing arrived" |
| `Artifact` | `Create`, object a `Document` with its size only | "shared a file of 84 KB with 2 cells" |
| `Heartbeat` | none | updates the cell's "last seen" and load on its actor instead; a stream of heartbeats would bury everything else |
| `Offer` (new, below) | `Offer`, from the owner to a cell or vessel | "the owner offered 'rotate the logs' to any cell on the second machine" |
| `Withdraw` (new, below) | `Undo` of that offer | "the owner withdrew 'rotate the logs'" |

A `Claim` that cites an offer is that offer's `Accept`; a decline is a `HandOff` of the offer before any claim, its
`Reject`.

Identifiers are URNs, not URLs, since nothing is served publicly:
- an activity is `urn:creatura:entry:<cell>#<sequence>`, the entry's own id (`SignedEntry.Id`);
- a cell is a `Service` actor, `urn:creatura:cell:<cell id>`, named by its creatura and the vessel it runs on;
- the owner is a `Person` actor, `urn:creatura:owner:<owner key fingerprint>`.

A claim's progress, hand-off and release carry the claim's activity id as their `context`, so a claim is a thread.
The terms Activity Streams lacks (`creatura:Release`, stress, blocked, the entry's signature) live in a small context
of Creatura's own beside the standard one.

### What the stream is, and is not

- **A view, never the record.** The stream is a pure function of the verified ledger and the revocations a home
  trusts. Every activity carries its entry's cell, sequence and signature, so a reader can check it against the
  ledger. Nothing is ever written to the stream directly.
- **The same everywhere.** Two homes that hold the same entries produce the same stream, byte for byte, whatever order
  the entries arrived in. The order is by timestamp, then cell, then sequence. Each cell's own order is exact; across
  cells the timestamps are only as good as the vessels' clocks, and the page says so.
- **Nothing the ledger seals.** An artifact shows its size and its claim, never its ciphertext, its sealed name or its
  wrapped keys; the stream decrypts nothing. The raw hardware identifier never appears (C4 still holds). A revoked
  cell's entries past its N are not in it, as they are not in the ledger.
- **The text is as private as the ledger.** Goals, folders, repository names and session ids are in it, as they are
  in the ledger (a goal taken from a prompt has been through the scrubber, which is not perfect). It is read only where
  the ledger is already read.

## Where it is shown

Every cell holds the whole ledger (decided 2026-10-28), so the stream needs no server. The first place is local:

- **`creatura cell stream`** serves a read-only page and the stream as JSON (`/activity`, an `OrderedCollection`,
  newest first, paged) on `127.0.0.1` only, from this home's ledger, and opens the page. It accepts GET and nothing
  else, and it binds no other address.
- **`creatura cell activity --out <file>`** writes the same collection to a file, for another tool.

The page: a timeline of threads, one per claim, newest activity first, with filters by vessel, by cell and by open
claims; each cell's last-seen time and load at the side, quiet vessels marked; blocked progress and flags in red.
It is plain HTML and script that loads nothing from outside (unlike the convoy viewer, which loads three.js from a
CDN): the page shows private text, so it must work with the network off.

A page on the hub is a later step. Every request to the hub must be signed by an endorsed cell (C20), and a browser
cannot sign; serving the hub's stream to a browser needs a sign-in of its own, which this note does not design.

## A face for each actor

Activity Streams gives every actor an `icon`. The logo was drawn in several versions before one was chosen: the same
winged creature as a lion or an ibex, under a halo, a ring with a diamond or a scatter of stars, in navy or with gold
wings. They make a family of faces for the actors, one look shared by the whole fleet.

At the size of a timeline avatar (24 to 32 pixels) the versions look much alike: the pose is the same, so only the head
(mane or horns), the wings' colour and the halo tell them apart. Two layers keep every actor distinct, however many
there are:

- **The figure says what kind of actor it is**, from a small fixed table: the owner keeps the project's icon (the
  lion with the halo); Claude Code cells, Codex cells and Creatura's own loops each get one version. Few kinds, so few
  figures, each easy to learn.
- **A coloured ring says which vessel**, from a fixed palette chosen by the vessel id, the same colour on every page
  and every machine.

So "the ibex in the green ring" is Codex on the second machine. A figure per instance would run out as the fleet grows,
and two instances of one kind on one vessel are told apart by name, not face.

The versions exist as one raster sheet. Each chosen one has to be traced to a clean single-colour SVG, as the current
icon was, and given a light variant for dark backgrounds (`docs/assets/creatura-icon-light.svg` is the pattern). The
icons ship inside the page and the tool; none is fetched.

## Offering a goal

An **offer** is a goal the owner hands to a cell, signed with the owner key like an endorsement or a revocation:

| field | |
|---|---|
| `Id` | random, unique |
| `To` | a cell id, or a vessel id (any cell on that vessel) |
| `Goal` | the goal text, and optionally `Repo`, `Branch`, `Folder` |
| `IssuedAt`, `ExpiresAt` | an offer not taken by then lapses |
| `OwnerPublicKey`, `Signature` | over the fields and the purpose `creatura goal offer v1` |

`creatura cell offer --to <cell or vessel> --goal <text> [--expires 24h]`, on the owner's vessel, writes it as an
`Offer` entry of the owner's own cell, with the owner-signed offer as its payload, so it travels through the same sync
as everything else and is in the stream for free. `creatura cell withdraw --offer <id>` writes a `Withdraw`, signed the
same way. A cell trusts an offer only when the owner signature inside it verifies against an owner it trusts; the
cell signature around it alone is not enough.

**How a session picks it up.** At `SessionStart` the hook looks for offers addressed to this cell or vessel that are
verified, unexpired, not withdrawn and not yet claimed. Claude Code adds what a `SessionStart` hook prints to the
session's context, which is why the hook prints nothing today; its JSON output also has a field for a message shown
to the person. The first version shows the offer to the person only, with its id. Whoever runs the session starts
it, and the first prompt's `Claim` then cites the offer (`ClaimPayload.Offer`). `creatura cell take --offer <id>`
and `decline --offer <id> --reason <text>` do the same from a terminal (`accept` is taken: it accepts an
endorsement). Putting an offer straight into the model's
context, for sessions nobody watches, is open decision 3.

**Two takers.** An offer to a vessel can be claimed by two cells at once; the ledger has no leader to stop it. Both
claims stand, and the stream shows the offer's thread with both, marked as a conflict. Nothing is merged or dropped
silently.

Codex has no hook at session start (its notify command runs after a turn), so it can claim an offer from the terminal
but is not shown one.

## Pre-registered tests

| id | claim | falsifier |
|---|---|---|
| C23 | the stream is a pure function of the verified ledger and the trusted revocations: the same entries, in any order of arrival, give the same bytes, also on two homes after a sync | any byte that differs |
| C24 | each entry is exactly one activity, except heartbeats, which only update their actor; each activity names its entry, its actor and its time, and a claim's progress, hand-off and release are in its thread; the collection has the shape Activity Streams 2.0 requires | an entry with no activity or two, a heartbeat in the timeline, an activity whose id is not its entry's, a step outside its claim's thread, or a shape a reader rejects |
| C25 | nothing the ledger seals or withholds reaches the stream: no ciphertext, sealed name or wrapped key, no raw hardware identifier, no entry past a revocation's N | any of them found in the stream or the page |
| C26 | `creatura cell stream` answers on the loopback address only, and only GET | an answer on another address, or to another method |
| C27 | the hook shows an offer only when its owner signature verifies against a trusted owner and it is addressed to this cell or vessel, unexpired, not withdrawn and not yet claimed | an offer shown that is tampered with, signed by another owner or only by a cell, addressed elsewhere, expired, withdrawn or taken |
| C28 | a claim that cites an offer closes it in the stream; two claims on one offer are both kept and marked as a conflict | an open offer after its claim, a dropped claim, or a conflict not marked |
| C29 | the hook keeps its promises: it always exits 0, prints nothing when there is no offer to show, and with one shows only that offer's goal and id, in the field for the person | another exit code, output with no offer, or anything else printed |

Each gets deliberate breaks, as before: the stream sorted by arrival, a heartbeat let through, an artifact's label
copied into the stream, the page bound to every interface, an offer trusted on its cell signature alone, an expired
offer shown, the second claim on an offer dropped.

## What gets built first

1. **The stream, read-only** (C23 to C26): the projection in `Creatura.Cells`, package-free; `creatura cell activity`
   and `creatura cell stream`; the page, with the actors' faces once the owner has picked the versions.
2. **Offers** (C27 to C29), after the owner has used the stream for a while: the `Offer` and `Withdraw` entries, the
   `offer`, `withdraw`, `take` and `decline` commands, `ClaimPayload.Offer`, and the hook's `SessionStart` change.

### Results (2026-10-29): the stream

Built: `ActivityStream` in `Creatura.Cells` (package-free), `creatura cell activity [--out <file>]`, and
`creatura cell stream [--port <n>] [--no-open]` with its page, served by a small HTTP server on a socket bound to
127.0.0.1 (not HttpListener, which needs an administrator for some prefixes on Windows). The page and the icons are
embedded in the tool, and it renders ledger text as text, never as markup.

C23, C24, C25 and C26 pass. Six deliberate breaks each fail one: a build time stamped into the document (C23),
heartbeats let into the timeline and progress outside its claim's thread (C24), an artifact's sealed name copied in
(C25), the server bound to every interface and every method answered (C26). Two of the tests were wrong at first,
and the breaks and the machine showed it:
- C25 searched the raw JSON, where the writer escapes base64's `+` as `\u002B`, so a copied sealed name was not
  found. It now searches every decoded string as well.
- C26 tried a TCP connection to the machine's other addresses, and a VPN's tunnel interface accepted it on a port
  where nothing listened. The falsifier is the stream's answer, so it now sends a request and looks for one.

Live on the owner's laptop, against its real ledger: 239 activities from 2 cells (Claude Code on the laptop and on the
Surface), one thread per claim, the Surface marked quiet after a day without an entry, and a POST refused with 405.
The page's figures are all the project's icon until the owner picks the versions; the rings already differ by vessel.

### Results (2026-10-29): offers

Built: `GoalOffer` and `GoalWithdrawal`, signed by the owner key; the `Offer` and `Withdraw` entry kinds, which a
ledger takes only when the owner signature inside verifies by an owner it trusts; `ClaimPayload.Offer` (taking) and
`HandOffPayload.Offer` (declining); `PendingOffers` and `OfferStates`; the commands `offer` (to a cell id, a vessel id
or a host name), `withdraw`, `offers [--all]`, `take` and `decline`; offers in the stream as `Offer` by the owner,
with a state (open, taken, conflict, withdrawn), `Undo` and `Reject`. At a new session's start (not a resume or a
compaction) the hook prints one JSON object whose only field is `systemMessage`; the first prompt takes an offer by
naming it (`offer of-…`). The page gained a Simple view, at the owner's request: each claim as a sentence or two,
offers and flags in words, no steps or metadata; Full is the view as it was.

C27, C28 and C29 pass, with C23 to C26 still holding; seven deliberate breaks each fail one, on the first pass: an
offer trusted on its cell signature alone, another owner's offer accepted, a lapsed offer shown, a withdrawal ignored,
two claims not marked as a conflict, the hook printing plain text, and the offer shown again on resume.

Live, across two vessels. Old binaries fail on an unknown entry kind while reading it, so the laptop's CLI, the hub
and the Surface were upgraded first (each backed up beside itself). The mesh could not carry traffic from the laptop
that day (another VPN was up, and Tailscale had no network map), so the Surface synced through an SSH tunnel through
`vpn-a`, signed as always. The laptop offered "report the Surface's ledger: how many entries it holds and its digest"
to any cell on SURFACE (`of-ae68d3755d`); the Surface synced, listed it as waiting, took it, recorded a step, and
released it with "249 entries from 2 cells, digest 893ef955842bde15"; after both synced, both ledgers held 251
entries with the digest `bf8aab185b47fb4e`, and the stream showed the offer taken and the claim finished.

Found live, on the laptop: the installed hooks discarded every hook's stdout (`>/dev/null 2>&1`) and ran SessionStart
in the background, so no message could reach the person. SessionStart now runs synchronously (a 5 s timeout) and
discards only stderr; the other events stay silent and in the background; `install-hooks` replaces the older
SessionStart group instead of adding a second. The installed command, fed a SessionStart event, printed exactly the
one JSON object.

Played end to end with headless Claude Code (`claude -p`) by `tools/try-offer.sh`, on offer `of-bb5eddfb10`
("Summarize what changed in egarim/creatura today and list anything left untested", to the laptop's vessel). A new
session's SessionStart hook showed the person that one offer (an earlier test offer had lapsed and was left out),
and that session's model, asked from its context alone whether anyone had offered it a goal, answered no: the message
reaches the person and not the model, as the documentation says. A second session whose first prompt began
`offer of-bb5eddfb10` took it, and did the goal with read-only tools; the offer stands taken by claim
`cell-eff16c8157166090#258`, which cites the offer and carries its goal, session, repo and branch. Its summary got one
fact wrong (it saw no release run, but `v0.2.0-preview.1` was released that day): the ledger records that a goal was
taken, not whether the answer was right.

Found: in a headless session the claim's path is only Claim then HandOff ("session ended (other) after 0 turns"). The
Stop hook runs in the background and the process exits before it writes a step; only SessionEnd, which waits, made
it in. So a `-p` session leaves no steps and no evidence in the ledger. Not yet seen: the message in the interactive
Claude Code window; the headless run shows only that Claude Code receives it and the model does not.

## Decided (owner, 2026-10-29)

Every recommendation below: the stream on a local page first; offers as entries of the owner's cell; the person
reads an offer first; offers to a cell or a vessel; a figure per kind of actor and a ring colour per vessel (until
the owner picks the figures, every actor wears the project's icon); Activity Streams 2.0.

## Open decisions (as first written)

1. **Where the stream is shown first.** (a) A local page from the home's own ledger; (b) a page on the hub, which needs
   a browser sign-in first. Recommendation: (a).
2. **How an offer travels.** (a) As an entry of the owner's cell, carrying the owner-signed offer; (b) as a file of its
   own with its own sync path, as revocations do. Recommendation: (a): one sync path, and the stream shows it with no
   extra work.
3. **Who reads an offer first.** (a) The person running the session, who starts it; (b) the model, through the
   context, so an unwatched session can start it alone. Recommendation: (a) first. (b) turns the owner key into a key
   that can put words into every session on every vessel, so it should come with its own decision and tests.
4. **Addressing.** A cell, a vessel, or later "any cell that can". Recommendation: a cell or a vessel; "any" waits
   until there is a rule for who should take it.
5. **Faces.** (a) A figure per kind of actor and a ring colour per vessel; (b) a figure per instance; (c) no icons.
   Recommendation: (a). Which version goes with which kind is the owner's to choose.
6. **The vocabulary.** Activity Streams 2.0, or a JSON shape of Creatura's own. Recommendation: Activity Streams 2.0,
   with a few terms of Creatura's own in its context.

## What this note does not cover

Federation (sending activities to anyone outside the owner's machines), notifications pushed to a phone, editing or
reprioritising a goal once offered, choosing which cell should take a job, and the hub's browser sign-in. Each is a
later note.
