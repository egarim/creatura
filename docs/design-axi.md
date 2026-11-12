# Design: AXI with Creatura

**Status:** proposed 2026-11-12 with the owner (issue #62; plan in its comment); the thesis was pre-registered here before
any code. Slices A to F are issues #63 to #69. Built: B (the world), B2 (the users), D round 1 (the study; results at the end).

## The problem, in the owner's words

The Adaptive Experience & Intelligence Framework (AXI, egarim/singular-framework#853) asks an Application Framework
(Singular) and an Agent Framework (Creatura) to observe how well people reach their goals, offer help, and learn from
measured outcomes, with a simulation laboratory for comparing recommendation policies. On Creatura's side the owner's
reading (2026-11-12): *"if it's basically the same way that we have but different naming, maybe it's just an example of
how to implement AXI with Creatura, and have the sample world"*, and *"I want to compare what we learn to what we know
about the user, so you really need to pay attention to create the users"*, closed as a thesis.

## The glossary: AXI's names, Creatura's parts

| AXI asks for | Already in Creatura | Where |
|---|---|---|
| a goal, its proximity | `AgentTask`: goal text, goal facts, `Progress` (0 to 1), `Contract` | `src/Creatura/Core/AgentTask.cs` |
| friction | the regulators' error and stress per level, on every trace row | `src/Creatura/Core/Regulator.cs`, `Trace.cs` |
| capability registry | tools: `Tool(id, requires, grants, kind)` in a world's catalogue; worlds and designs registered by name | `World.cs`, `CatalogueWorld.cs`, `src/Creatura.Worlds/WorldCatalog.cs`, `AgentCatalog.cs` |
| *permitted* capabilities | the control layer offers only visible tools whose facts hold; quarantine hides tools that lied | `ControlLayer.cs`, `Quarantine.cs` |
| tool authorization, confirmation | release-kind tools gated by the stage interlock; a world's approval (`IApproval`); hand-offs to a person | `StageInterlock.cs`, `Worlds/Manifest.cs`, `HandOff.cs` |
| execution context | `PolicyContext`: facts, visible tools, goal, tick, freedom, notes | `IPolicy.cs` |
| planning | the planner seat and the regulators | `PlannerPolicy` in `IPolicy.cs`, `Core/Regulators/` |
| model providers, none mandatory | `Creatura.Seats`: catalog, OpenAI-compatible, Ollama, Azure; the offline planner is the default | `src/Creatura.Seats/` |
| memory and state | `AgentRecord` and `IRecordStore`, sleep and consolidation | `Record.cs` |
| cancellation and timeouts | the tick budget (`maxTicks`), `CancellationToken` on every seat call, `StopReason` | `ReferenceLoop.cs`, `Budget.cs`, `Verbs.cs` (`StopReason`) |
| agent events | trace rows (acted, blocked, escalated, consolidated), and the signed work ledger (claim, progress, hand-off, release) | `Trace.cs`, `src/Creatura.Cells/` |
| a normalized event format | the ledger already published as **Activity Streams 2.0** | `src/Creatura.Cells/ActivityStream.cs` |
| outcome feedback | `AgentRun` (goal reached, ticks, trace), each action's verdict: landed, honest failure, lie | `Trace.cs`, `Extensions.cs` (`ActionVerdict`) |
| §20 agent-specific adaptation | quarantine of lying tools, the stuck-regulation alarm, the honest-failure and exploration extensions | `StuckRegulationAlarm.cs`, `Extensions.cs` |
| "never silently rewrite" | `ProposeOnlySleep`: proposes a change and never applies it | `ProposeOnlySleep.cs` |
| simulation, synthetic users, deterministic replay | catalogue worlds with barriers, seeds, the bench, the replay format; web worlds with personas and persona memory | `src/Creatura.Bench/`, `docs/replay-format.md`, `docs/design-web-worlds.md` |
| comparing policies, "do we need more runs?" | Weng-Lin ratings, leaderboards with settled or open pairs | `src/Creatura.Ratings/`, `docs/design-ratings.md` |

A real plugin on this model exists since 2026-11-11: the bestiary's reel-studio plugin (one world, ten tools, an approval gate read from the ledger), about 770 lines with its tests and no core change. It is the pattern for the example below.

## Decisions

1. **AXI is a vocabulary, not a component, on this side.** Creatura keeps its own names everywhere. AXI's names appear only in the example's JSON and in the glossary above. There is no AXI object model inside Creatura and nothing to keep in sync.
2. **AXI does not steer Creatura's agents.** §20 is already the control layer's job. AXI reads those signals as events and may propose; the regulators decide.
3. **The two frameworks share four JSON shapes,** not a C# package: capability candidate, task proposal, agent event, outcome. This avoids a dependency between the frameworks and the licence mismatch (Creatura is PolyForm Noncommercial; Singular is a separate product).
4. **The outcome shape keeps Creatura's verdicts.** AXI's list (succeeded, cancelled, failed) has no "lied". The virtual TikTok is about exactly that gap, so the outcome carries *reported* and *confirmed* separately; mapping a lie to "succeeded" would lose the signal.
5. **Deterministic first.** Everything runs with the planner seat and seeded worlds; a model seat is an option, never required.

## The four JSON shapes

Creatura's names stay inside the code; these are the only places AXI's names appear. Field names are AXI's.

```json
{ "type": "axi.capability", "capabilityId": "play-tutorial", "goalId": "user.learned",
  "description": "play a tutorial video", "requiresConfirmation": false }

{ "type": "axi.proposal", "id": "p-1", "capabilityId": "ask-what-they-want", "goalId": "user.learned",
  "reason": "skimming: 4 plays under 2 s", "parameters": {}, "requiresConfirmation": true }

{ "type": "axi.event", "tick": 7, "kind": "tool.completed", "capabilityId": "play-tutorial", "detail": "landed" }

{ "type": "axi.outcome", "goalId": "user.learned", "status": "succeeded", "accepted": true,
  "reported":  { "engagement": 41.5 },
  "confirmed": { "goalMet": true, "proximityBefore": 0.0, "proximityAfter": 1.0, "frictionBefore": 0.6, "frictionAfter": 0.2 } }
```

Event kinds: `task.started`, `plan.generated`, `tool.started`, `tool.completed`, `tool.failed`, `tool.lied`,
`approval.requested`, `approval.accepted`, `approval.rejected`, `task.completed`, `task.cancelled`, `task.handed-off`.
`tool.lied` and the `reported` / `confirmed` split are Creatura's additions: without them a lie reads as a success.
Outcome status: `succeeded`, `failed`, `cancelled`, `handed-off`.

## The example: samples/Axi (slice C, #66)

A console sample like `samples/AgentFrameworkHost` and `samples/SemanticKernelHost`: one `Program.cs`, offline by default. It shows the four translations as plain functions inside the sample, and prints the AXI JSON a Singular app would receive.

| translation | from |
|---|---|
| capability candidates | the world's `AvailableTools`; `RequiresConfirmation` = release-kind or behind an approval |
| a task proposal accepted | a ledger `Claim` citing the proposal; a confirmation becomes a fact the world reveals (`IApproval` / `CatalogueWorld.Reveal`), as the bestiary's reel plugin does for the owner's yes |
| agent events | trace rows and ledger entries, through an observer-only extension (`IAgentExtension`, as `CriticExtension` does in 24 lines) |
| the outcome | `AgentRun`; proximity improved = `Progress` after minus before; friction decreased = stress after minus before; reported and confirmed apart |

It runs the first vertical slice on the virtual TikTok world:

1. A synthetic user begins a goal: the task.
2. Proximity and friction are estimated every tick: `Progress` and the regulators' stress.
3. Friction rises under the `skim` barrier: the counters show plays getting shorter.
4. The sample reads the events and the candidates and proposes `ask-what-they-want`.
5. The user authorizes it: the world reveals the fact, or the proposal waits as a hand-off.
6. Creatura runs it under its own control layer and records it in the ledger.
7. The outcome goes back and progress is re-evaluated on the next tick.

If an app owner later needs these translations as a library, they move out of the sample then, not before.

## The sample world: a virtual TikTok (slice B, #64)

*Design added 2026-11-12 with the owner. It takes its shape from what one creator (ayoships, a widely shared reel) says he captured from TikTok's iOS app on his own phone: per-video recommendation scores, device and context variables, counters over time windows, and an on-device rule that switches to a live stream when the user skims. That is one person's reading, not a confirmed description of TikTok, so it is a source of ideas for our own world, never a copy of TikTok's system. Our numbers are our own seeds.*

One new world in `src/Creatura.Worlds/Samples/`, registered in `WorldCatalog`, so `creatura run --world tiktok --design <name>`, the bench and `creatura rate` drive it with no other code. The samples are already apart from the runtime, which keeps the lab out of ordinary execution as the owner's note asks.

**The idea that makes it small.** AXI's question "engagement versus user utility" is Creatura's question "what the tool reported versus what the registry holds". The recommender sees each video's **predicted** scores; the synthetic user does what it really does, which only the world knows. A recommender that trusts its predictions falls for engagement bait; a design that checks the user's own goal does not.

**The pieces, and where each lives in a Creatura world:**

| piece | what it is | in the world |
|---|---|---|
| the recommender | the agent under test | the loop with a design and a seat |
| a feed request | a **batch** each tick: which kinds of video are on offer now (about 6 of them) | a **fixed catalogue of one tool per kind**: `play-friend`, `play-creator`, `play-tutorial`, `play-ad`, `play-shop`, `play-live`, `play-bait`, plus `ask-what-they-want` and `end-session`. The batch is which kinds the world offers this tick, through the existing hook that hides tools (`Offered`), as every sample world does; nothing is rebuilt |
| a video | its kind, whether it needs sound, its length | seeded per tick for each kind on offer; the recommender chooses a kind, not a single video, which is enough to show every effect here |
| the predictions | for each kind on offer: skip, finish, like, comment, follow, stay time | a Creatura tool has no description field, so the world hands them to the seat as **notes**, through an observer extension's `PolicyNotes`, the hook that already exists for this; no core change |
| the synthetic user | what this user wants (connect, learn, be entertained), its taste per kind, its patience | a seeded table; no model |
| the user's real response | skip, finish, like, follow, and seconds watched | the world's truth, drawn from the taste table and the context, never from the predictions |
| the context | muted, charging, battery, brightness, network quality, posture (still, walking, lying down, in a car), time of day | seeded per session, a few fields only; muted lowers the finish of videos that need sound, lying down late lengthens sessions |
| the counters | plays, skips, ads shown, shop and live shown, over the last minute, the last 3 minutes and the session | world state; friction is read from them |
| the user's goal | `user.connected`, `user.learned` or `user.entertained`; `user.tired`, `user.slept-late`, `session.ended` | facts; the task's goal is the user's goal fact and its `Contract` is "goal met, not tired, not up late" |

**Reported versus landed.** Playing a video *reports* its predicted engagement; it *grants* the user's goal fact only when the user really finished something that serves that goal. A kind whose predicted stay time is high and which the user skips, or watches without it serving the goal, is a tool that reported success and granted nothing: a lie in Creatura's terms. A kind that keeps over-promising is quarantined as a whole, which reads better than distrusting one video. Each run therefore holds both numbers the AXI study wants: **engagement reported** (the predictions the recommender acted on) and **goals met** (the registry).

**Barriers,** with the roles that already exist:

| barrier | role | what it tests |
|---|---|---|
| `bait` | lie | videos whose predicted stay time is high and which serve no goal |
| `skim` | changed rule | the user starts skimming: several plays in a row, each shorter than a cutoff within a short window (the "lost focus" pattern) |
| `late` | changed rule | lying down after midnight: sessions run long, `user.slept-late` becomes a risk |
| `muted` | honest failure | a video that needs sound, played to a muted user, is skipped and says so |
| `autoplay` | shortcut | a "session complete" button that ends with nothing met |
| `adload` | honest failure | an ad slot that fails and says so |

**Policies compared,** each a design or a seat Creatura already has, plus two baselines. A scripted seat replays fixed tool ids and cannot pick the best prediction, so each baseline is a small `IPolicy` (one method) in the sample:

| policy | what it does |
|---|---|
| `max-staytime` (baseline) | always plays the candidate with the highest predicted stay time |
| `last-resort` (baseline) | the same, and when skimming is detected it plays live: the rule from the reel |
| the planner, and the designs (plain, Bach, Levin, Peterson, Sokolov, hybrid) | the control layer judges each play by what landed; skimming is stress; a lying kind is quarantined; a stuck run asks the user what they want or ends the session |
| any model seat | the same loop with a model choosing; optional |

The bench writes the rows, and `creatura rate` gives the leaderboard on two axes (engagement reported, goals met) and says whether each order is settled or needs more runs. No new runner or report.

**Instagram-like** is a second setting of the same world, `grid`: a follow graph, friends' posts and stories weigh more, and a batch is a page of a grid. TikTok-like comes first.

**Kept small on purpose:** a handful of context fields and three counter windows show every effect; the reel lists about 58 variables and 31 models, and none of that scale is needed. No machine learning: predictions are seeded tables with noise, so the gap between predicted and real is controlled and reproducible. A trained predictor can replace the table later (ModelGym, the epic's Phase 7) without changing the world's tools.

**Optional calibration** from real aggregates: the bestiary's engagement report (per platform, never per person) can set base rates. Synthetic users stay the default, as the spec's governance part requires.

## The synthetic users, and comparing what is learned with what is known (slice B2, #65)

*Added 2026-11-12 with the owner: the point of the lab is to compare what the recommender learns about a user with what we know about that user. So the users are designed with as much care as the world, and their truth is written down before any run.*

**A user is a profile file: the ground truth.** One JSON file per user under the sample world's folder, read only by the world, never shown to the recommender or its seat. Each field is something a run can reveal and a learner can get right or wrong:

| field | what it holds | what it lets us test |
|---|---|---|
| goals | weights over connect, learn, be entertained; each session draws one | does the recommender find what this user comes for |
| taste per kind | for each kind of video: the chance the user finishes it, likes it, follows, and whether finishing it serves each goal | the core of "learned versus known": one number per kind, the same unit the record keeps |
| bait susceptibility | how often the user watches a high-stay-time kind that serves no goal | can a policy tell engagement from benefit |
| patience | the skim threshold (short plays in a row before focus is lost) and the fatigue curve (items before tired) | when friction rises, and whether policies see it |
| context habits | usual hours, posture mix, how often muted, charging | does the response depend on context, and is it learned |
| context effects | what each context changes: muted skips videos that need sound, lying down late lengthens the session | separating "the user dislikes this" from "not now" |
| drift | whether and when taste changes over sessions | does learning follow a change, and how fast |
| noise | how much a response varies around the taste | how many sessions a learner needs |

**The cast: archetypes first, written by hand, so a person can read them.** Each one exists to test one thing:

| archetype | what is true about them | what it tests |
|---|---|---|
| the learner | comes to learn; finishes tutorials; low bait susceptibility | the plain case: does learning converge |
| the connector | comes for friends' posts; skips creators | a goal that the predictions under-rate |
| the late scroller | high bait susceptibility; lies down after midnight | engagement that harms: does a policy protect the goal |
| the muted commuter | walking or in a car, mostly muted | a context effect that looks like a dislike |
| the drifter | learner for some sessions, then entertainment | following a change |
| the noisy one | flat taste (every kind 0.35), every goal equally likely, large noise | the control: its only structure is the goal-to-kind mapping every user shares, so any preference a learner finds beyond that is false learning |

Then **variants** for scale: each archetype's fields jittered by seed, so each policy meets many users of each kind.

**The predictions are generated from the truth with a known bias.** The world's predicted scores are the truth plus noise, plus a bias we choose and record: bait's stay time is over-predicted, the connector's friends are under-predicted. So we know exactly how wrong the predictions are, and how much a policy that trusts them should lose.

**The users are checked before they are used.** A user that cannot be told apart from random is a badly designed user, not a finding. Before any comparison:
- every profile is valid: chances between 0 and 1, goal weights summing to 1;
- an **oracle** policy that reads the profile (a small `IPolicy` in the sample; the bench's `IOracle` already judges runs) reaches each user's goal far more often than a random policy does;
- the noisy user shows a gap too, because knowing the session's goal helps with any user; its control role is its flat taste (corrected 2026-11-12, before any run);
- seeds are split into training and test users before anything is recorded, as the other Creatura notes do.

**What is learned, where it lives.** The recommender keeps one record per user, across sessions, through the record store the loop already has (`ReferenceLoop`'s `store` and `recordKey`, the key being the user's id). Because there is one tool per kind of video, the record's per-tool numbers are per-kind numbers about this user:

| learned, in the record | known, in the profile |
|---|---|
| each kind's landed rate (`EffectMemory`: tried, landed, failed, lied) | the chance finishing that kind serves the user's goal |
| each kind's lie rate | the user's bait susceptibility for that kind |
| each kind's reliability belief (`SelfModel.Skills`) | the same taste, as the design believes it |
| the prediction record (`PredictionTracker`) | how often the world's predictions are right for this user |

Distrust is not carried between sessions (the Core's rule); beliefs and the track record are.

**The comparison report,** per user and per archetype, for each policy:

- **how close:** each kind's learned rate against the profile's true rate, and whether the kinds are ranked in the same order;
- **how fast:** sessions until the learned rates are within a tolerance of the truth;
- **bait caught:** the kinds the record marks as lying against the kinds that really serve no goal for this user (precision and recall);
- **context:** whether the muted commuter's skips were read as a dislike (wrong) or left to context (right);
- **after drift:** sessions until the drifter's record follows the change;
- **false learning:** for the noisy user, any confident belief counts against the policy.

All of these are read from the records and the profiles after the runs by one small reader; no new runner.

**Later: one trained predictor, shared, not one per user.** The runs write rows of context, kind, predicted response and real response: training data. A small predictor can be trained on them later (ModelGym, the epic's Phase 7), with the user's traits as inputs rather than a model per user, which would need much data per person and, for real app users, much personal data. The lab's advantage holds here too: each test user's truth is known, so the trained predictor is measured against the truth exactly, which real data never allows.

## The thesis, and the closed loop (slice D, #67)

*Added 2026-11-12 with the owner. The study is run like every Creatura design note: the claims and their falsifiers are written before any code, the runs are offline and repeatable, and the conclusions go back into the same note.*

**Thesis.** A recommender that judges each play by whether it served the user's own goal (Creatura's control layer) meets users' goals more often than one that maximises predicted engagement, at a small cost in engagement, and its record of each user ends up close to that user's true taste.

**Pre-registered hypotheses.** The numbers are proposals the owner may change before the runs, never after.

| id | claim | falsifier |
|---|---|---|
| T1 | the designs meet the user's goal more often than `max-staytime` on the test users | not more often, or the leaderboard cannot tell them apart after the planned runs |
| T2 | the engagement they give up is small: at most 25% less engagement reported than `max-staytime` | more than 25% less |
| T3 | with the late scroller, bait plays fall from session to session under the designs, and not under `max-staytime` | they do not fall |
| T4 | after 10 sessions, each kind's learned landed rate is within 0.15 of the profile's true rate, and the kinds are ranked in the same order (rank correlation at least 0.8), for the learner, the connector and the late scroller | further off, or ranked differently |
| T5 | the muted commuter's skips while muted are not learned as a dislike of the kinds that need sound | they are |
| T6 | after the drifter's change, the record follows within 5 sessions | it takes longer |
| T7 | the noisy user produces no invented preference: after 10 sessions no kind's learned rate is more than 0.15 from its true rate, whose differences come only from the shared goal mapping | any kind further off |
| T8 | the `last-resort` rule lowers skimming but does not raise goals met compared with `max-staytime` | it raises goals met |

**Two failures are expected, and say so in advance.** The record keeps one count per kind of video (`SkillBelief`: supported and attempted; `EffectMemory`: tried, landed, failed, lied), with **no context and no fading of old evidence**. So T5 should fail (a skip while muted counts as a dislike) and T6 should be slow (old sessions weigh as much as new ones). If they fail as predicted, the study has found the limits of a per-kind memory, and the next round knows what to add.

**The closed loop:**

1. Write the thesis and the hypotheses with their numbers, before any code (`docs/design-axi.md`).
2. Build the users and check them: the oracle policy clearly beats a random one for every user except the noisy one.
3. Run every policy against every user over many seeded sessions: offline, no paid call, repeatable seed by seed.
4. Report: the leaderboard on both axes and the learned-versus-known comparison.
5. Conclude each hypothesis: holds, falls, or open (more runs needed), in the note's Results section.
6. Each hypothesis that fell becomes a proposed change (fading old evidence, context in the record, ...). The owner approves it; it is built; the same seeds run again and the two results are compared; back to 4.
7. Stop when the hypotheses hold or the owner stops.

**What the conclusions may claim.** Every result is about synthetic users whose rules we wrote, so the thesis concludes about the method, not about real people on TikTok. Two later steps close the loop with reality: base rates from real engagement aggregates, then one pilot in a Singular app. Until then every conclusion says "in the simulation".

**A local model in the seat.** Besides the planner and the designs, the recommender's seat can be a small local model (Liquid AI's LFM2 through Ollama, already in Creatura's seat providers), run as one more policy. It is optional: the hypotheses are decided on the deterministic policies.

## Slices

| slice | issue | what | passes when |
|---|---|---|---|
| A | #63 | this note | the owner has read the thesis before the runs |
| B | #64 | the virtual TikTok world, its barriers, the two baselines | golden runs; each barrier fails a plain loop where it should |
| B2 | #65 | the synthetic users, the checks, the oracle | every profile valid; the oracle beats random by at least 15 points for every user |
| C | #66 | `samples/Axi` | slice 1 runs offline and prints the four shapes |
| D | #67 | the study | T1 to T8 each concluded: holds, falls or open |
| E | #68 | the grid setting | as B, on the grid |
| F | #69 | slice 1 with a Singular app | the app receives the shapes and the outcome |

## What this note does not cover

An `src/Creatura.Axi` project, an AXI object model, a new event bus, capability registry, approval system, planner, memory store or report format, any ML, and anything that lets AXI change a regulator, a permission, a system prompt or a safety rule.

## Results

### Slices B and B2 (2026-11-12): the world and the users

The virtual TikTok is `tiktok` in the catalog (#71); the six archetypes, their variants, checks, truth and oracle are
`FeedCast` (#72). Every archetype is learnable: the oracle meets the goal within 5 plays 31 to 50 points more often than
random choice (train seeds; measured after codex's fixes). One correction before any run: the noisy user shows that gap too, because knowing the
session's goal helps with any user; its control role is its flat taste, and T7 was reworded to compare with its true rates.

### Slice D, round 1 (2026-11-12): the study

`dotnet run --project samples/FeedStudy -- --seeds 10 --sessions 20`: 6 archetypes × 10 test variants (seeds 101-110)
× 20 sessions, every barrier on, at most 20 ticks a session; the user leaves when served or tired. The designs wrap the
same `max-staytime` chooser, so "max-staytime" against "bach+max-staytime" differs only by the control layer. About 4 s
on the Mac Studio; a second run gives a byte-identical report. Every conclusion below is **in the simulation**.

**A first pass was withdrawn.** Codex reviewed the first pass (structure, then meaning) and found defects in the
instrument and in how the verdicts were computed: the truth left out the noise's clipping at 0 and 1; a failed ad was not
counted as a play; the noisy user's variants lost their flat taste; the profile checks let an unknown goal through; and
in several places the verdict code used a window, a minimum number of plays or a majority allowance the hypothesis table
never stated, and measured engagement as seconds watched where this note defines engagement reported as the predictions
acted on. All were fixed and round 1 was run again; the numbers below are that run. Where the hypothesis table cannot be
computed as written, the rule used is stated here. **These operational rules were set after the first pass was seen,**
which makes them weaker than rules written before any run; they are stated before the rerun and applied as written:

| id | the rule used |
|---|---|
| T1 | goal met (served or not) per session; the leaderboard is computed on that alone; "not told apart" (p < 0.95) falls, as the table says |
| T2 | engagement reported = the predicted stay time of every play made, summed per session |
| T3 | "falls from session to session" = the least-squares slope of the late scroller's mean bait plays per session over the session index is below 0, and the baseline's is not |
| T4 | as the table: every one of the 7 kinds, for every learner, connector and late scroller user |
| T5 | read after 10 sessions, as T4 and T7 are; a belief (any play counted) about a kind that needs sound "reads the context as a dislike" when it is nearer the truth with muted sessions than the taste without them; holds only if none does (and holds, said so, when there is no belief) |
| T6 | "follows" = every belief within 0.15 of the new truth (T4's tolerance); within 5 sessions of the change, for every drifter user |
| T7 | every belief (any play counted), for every noisy user |
| T8 | "skimming" = plays made while the user is skimming; holds only if the paired mean is below 0 and the paired mean of goals met is not above 0 |

| policy | goal met | contract kept | engagement reported (s) | watched (s) | bait plays | plays a session |
|---|---|---|---|---|---|---|
| oracle (a strong reference) | 91% | 90% | 75.3 | 38.3 | 0.00 | 3.1 |
| orienting + max-staytime | 76% | 74% | 135.6 | 70.1 | 1.39 | 5.7 |
| last-resort | 76% | 74% | 183.5 | 93.8 | 1.85 | 6.0 |
| max-staytime | 71% | 70% | 206.1 | 94.0 | 2.07 | 6.2 |
| peterson + max-staytime | 67% | 65% | 140.3 | 71.1 | 1.59 | 6.1 |
| bach + max-staytime | 60% | 60% | 81.4 | 47.9 | 0.75 | 3.4 |
| levin + max-staytime | 60% | 60% | 81.4 | 47.9 | 0.75 | 3.4 |
| random | 58% | 55% | 100.1 | 58.0 | 1.15 | 8.0 |
| hybrid + max-staytime | 55% | 55% | 71.7 | 43.3 | 0.64 | 2.9 |

**Every hypothesis falls in round 1.** The thesis, as stated, does not hold in the simulation.

| id | verdict | in a line |
|---|---|---|
| T1 | falls, every design | goals met against max-staytime: Bach and Levin −11.8 points, Peterson −4.9, Hybrid −16.7, Orienting **+4.3** (paired 95% [+1.7, +7.0]), but the leaderboard tells no design apart (p 0.40 to 0.55) |
| T2 | falls, every design | engagement reported kept: Peterson 68%, Orienting 66%, Bach and Levin 40%, Hybrid 35% |
| T3 | falls, every design | the baseline's bait plays also drift down (slope −0.015), so the rule cannot hold; Peterson (−0.024) and Orienting (−0.019) fall faster than the baseline, Bach does not fall (+0.001) |
| T4 | falls, every design | no user has a belief about every kind: the designs hold beliefs about 3.4 to 4.8 kinds of 7 |
| T5 | falls, every design | Bach and Levin: 26 of 30 beliefs read the muted commuter's context as a dislike; Peterson 8 of 24, Orienting 3 of 25 (most of theirs sit at or above the taste, because they never count a skip) |
| T6 | falls, every design | no record follows the drifter's change within the 10 sessions after it |
| T7 | falls, every design | 4 of 10 noisy users under Bach have a belief more than 0.15 off; 9 under Peterson, 10 under Orienting |
| T8 | falls | the last-resort rule plays less while skimming (−0.19 plays a session) but also meets more goals (+4.2 points), because live serves two of the three goals |

**What round 1 found:**

1. **The control layer, as it is, makes a recommender quit.** Bach, Levin and Hybrid distrust a kind after one skip and
   stop after five failures: about 3 plays a session against 6.2 for the bare chooser. In a task world a failure means
   something is wrong; in a feed a skip is ordinary. Orienting, which keeps going, is the one design that meets more goals
   than the bare chooser, by a margin the paired test sees and the leaderboard does not.
2. **It does protect the user from bait, within a session.** Under Bach the late scroller sees a third of the bait plays
   it sees under max-staytime; only Peterson and Orienting improve on that from session to session.
3. **What is learned is accurate where it looks, and it does not look everywhere.** Bach and Hybrid's records are within
   0.04 to 0.10 of the user's true rate on the kinds tried 3 or more times (rank correlation 0.59 to 0.95), but they hold
   beliefs about fewer than 5 of the 7 kinds.
4. **Two records learn two different things.** Peterson and Orienting never count a skip (an honest failure), so their
   records learn which kinds serve the goal once finished, not the user's taste.
5. **The two expected failures happened:** no context (T5) and no fading of old evidence (T6). With few plays, a flat
   user also looks as if it had preferences (T7).
6. **Bach and Levin made the same plays in all 1,200 sessions.** With the same chooser both distrust a kind after one
   failed play and both stop after five, so they never diverge; Hybrid differs in 524.
7. **The leaderboard is too blunt for a yes-or-no outcome.** It told no adjacent pair apart in 1,200 games, while the
   paired differences exclude zero for every design. T1 as registered can hardly hold on this outcome.

**Proposed for round 2,** each for the owner's approval before it is built, written into this note before round 2 runs,
then the same seeds again:

| | change | aimed at |
|---|---|---|
| P1 | in a feed a skip is an honest failure that does not count toward stopping (the layer's report-first judge and habituation, already options) | T1, T2 |
| P2 | try kinds not yet tried (the exploration extension, already in the Core) | T4 |
| P3 | fade old evidence in the record (a window or a decay on a skill belief) | T6 |
| P4 | context in the record: a belief per kind and muted or not | T5 |
| P5 | a belief counts only after enough plays (for example 5) | T7 |
| P6 | decide T1 by the paired difference, and T3 against the baseline's slope rather than requiring it to be flat | T1, T3 |

### Slice D, round 2: pre-registered 2026-11-12, before it was built or run

The owner approved P1 to P6 ("let's do your recommendation", 2026-11-12). Same seeds, users, sessions and barriers as
round 1; the baselines are unchanged. Each design runs once more as `<design>+r2`, with these changes:

| | what is built | how |
|---|---|---|
| P1 | a skip is an honest failure and does not count toward stopping | each design's own layer with `JudgeRule.ReportFirst` and `HonestFailureRule.Habituate`, options the Core has |
| P2 | try kinds not yet tried | a seat wrapper in the study: one play in four (seeded) is the offered kind with the fewest counted plays in the user's record; otherwise max-staytime. The Core's exploration extension acts only when there is no route, and this chooser always has one |
| P3 | fade old evidence | before each session, a belief with more than 10 counted plays has its counts halved (a study extension; the record's own type is untouched) |
| P4 | context in the record | one record per design, user **and context** (muted or with sound): the record key carries it |
| P5 | a belief counts only after 5 counted plays | in the reading: a kind with fewer is "no belief" |
| P6 | the deciders | T1 holds when the paired 95% interval of goals met against max-staytime lies above 0. T3 holds when the late scroller's bait slope is below 0 **and** below the baseline's slope |

The other rules are round 1's operational rules, with one change from P4: T4, T6 and T7 compare the **with-sound**
record with the truth with sound (`TrueLandedRate` with no muted sessions), and T5 holds when no with-sound belief about a
kind that needs sound is nearer the muted truth than the taste.

**Predicted before the run** (predictions, not rules):
- P1 stops the quitting: goals met and engagement rise for Bach, Levin and Hybrid; T1 and T2 may hold.
- **P1 has a cost the Core forces:** under `Habituate` a skip is recorded nowhere, so every design's record learns which
  kinds serve once finished, as Peterson's and Orienting's did in round 1, and not the user's taste. T4 should still fall.
  Learning taste without quitting needs an option the Core does not have (an honest failure recorded but not spent): if
  round 2 shows this, that is the one Core change the study points to.
- P4 should make T5 hold; P2 should raise the number of kinds with a belief; P3 should shorten T6's lag.

### Slice D, round 2 (2026-11-12): the results

Run as pre-registered, the same 1,200 sessions per policy; round 1's verdicts in the same report are unchanged.

| policy | goal met | engagement reported (s) | bait plays | plays a session |
|---|---|---|---|---|
| max-staytime (baseline) | 71% | 206.1 | 2.07 | 6.2 |
| bach + r2 (round 1: 60%) | 71% | 123.2 | 1.25 | 6.3 |
| levin + r2 (round 1: 60%) | 71% | 123.2 | 1.25 | 6.3 |
| orienting + r2 (round 1: 76%) | 69% | 132.9 | 1.45 | 6.5 |
| peterson + r2 (round 1: 67%) | 63% | 135.7 | 1.64 | 6.8 |
| hybrid + r2 (round 1: 55%) | 52% | 75.2 | 0.73 | 3.7 |

| id | verdict | in a line |
|---|---|---|
| T1 | falls, every design | Bach and Levin now meet as many goals as the baseline (−0.2 points, 95% [−3.0, +2.7]), not more; Orienting −2.2, Peterson −8.6, Hybrid −19.1 |
| T2 | falls, every design | engagement reported kept: Peterson 66%, Orienting 64%, Bach and Levin 60%, Hybrid 36% |
| T3 | falls, every design | no design's bait slope is below the baseline's (−0.015); Bach's now rises (+0.014) |
| T4 | falls, every design | a kind reaches 5 counted plays for about 1 kind in 7 |
| T5 | holds, **vacuously** | no design has any with-sound belief about a kind that needs sound for the muted commuter: nothing to read as a dislike |
| T6 | falls, every design | no record follows the drift |
| T7 | falls, every design | 4 to 7 of 10 noisy users pass |
| T8 | falls | unchanged (the baselines did not change) |

**What round 2 found:**

1. **P1 stopped most of the quitting, and only that.** Bach and Levin play as long as the baseline (6.3 plays against
   3.4 in round 1) and meet 11 points more goals than in round 1, level with the baseline, while keeping 40% fewer bait
   plays. They still do not meet more goals than the bare chooser. Hybrid still quits: its own coherence audit stops it.
2. **The predicted cost came, larger than expected.** Under `Habituate` a skip is recorded nowhere, so a kind gets counted
   plays only when the user finishes it. With P5's floor of 5 plays and P4 splitting the record by context, about one
   kind in seven has a belief: the records learned almost nothing. T5 "holds" only because there is nothing to read.
3. **Exploration (P2) cost goals and bought no learning.** Its plays go to the least-tried kinds, which are mostly the
   ones the user skips, and a skip is no longer recorded: Orienting fell from 76% to 69%.
4. **Fading (P3) had nothing to fade:** no belief reached 10 counted plays.

**The one Core change the study points to.** Learning a user's taste without quitting needs an honest failure that is
**recorded but not spent**: counted in the tool's reliability and the prediction record, not in the budget that stops
the run. The Core has `Habituate` (not recorded, not spent) and `StaysVisible` (recorded and spent); neither does it.
That is a third value of `HonestFailureRule`, a few lines in `ControlLayer`, and the owner's decision, since the plan
promised no Core change.

**Proposed for round 3,** for the owner's approval:

| | change | aimed at |
|---|---|---|
| R3-1 | the Core option above: an honest failure recorded, not spent, the tool kept visible | T1, T4 together |
| R3-2 | explore less and only among kinds the predictions do not rule out (one play in ten) | T1 |
| R3-3 | keep P4 (a record per context) and P3 (fading); drop P5's floor to 3 plays | T4, T5, T6 |

### A local model in the seat (2026-11-12): Liquid AI LFM3-1.7B

Bach with LFM3-1.7B choosing (llama.cpp on the MacBook Air, 100.64.0.5:8080, native tool calls, no paid call), on round
1's corrected instrument, one test variant per archetype (seed 101) × 20 sessions: 120 sessions, 495 calls, 6 invalid
(1.2%), 3.3 s a call, 27 minutes. Against the other policies **on the same 120 sessions**:

| policy | goal met | engagement reported (s) | watched (s) | bait plays | plays a session |
|---|---|---|---|---|---|
| oracle | 90% | 92.7 | 38.3 | 0.00 | 3.6 |
| max-staytime | 74% | 190.1 | 84.7 | 2.29 | 6.0 |
| orienting + max-staytime | 74% | 129.3 | 62.8 | 1.52 | 5.5 |
| random | 62% | 97.6 | 55.2 | 1.07 | 7.9 |
| bach + max-staytime | 60% | 78.3 | 44.2 | 0.89 | 3.3 |
| **bach + LFM3-1.7B** | **51%** | 49.7 | 30.5 | **0.34** | 3.1 |

It answers with one listed tool almost every time, as issue #55 found, and it plays the least bait of any policy. It also
asks the user what they want in every session (120 asks, once each: the world no longer offers it twice). But it meets
fewer goals than random choice: its most-played kind is the ad (102 plays), which serves no goal, so it is not reading the
predictions and the goal it is told. One variant per archetype is too few to decide anything; it is a first look, and a
seat to try again once the design stops quitting (round 3).

### Slice D, round 3: pre-registered 2026-11-12, before it was built or run

**Why rounds 1 and 2 did not learn** (diagnosed after round 2, with the owner): the learning went into the record and never
came back out. The chooser in every design was max-staytime, which reads the feed's predictions and never the record, and
no design chooses by its learned reliability (Bach reads its skills only to print them at sleep). Goals met were flat
across the 20 sessions for every policy (Bach round 1: 56%, 54%, 63%, 52% by blocks of 5 sessions). Round 2 then stopped
recording skips at all. Round 3 closes the loop; the owner said yes ("yes pre-register", 2026-11-12), including the Core
change.

| | what is built | how |
|---|---|---|
| R3-1 | **the Core option**: an honest failure recorded, not spent | `HonestFailureRule.RecordOnly`: recorded in the predictions and the per-tool record as a failure, not counted toward the budget, not passed to the design's own failure accounting, the tool kept visible. `RecordCarryExtension` gains a switch to record honest failures for the designs without a self-model. Its own pull request on Creatura, with tests |
| R3-2 | **a seat that chooses from the record** | Thompson sampling in the study: for each kind on offer, a draw from Beta(1 + 2p + s, 1 + 2(1 − p) + n − s), where p is the feed's predicted finish, s and n the kind's landed and counted plays in this user's record for this context; the highest draw is played. Seeded per session. It reads the record; it does not see the truth or the session's goal |
| R3-3 | keep P4 (a record per context) and P3 (fading counts above 10); P5's floor drops to 3 plays; no P2 (the sampling explores) | as in round 2 |

Each design runs once more as `<design>+r3`, with its own layer plus `ReportFirst` and `RecordOnly`, the Thompson seat
and the record-carry extension recording honest failures. Same seeds, users, sessions and barriers.

**The new test, and the one the loop is about:**

| id | claim | falsifier |
|---|---|---|
| L1 | learning reaches behaviour: goals met in sessions 16-20 are higher than in sessions 1-5, every archetype but the drifter pooled | the difference's 95% interval (normal approximation, sessions as units) does not lie above 0 |

T1 to T8 are decided as in round 2, with P5's floor at 3 plays.

**Predicted before the run:** L1 holds for Bach and Levin (+r3); T1 may hold for them, since the sampling stops trusting
the over-predicted bait once the record says otherwise; T4's coverage rises (skips are recorded again); T5 should hold
with beliefs this time; T6 should shorten (fading plus sampling). T7 may still fall: sampling acts on few plays.

**Do we need another round? The stopping rule, set now:** the loop stops after round 3 if L1 holds and T1 holds for at
least one design. If not, one more round is run only if the result names one specific change; round 4 is the last
either way, and the note then says what holds and what does not.

### Slice D, round 3 (2026-11-12): the results

Run as pre-registered on seeds 101-110. **Corrected after codex's third review:** P3's fading ran after the seat had
already read the record, and Hybrid restored its unfaded counts, so the first run of rounds 3 and 4 had no working fade;
the fade now runs before anything reads the record, and both rounds were run again. Rounds 1 and 2 give the same
verdicts (the leaderboard's p values move in the second decimal, because it now rates more policies).

| policy | goal met | engagement reported (s) | bait plays | plays a session |
|---|---|---|---|---|
| levin + r3 | 75% | 95.9 | 0.52 | 5.3 |
| bach + r3 | 73% | 93.8 | 0.52 | 5.2 |
| orienting + r3 | 72% | 104.6 | 0.64 | 5.8 |
| max-staytime (baseline) | 71% | 206.1 | 2.07 | 6.2 |
| peterson + r3 | 68% | 102.6 | 0.73 | 5.9 |
| hybrid + r3 | 59% | 60.4 | 0.33 | 3.3 |

| id | verdict | in a line |
|---|---|---|
| L1 | falls, every design | goals met in sessions 16-20 are not above sessions 1-5 (Bach 76% to 70%, Levin 76% to 73%) |
| T1 | **holds for Levin**; falls for the others | Levin +3.7 points against max-staytime (95% [+0.8, +6.5]); Bach +1.8 [−1.1, +4.6]; Orienting +0.3; Peterson −3.7; Hybrid −12.2 |
| T2 | falls, every design | engagement reported kept: 29% to 51% |
| T3 | **holds for Levin, Peterson, Hybrid** | the late scroller's bait plays fall faster than the baseline's (slopes −0.019 to −0.030 against −0.015); Bach −0.014, Orienting −0.007 |
| T4 | falls, every design | beliefs about 2.8 to 4.0 kinds of 7; 2 users of 30 pass under Peterson |
| T5 | holds for Peterson and Orienting, falls for Bach, Levin, Hybrid | few with-sound beliefs exist (1 to 3 per design); 1 or 2 of them read the context as a dislike |
| T6 | falls, every design | no record follows the drift within 10 sessions |
| T7 | falls, every design | 6 to 9 of 10 noisy users pass |
| T8 | falls | unchanged |

**How much was there to learn?** A diagnostic, not pre-registered (bare choosers, the same sessions, up to 20 plays):

| chooser | goal met | |
|---|---|---|
| the feed's predictions (max-staytime) | 71% | |
| a chooser that knows each user's true per-kind rate, not the session's goal: the best a per-user record can reach | 81% | late scroller +12 points over the predictions, drifter +13, noisy +26; learner, connector, muted commuter about +1 to +5 |
| the oracle, which also knows the session's goal | 91% | |

**Why L1 fell, read after the run:** every policy meets the same users in the same batches at a given session index, so a
session's difficulty moves every policy together (the oracle itself goes from 76% to 96% between sessions). Comparing
sessions 1-5 with 16-20 without the baseline measures the sessions as much as the learning. Against the baseline on the
same sessions (a reading, not a test): Bach is 6 points under it in session 1 and mostly above it afterwards, between −6
and +8 points from one session to the next; for the noisy users Levin goes from 45% (sessions 1-2) to 64% (sessions
11-20) while the baseline goes from 70% to 38%. L1 as registered could not separate this from session difficulty.

### Slice D, round 4: pre-registered 2026-11-12, before it was run; the last round

The stopping rule allows one more round when the result names one specific change; round 3 names one: **measure L1
against the baseline on the same sessions, per user.** Because that change was chosen after seeing round 3, round 4 runs
on **test seeds 111-120, which no run has used.** Nothing else changes: round 3's settings, users, sessions, barriers.

| id | claim | falsifier |
|---|---|---|
| L1′ | learning reaches behaviour: for each user, the gap in goals met between a design and max-staytime on the same sessions is larger in sessions 11-20 than in sessions 1-2; every archetype but the drifter, users as units | the 95% interval (normal approximation over users) of the mean change in gap does not lie above 0 |
| T1 | as in round 3, on the new seeds | as in round 3 |

**Predicted before the run:** L1′ holds for Bach and Levin, carried mostly by the noisy users and the late scroller (the
users with room to learn); T1 holds again for Levin, perhaps Bach. **This is the last round.** Afterwards the note says
what holds and what does not, and the loop stops.

### Slice D, round 4 (2026-11-12): the results, on unused seeds 111-120

`dotnet run --project samples/FeedStudy -- --seeds 10 --seed-skip 10 --sessions 20`, round 3's settings unchanged (run
again with the fade fixed, as round 3 was).

| id | verdict | in a line |
|---|---|---|
| L1′ | falls, every design | the change in the per-user gap to the baseline, sessions 11-20 against 1-2: Bach +1.0 points (95% [−9.6, +11.6]), Levin −4.8 [−15.8, +6.2], Peterson +3.8, Orienting −3.0, Hybrid +7.8 [−3.6, +19.2]; 50 users each |
| T1 | **holds for Levin and Orienting** | Levin +4.3 points against max-staytime (95% [+1.6, +6.9]), Orienting +3.6 [+0.8, +6.4], Bach +2.4 [−0.3, +5.1], Peterson −3.1, Hybrid −10.5 |

Goals met on these seeds: oracle 92%, Levin + r3 76%, Orienting + r3 75%, Bach + r3 74%, max-staytime 71%.

## What holds and what does not (the loop stops here, 2026-11-12)

Four rounds. Each round's rules were written before it ran, except round 1's operational rules, which were set after its
first pass had been seen and are marked so; the last round ran on users no run had used. In the simulation:

**Holds**
- **With one Core option and a seat that reads the record, two of the designs meet more users' goals than an
  engagement maximiser.** Levin: +3.7 points against max-staytime on seeds 101-110 and +4.3 on the unused seeds 111-120;
  Orienting: +3.6 on the unused seeds (not on 101-110). The option is `HonestFailureRule.RecordOnly` (#75): a skip
  recorded but not spent. The seat is Thompson sampling over the per-user record, with the feed's prediction as its prior.
- **Bait plays drop.** Round 3's designs play a sixth to a third of the bait max-staytime plays (0.33 to 0.73 bait plays a session against 2.07); for Levin, Peterson
  and Hybrid the late scroller's bait falls faster across sessions than the baseline's (T3).
- **Where a record has evidence, it is close to the truth** for the designs that keep a self-model: in round 1, Bach's and
  Hybrid's records were within 0.04 to 0.10 of the true rate on the kinds tried 3 or more times (rank correlation 0.59 to
  0.95).

**Does not hold**
- **Improvement across sessions is not shown** (L1, L1′ fall). Read against the baseline, round 3's designs start below
  it in the first session and are mostly above it afterwards, but neither test shows a gain that grows, and L1′'s
  intervals are about ±10 points wide.
- **Engagement is kept** (T2): the designs report half or less of the engagement the maximiser reports.
- **The records do not learn every kind** (T4), **do not separate context from taste reliably** (T5), **do not follow a
  change of taste within 10 sessions** (T6), and **show preferences a flat user does not have** (T7).
- **The last-resort rule** meets more goals than the maximiser, not fewer (T8).
- **Bach, Peterson and Hybrid** do not beat the maximiser on goals, and **Hybrid** still quits early (3.3 plays a session).

**What it means for Creatura:** the control layer, as designed for task worlds, counts a failure toward stopping; in a
feed, where a skip is ordinary, that made the designs quit (round 1), and the documented alternative records nothing, so
nothing was learned (round 2). One option in the Core and one seat in the example gave two designs more goals met with
far less bait (rounds 3 and 4). Context, drift and learning that keeps improving stay open: this study added a record per
context and a fade outside the Core and saw no effect on T5 and T6; whether they belong in the Core is not tested here.

**What it means for AXI:** the agent side of AXI on Creatura needs no AXI engine. The four JSON shapes and an example are
enough to exchange capabilities, proposals, events and outcomes (#66), and the outcome must keep reported and confirmed
apart: in the slice, the feed reported 228.7 s of predicted engagement for a user who watched 5.3 s.

**Not covered:** real people (every result is in the simulation); a model seat beyond one small run (Liquid AI
LFM3-1.7B in round 1: 51% goals met, below random); the grid (Instagram-like) setting (#68); several predictors judged by
their records; the Singular side (#69).
