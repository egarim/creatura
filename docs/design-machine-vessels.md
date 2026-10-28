# Design: machine vessels acting together

Status: built 2026-10-28; first battery run below.

## The idea, in the owner's words

> Give these agents vessels that are machines, virtual ones like a truck, not only a computer but a physical machine,
> and they need to act together to solve a scenario, all of this in a memory mesh.

## Why it belongs in Creatura

Creatura's claim is that regulation patterns keep an agent on course in a world that can lie. So far every test has
one agent in one world. A scenario where several machines share one world and must coordinate over an unreliable
link tests the same claim at the level of a group. That is where Levin's colony, the work ledger and the cells were
heading anyway.

Most of the parts exist:
- **The vessel** says what an agent runs on. A truck is one more kind of vessel.
- **Worlds that lie**: a flooded road reported open is the same fault as a tool that reports success and changes
  nothing.
- **The in-memory mesh**: `InProcessCellHub` runs cells with no network. It already duplicates messages and takes
  cells offline.
- **The ledger** answers who is working on what. Claims, progress, handoffs and releases are exactly what a convoy
  needs: "truck 2 takes village B", "truck 3 is stuck", "delivered".
- **The bench** runs pre-registered experiments with arms, an oracle the agents cannot influence, and a verdict per
  hypothesis.

## What is new

### 1. A machine half for the vessel

A vessel has a model half (`Vessel` in the Core) and, on a computer, a hardware half (`Hardware` in the cells). A
machine is a third description: a **`Machine`** record with its kind, capacity, fuel and fuel per step, speed, and
the sensors it carries (decision D4). The record is the specification. The machine's actual state (where it is, how
much fuel it has, what it carries) belongs to the world, which is the truth. The agent only believes it, from what
its sensors report, and sensors can lie.

### 2. A shared world with a view per machine

A **`SharedWorld`** holds the map and every machine's true state, and gives each machine its own `IWorld` view. Each
view holds:
- what that machine senses: its position, its fuel and load, the roads it can see;
- that machine's tools: drive to a node, load at the depot, unload at a village, refuel, sense a road;
- the coordination tools that write to its cell's ledger: claim a delivery, report progress, hand off, release.

Each machine is an ordinary `ReferenceLoop` with its own regulator, acting on its own view. The loop does not change.

### 3. A scheduler that keeps time

A **`Scenario`** steps the world one tick at a time:
1. every machine's loop takes one decision, in a fixed order by machine id;
2. the world applies movement, fuel and faults;
3. the mesh delivers what was sent.

Everything random comes from one seed: road closures, sensor lies, message loss. The same seed gives the same run
byte for byte, so a scenario can be gated with golden traces like the rest of Creatura.

### 4. Coordination through the ledger

Each machine is a cell, with a key, a ledger and a place on the in-memory mesh. A **coordination extension** brings
the mesh into the loop. At the start of each tick it merges what arrived. It shows the machine, as facts in its view,
which deliveries others have claimed, how far each has got, and which claims went quiet. The ledger only says who
claimed what. What a machine does about it is its regulator's choice, plus one shared rule (decision D5): a claim
with no progress or heartbeat for K ticks may be taken over, by a new claim that names it.

The in-memory mesh learns to drop and delay messages from the seed, so the link can be as bad as a real convoy's
radio.

### 5. The judge

The scenario's oracle reads only the world's own facts, which no agent can write: supplies that actually reached each
village, deliveries made twice, trucks stranded, lies believed, ticks used. Supplies a truck reports but the world
did not receive count as **claimed**, never as **delivered**.

## The first scenario: a convoy after a flood

- **The map:** one depot, four villages that need supplies, about twelve road segments between them.
- **The machines:** three trucks of different capacity and fuel range.
- **The faults:**
  - two roads close during the run;
  - one closed road keeps reporting itself open to the trucks' road sensors;
  - one truck's fuel gauge reads high.
- **The link:** the in-memory mesh, with message loss in some arms.

### Arms

