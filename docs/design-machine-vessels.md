# Design: machine vessels acting together

Status: proposed, 2026-10-28. Nothing here is built.

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
with no progress or heartbeat for K ticks may be taken over.

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
| E | the plain loop | shared | perfect, and one truck breaks down at tick 20 |

Each arm runs over the same 50 seeds.

## Decisions for the owner

| | decision | options | recommendation |
|---|---|---|---|
| D1 | the scenario | a convoy after a flood; warehouse robots; farm machines | **the convoy**: it continues the flood-relief world and its lies are easy to read |
| D2 | the size | 3 trucks, 4 villages, ~12 roads; bigger | **3, 4 and ~12** first; bigger only after the hypotheses have verdicts |
| D3 | who decides for each truck | offline planners only; real models in some seats | **offline planners** for every gated run. Model seats are a separate, optional arm, because they cost money per tick |
| D4 | where the machine's description lives | a `Machine` half on the vessel; a separate record on the world | **on the vessel**, beside the model half, so a regulator can read its own machine as it reads its own model |
| D5 | taking over a quiet claim | never; after K ticks without progress or heartbeat | **after K = 5 ticks**, as a rule of the coordination extension, written to the ledger as a handoff |
| D6 | what counts as success | delivered against demand, deliveries made twice, trucks stranded, lies believed, ticks | **all five**, with delivered judged only by the world |
| D7 | where it runs | the in-memory mesh only; also across the laptop and the second machine through the hub | **in memory** first; the hub version later, as its own step |

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
5. A plain-text replay of a run: each tick, where each truck is, what it believed, what was true.

## What this note does not cover

- Physics, continuous space, or real-time simulation. The world is a road graph that steps one tick at a time.
- Real vehicles or real sensors.
- People in the loop, such as a dispatcher.
- Running the scenario across real machines through the hub (D7, later).
- A visual replay. The plain-text replay comes first, and a picture of a run is worth adding once there are runs worth
  watching.
