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