| arm | trucks run | ledger | link |
|---|---|---|---|
| A | the plain loop (control layer only) | shared | perfect |
| B | the designs, one per truck: Levin's colony, Bach's self-model, Peterson's map keeper | shared | perfect |
| C | the plain loop | none: each truck alone | — |
| D | the plain loop | shared | 30% of messages lost, some late |
| E | the plain loop | shared | perfect, and one truck breaks down at tick 8 |

Each arm runs over the same 50 seeds.

## Decisions for the owner

| | decision | options | recommendation |
|---|---|---|---|
| D1 | the scenario | a convoy after a flood; warehouse robots; farm machines | **the convoy**: it continues the flood-relief world and its lies are easy to read |
| D2 | the size | 3 trucks, 4 villages, ~12 roads; bigger | **3, 4 and ~12** first; bigger only after the hypotheses have verdicts |
| D3 | who decides for each truck | offline planners only; real models in some seats | **offline planners** for every gated run. Model seats are a separate, optional arm, because they cost money per tick |
| D4 | where the machine's description lives | a `Machine` half on the vessel; a separate record on the world | **on the vessel**, beside the model half, so a regulator can read its own machine as it reads its own model |
| D5 | taking over a quiet claim | never; after K ticks without progress or heartbeat | **after K = 5 ticks**, as a rule of the trucks' planner, written to the ledger as a new claim naming the quiet one |
| D6 | what counts as success | delivered against demand, deliveries made twice, trucks stranded, lies believed, ticks | **all five**, with delivered judged only by the world |
| D7 | where it runs | the in-memory mesh only; also across the laptop and the second machine through the hub | **in memory** first; the hub version later, as its own step |

Decided by the owner, 2026-10-28: every recommendation (D1 the convoy, D2 3 trucks, 4 villages and about 12 roads,
D3 offline planners, D4 the machine on the vessel, D5 takeover after 5 quiet ticks, D6 all five measures, D7 in memory
first). The owner also asked for a way to watch a run as a story, in three.js or anything else that helps: see
"Watching a run".

Corrections made while building, before the code:
- **A takeover is a new claim.** Only the cell that made a claim can sign its handoff, so a truck taking over a quiet
  claim writes a claim of its own that names the quiet one.
- **The mesh is the scenario's own.** Seeded, in-memory delivery with loss and delay between the trucks' ledgers,
  rather than changes to `InProcessCellHub`, which other tests rely on.
- **"Byte for byte" is the run's story.** Cell keys and ECDSA signatures are random, so the signed ledger differs
  between two runs of one seed. What M6 compares is the replay: positions, fuel, loads, actions, verdicts, deliveries,
  messages. Ties between trucks go by truck name, never by cell id.
- **The trucks' planner understands position.** The Core's breadth-first planner assumes facts only accumulate, which
  a moving truck breaks: it is no longer at the depot once it leaves. The trucks get an offline planner of their own
  that knows roads, fuel and loads. The control layer and the designs wrap it unchanged.
- **A bigger flood.** The first smoke run had every village served by tick 12, before the breakdown at tick 20 and
  before most closures mattered, so nothing was tested. Demand went from 18 to 30 units (two trips for every truck),
  closures to ticks 2 to 10, and the breakdown to tick 8, while the broken truck still holds a claim.
- **The hypotheses are experiments, not unit tests.** The tests check the mechanics (the world as truth, the lying
  road caught, determinism, ledgers converging). M1 to M7 get verdicts from a battery run, reported as found, whether
  they hold or fail.

## Pre-registered hypotheses, stated before the code

| id | claim | falsifier |
|---|---|---|
| M1 | with a shared ledger, deliveries made twice are fewer than without one (A against C) | as many or more in A over the 50 seeds |
| M2 | with a shared ledger, the villages get at least as much of their demand (A against C) | less delivered in A |
| M3 | the control layer catches the road that lies: no truck counts the lying road's report as true twice | a truck that drives into the lying road a second time after it failed once |
| M4 | with 30% of messages lost, the ledgers still end identical after a final sync, and deliveries made twice stay below the arm without a ledger (D against C) | ledgers that differ, or D at or above C |
| M5 | when a truck breaks down, its claim is taken over and its village is served whenever another truck has the capacity (E) | a village left unserved while a truck with capacity stood idle |
| M6 | the same seed gives the same run, byte for byte | any difference between two runs of one seed |
| M7 | the designs in arm B deliver at least as much as the plain loop in arm A, with fewer lies believed | less delivered, or more lies believed, in B |

M7 is the one the framework is for, and the one most likely to fail. It is stated so it can.

## What gets built, in order

1. `Machine` on the vessel, and `SharedWorld` with the convoy map, its trucks and its faults.
2. `Scenario`, the scheduler, with the seed and the golden traces (M6).
3. Message drop and delay in the in-memory mesh, and the coordination extension with the takeover rule.
4. The oracle and arms A to E as a battery, with M1 to M7 scored per arm.
5. The replay: each tick, where each truck is, what it believed, what was true, what was said on the mesh.
6. A viewer that plays a replay back as a story.

## Watching a run

The scenario writes a **replay**: one JSON line for the map and the trucks, then one line per tick with every truck's
true and believed state, the events of the tick (actions and their verdicts, claims, deliveries, closures, lies
caught, messages lost) and a sentence for each event. Anything that reads JSON lines can show a run. The format is
the interface for UIs, documented in `docs/replay-format.md`, and an observer interface lets a UI follow a run live.

The first viewer is a three.js page: the map in 3D, trucks moving along the roads, closed roads in red, the lying road
shown as what it claims, the mesh's messages as pulses between trucks, a timeline to scrub, and the story told tick by
tick beside it.

## Results of the first battery (2026-10-28)

Five arms, 50 seeds each, 250 runs in about 25 seconds, judged from the world alone.

| arm | delivered | wasted trips | lies acted on | second tries on a lying road | stranded | served by tick (average) |
|---|---|---|---|---|---|---|
| A plain loop, ledger | 1,406 / 1,500 | 0 | 262 | 210 | 6 | 24.4 |
| B one design per truck | 1,423 / 1,500 | 0 | 279 | 225 | 6 | 25.9 |
| C no ledger | 1,430 / 1,500 | 290 | 407 | 321 | 6 | 18.1 |
| D ledger, 30% loss | 1,441 / 1,500 | 111 | 405 | 320 | 5 | 26.7 |
| E ledger, a breakdown | 1,429 / 1,500 | 0 | 207 | 150 | 6 | 25.0 |

| | verdict | what it rests on |
|---|---|---|
| M1 | **held** | wasted trips 0 with a ledger, 290 without |
| M2 | **failed** | delivered 1,406 with a ledger, 1,430 without |
| M3 | **failed** | 905 second tries on a lying road over the arms with a ledger |
| M4 | **held** | ledgers identical after the final sync in 50 of 50 runs; wasted trips 111 with loss, 290 without a ledger |
| M5 | **failed** | 15 of 50 runs ended with a village unserved and a working truck idle |
| M6 | **held** | six runs of seed 0 matched in every arm |
| M7 | **failed** | the designs delivered more (1,423 against 1,406) but acted on more lies (279 against 262) |

Why the failures happened, read from the stories of the runs:
- **A stuck truck keeps its claim (M2, M5).** A truck that holds a village's claim and cannot reach it keeps trying
  and keeps talking, so its claim never goes quiet, and the other trucks wait for it until the run ends. The takeover
  rule covers a truck that falls silent, not one that is alive and stuck. Without a ledger, nobody waits for anybody:
  more trips are wasted, but more gets delivered, and sooner.
- **The control layer distrusts a tool, not the road (M3).** It sets a lying tool aside for a cooldown and then
  trusts it again, and each direction of a road is a different tool. A road that lies for the whole run is tried
  again and again.
- **A lying fuel gauge strands a truck (M2, M5).** The truck believes it has fuel and runs dry. Under the designs'
  setting the control layer judges by the registry alone (`JudgeRule.TwoWay`), so its failed drives count as lies,
  and it keeps its claim.
- **The designs did not do better than the plain loop (M7).** They delivered a little more and acted on a little
  more lies: none of them turns "this tool lied" into "this road is flooded".

One bug was found and fixed before these figures: two delivery reports of the same tick were taken in the ledger's
order, which ties on random cell ids, so arm D was not repeatable. The determinism test now compares six runs of a
seed, because two runs agreed by chance.

What would change the failures, for a later version, each to be tested as a new arm rather than folded into these:
- a takeover rule on "no delivery progress for K ticks", not only on silence;
- distrust keyed by what a tool touches (the road), not by the tool's id;
- a truck that hands off its claim when it finds itself stranded.

## The fixes, as new arms (pre-registered 2026-10-28, before the code)

Each fix from the first battery becomes a switch, tested in new arms on the same seeds and the same floods. Arms A
to E stay as they were.

- **Stalled-claim takeover.** A claim whose truck has delivered nothing to its village for 12 ticks (since the claim
  or its last delivery there) may be taken over, as a silent one can. Twelve ticks is enough for the longest round
  trip, depot to the farthest village and back, with a load and a refuel. Delivery reports carry a marker, so a
  truck only driving about does not count as progress.
- **Distrust by road.** An extension, not a change to the control layer: when the layer judges a drive a lie (it
  reported arrival and nothing landed), the truck stops trusting that road in both directions for the rest of the run,
  and tells its planner to avoid it. A drive that failed honestly (out of fuel) is not a lie about the road.
- **Hand off when stranded.** A truck whose drive fails for lack of fuel hands back the claim it holds, on the
  ledger, and claims nothing more.

| arm | as | with |
|---|---|---|
| G | A | stalled-claim takeover |
| H | A | distrust by road |
| I | A | hand off when stranded |
| J | A | all three |
| K | E (a breakdown) | all three |
| L | B (one design per truck) | all three |

| id | claim | falsifier |
|---|---|---|
| N1 | the stalled-claim takeover delivers more than the plain loop (G against A) | as much or less in G |
| N2 | distrust by road ends second tries on a lying road (H) | any second try in H |
| N3 | handing off when stranded delivers more than the plain loop (I against A) | as much or less in I |
| N4 | with all three, a ledger delivers at least as much as no ledger, with fewer wasted trips (J against C) | less delivered in J, or as many wasted trips |
| N5 | with all three, a breakdown never leaves a village unserved while a working truck stands idle (K) | one such run |
| N6 | with all three, the designs deliver at least as much as the plain loop and act on fewer lies (L against J) | less delivered in L, or as many lies or more |

### Results of the second battery (2026-10-28)

All eleven arms over the same 50 seeds, about two and a half minutes. Arms A to E reproduced the first battery's
figures exactly.

| arm | delivered | wasted trips | lies acted on | second tries on a lying road | stranded | served by tick (average) |
|---|---|---|---|---|---|---|
| A plain loop | 1,406 / 1,500 | 0 | 262 | 210 | 6 | 24.4 |
| G stalled-claim takeover | 1,454 / 1,500 | 0 | 211 | 140 | 7 | 27.2 |
| H distrust by road | 1,473 / 1,500 | 0 | 52 | 0 | 6 | 24.0 |
| I hand off when stranded | 1,423 / 1,500 | 0 | 280 | 226 | 6 | 25.4 |
| J all three | 1,500 / 1,500 | 0 | 54 | 0 | 6 | 25.5 |
| K all three, a breakdown | 1,500 / 1,500 | 0 | 59 | 0 | 6 | 26.4 |
| L all three, the designs | 1,500 / 1,500 | 0 | 61 | 0 | 6 | 27.4 |

| | verdict | what it rests on |
|---|---|---|
| N1 | **held** | delivered G 1,454 against A 1,406 |
| N2 | **held** | second tries H 0 against A 210 |
| N3 | **held** | delivered I 1,423 against A 1,406 |
| N4 | **held** | delivered J 1,500 against C 1,430; wasted trips J 0 against C 290 |
| N5 | **held** | 0 of 50 runs with a breakdown left a village unserved while a truck stood idle |
| N6 | **failed** | the designs and the plain loop both delivered 1,500; the designs acted on 61 lies against 54 |

What the second battery says:
- **The three fixes work best together.** Each alone helps a little. Together they deliver every unit in every run,
  breakdowns included, with no wasted trip and no second try on a lying road. The ledger then beats working alone on
  every measure but speed: arm C still finishes sooner (18.1 ticks against 25.5), because nobody waits to claim.
- **Distrust by road does most of the work.** It cuts the lies acted on from 262 to 52 on its own.
- **A takeover spreads a lie before it stops it.** With the stall rule alone (G) or the hand-off alone (I), a village
  changes hands and the new truck tries the same lying road. That is why those arms act on nearly as many lies as
  the plain loop; with distrust by road as well, it stops.
- **The designs still add nothing here (N6).** With the fixes in place they deliver as much as the plain loop and act
  on a few more lies. Whatever the designs do differently, it does not touch roads or claims.
- **The stranded truck remains.** The truck with the lying fuel gauge still runs dry in some runs (6 in every arm).
  The fixes make the convoy survive it, not avoid it. A truck that cross-checks its gauge against the fuel it has
  spent would be the next fix to test.

## A language model in the seat (pre-registered 2026-10-28, before the code)

Arm **S** is arm J (all three fixes) with a language model in every truck's seat instead of the offline planner.
Everything else is the same: the regulator, the control layer, the ledger, the faults, the judge. The model gets the
truck's facts and visible tools, as the planner does, plus a short briefing on how the convoy works (what the facts
mean, how a delivery goes: claim, load, drive, unload, refuel). It answers with one tool per tick. A model that
answers with no tool waits.

The model is chosen by name from Creatura's model catalog (`models.json`), so any catalogued model can take the
seat: local Ollama models for free, DeepSeek, Azure OpenAI, Azure Foundry, or an OpenAI-compatible gateway such as
LiteLLM. `docs/convoy-models.md` says how to set one up. Arm S is not part of the default battery, because it costs
money and time, and it is not repeatable: two runs of a seed can differ, so M6 does not apply to it.

| id | claim | falsifier |
|---|---|---|
| S1 | a model in the seat delivers at least as much as the offline planner on the same seeds (S against J) | less delivered in S |
| S2 | a model in the seat acts on no more lies than the offline planner (S against J) | more lies acted on in S |

Both are judged on the seeds a run covers, which will be few, so a verdict on them is a first reading, not a result.

### First runs with a model (2026-10-28)

A smoke run, not a battery: seed 1, cut to 8 ticks, on the laptop's local Ollama, beside the offline planner on the same
seed and ticks.

| seat | delivered in 8 ticks | answers refused | model time |
|---|---|---|---|
| the offline planner (arm J) | 15 units | none | none |
| `qwen2.5:7b`, calling functions | 5 units | 12 of 24 | 371 s |
| `qwen2.5:7b`, answering in JSON | 0 units | 0 of 24 | 699 s |

- **A small local model does not drive well yet.** Calling functions, half its answers named no listed action: empty
  replies, the action written as text instead of called, or an action that does not exist. Answering in JSON, every
  answer was valid, and none of them got a truck anywhere useful.
- **The architecture held.** Every refused answer was refused before it ran, and its truck waited. No truck did
  anything the world did not allow.
- **It is slow.** About 15 to 30 seconds a call, so a full run on the owner's laptop would take over an hour.
- No hosted model has run yet, and S1 and S2 have no verdict: that needs arm S on a few seeds with a stronger model,
  at about $0.09 a run with `deepseek-chat` (docs/convoy-models.md).

The gap between a valid answer and a good one is what training would close: see docs/design-trained-models.md.

## What this note does not cover

- Physics, continuous space, or real-time simulation. The world is a road graph that steps one tick at a time.
- Real vehicles or real sensors.
- People in the loop, such as a dispatcher.
- Running the scenario across real machines through the hub (D7, later).
- A visual replay. The plain-text replay comes first, and a picture of a run is worth adding once there are runs worth
  watching.
