# Design: trained models for a world

Status: decided 2026-10-28; step 1 (the recorder) built; step 2 (the policy network) built: T2 and T4 held; step 3 (training by trial) built: the first attempt failed T3; the second held T7, T7b and T7c. Step 5 (the world model and the learned judge) built: T9 failed by three lies. Step 6 (a noisy world) built: T10 held; T10b failed, so the lie judge was not run; T10f failed, as predicted. Its second attempt (sealed noise, memory, loop data) registered, not built.

## The question, in the owner's words

> Can we train a specialised model for the use case, like a JEPA type of model for an X use case? The use case will
> depend on the world, I guess. Also, does it have to be a language model? Maybe there is another type of model that
> can be trained and later reused on this architecture.

And, before it: is the data the agents accumulate useful for creating model adapters?

## The answer, short

Yes to both, and no, it does not have to be a language model. Creatura's seat is `IPolicy`: anything that reads the
situation (facts, visible tools, notes) and returns one visible action. An offline planner fills it today and a
language model fills it in arm S; a trained network can fill it just as well. Beyond the seat, a trained model can
also serve the regulators as a model of the world.

What makes Creatura a good place to train is that every world is already a simulator with a judge the agents cannot
influence. A battery produces about 100,000 decisions in minutes, each with the world's verdict on it, and runs can be
replayed seed by seed. That is unlimited, labelled, free data, and a place to train by trial as well as by example.

## What exists today

- **The seat** (`IPolicy`) and its two fillings: `ConvoyPolicy` (the offline planner) and `ModelPolicy` with
  `ConvoyModelSeat` (a catalogued language model, arm S).
- **The fine-tune seam**: `ExampleRecorder` in `Creatura.Bench`, an observer-only extension that writes one `Example`
  per judged action (the rendered context, the action, the control layer's verdict) and the run's outcome as the
  oracle read it, to an `IExampleSink` (`JsonlExampleSink` writes JSON lines). Its own summary says it plainly:
  nothing consumes what it writes; no trainer, no export, no model reads it yet. The convoy does not attach it.
- **The call log** (`CallLog`, `--calls`): with a model in the seat, the exact prompt and answer of every call, its
  validity, tokens and timings.
- **The labels**: every action's verdict from the world (landed, lied, honest failure) and every run's measures
  (delivered, wasted trips, lies acted on, stranded trucks).
- **The first model runs** (arm S, `qwen2.5:7b` on Ollama, 2026-10-28, seed 1 cut to 8 ticks): calling functions,
  half its answers were refused as not being a listed action, and its trucks delivered 5 units where the planner
  delivered 15. Answering in JSON, every answer was valid and none was useful: 0 units. A raw small model does not
  drive well; training is what would change that.

## The parts

### 1. The recorder, attached and complete

Everything else depends on this, so it comes first.

- **Attach `ExampleRecorder` to the convoy** behind a switch (`--examples <file>`), on every truck, in any arm. It
  observes only: a run with the recorder replays byte for byte as one without it (a test says so).
- **Record structure, not only text.** `Example.Context` is the `TickContext` rendered as text, which suits a
  language model. A network needs numbers. A **feature encoder** per world (`ConvoyFeatures`) turns a truck's facts into
  a fixed-length vector, and the world's **action vocabulary** gives every possible action an index; the visible
  actions are a mask over it. Each example carries the text, the vector, the mask and the chosen index.
- **Record transitions** for a world model: the facts before, the action, the facts after, and the verdict.
- **Record the seat's exact prompt** when a model is in the seat. That needs `ModelPolicy`'s prompt builder exposed
  (it is private today), so the export can render any decision, the planner's included, in exactly the prompt a model
  seat will see.
- **Keep seeds apart.** Training data comes only from seeds 100 to 9,999. Seeds 0 to 49, the battery's, are never
  trained on, so every verdict below is on runs the model has not seen.

An **exporter** turns recorded runs into three training sets: features to action (for the network), prompt to answer
in chat format (for a language model adapter), and transitions (for the world model). Filters choose what to learn
from: only runs that served every village, only actions that landed, or pairs of a good and a bad choice from the same
situation.

### 2. A small policy network in the seat

Not a language model. A network that reads the feature vector and scores every action in the vocabulary, with the
actions that are not visible masked out, and plays the best visible one.

- **First by example** (behaviour cloning): it learns to choose what the offline planner of arm J chose.
- **Then by trial** (reinforcement learning in the simulator): the reward is the judge's own reading. Units delivered
  count for; lies acted on, wasted trips and stranded trucks count against. This is where it can learn what the planner
  cannot, such as when to doubt a sensor.
- **In C#**: a `NetworkSeat : IPolicy` in `Creatura.Seats`, package-free, that loads the weights and does the
  arithmetic. It is tiny, takes microseconds per decision, costs nothing, and is deterministic, so a seed still replays
  byte for byte and the batteries stay fast.
- **Its limit**: it knows the world it was trained on. A network built over the road graph, a graph network, could
  carry over to other maps of the same world; a different world needs a model of its own.

### 3. A language model adapter

A small open model (Qwen 2.5 3B or 7B) with a LoRA adapter trained on the planner's decisions, written in exactly the
prompt the model seat sees. It addresses what the first runs showed: the raw model often does not answer with a listed
action. It keeps the text interface, so `ConvoyModelSeat` and `ModelPolicy` work unchanged, and it can carry over to new
worlds better than a network. It is slower, not repeatable, and needs a machine to run it. This follows the pattern of
the monkey-loop pilot: a teacher's decisions, then an adapter trained on the laptop.

### 4. A learned world model

This one decides nothing. It predicts what the world will look like after an action: given the facts and an action,
the facts that should follow. JEPA's idea is to predict in a learned representation rather than raw observations; for
Creatura's symbolic worlds a forward model over the facts is the first step, and a learned representation the second.

What it gives the framework:
- **An expectation for every action.** A lie becomes a measured surprise: the world reported arrival, the model
  predicted arrival was impossible on a flooded road. This is Peterson's anomaly and Bach's incoherence, learned instead
  of hand-coded, fed to the regulators through their prediction slot.
- **Imagination.** A seat can try actions in the model before taking them in the world.
- **Free data.** Every run is a stream of (facts, action, next facts) records.

### 5. A catalog of trained models

Trained models are tied to a world family, so they are catalogued with it, the way `models.json` catalogues language
models. An entry names the model, its kind (network, adapter or world model), the world family it was trained for
(convoy, on any map with this action vocabulary), the hash of its feature schema, the seeds it was trained on, and
where its weights are. A seat refuses a model whose world family or schema does not match the world it is put in. This
is what makes a trained model reusable: any truck in a convoy world can load one into its seat.

## Decisions for the owner

| | decision | options | recommendation |
|---|---|---|---|
| T-D1 | what to train first | the policy network; the adapter; the world model | **the policy network**: cheapest, fastest, repeatable, and testable as an arm against J the way the fixes were |
| T-D2 | where training runs | Python on the laptop (PyTorch or MLX), weights exported to C#; .NET (TorchSharp, ML.NET) | **Python on the laptop**, exporting plain weights a package-free C# seat reads. ONNX later, if a model grows past what plain arithmetic suits |
| T-D3 | the network's shape | a plain network over a fixed feature vector; a graph network over the road map | **the plain network first**, the graph network as the step that tests carrying over to a second map |
| T-D4 | what the network learns from | the planner's decisions only; then trial in the simulator | **both, in that order**, so the gain from trial is measured separately |
| T-D5 | which decisions to learn from | all; only runs that served every village; only actions that landed | **runs of arm J that served every village**, all their actions, with the verdicts kept for a later filter |
| T-D6 | where the weights live | in the repository; as release assets; on the hub as shared files | **the repository** for small networks (kilobytes), **shared files** later for adapters |

Decided by the owner, 2026-10-28: every recommendation (T-D1 the policy network first, T-D2 Python on the laptop with
plain weights read in C#, T-D3 the plain network first, T-D4 by example then by trial, T-D5 arm J's fully served runs,
T-D6 weights in the repository).

How step 1 records, settled while building it:
- **At the seat, joined to an observer.** A recording wrapper around each truck's seat captures exactly what the
  seat was given (facts, visible tools, notes, goal, tick, freedom, history) and what it chose. An observer extension
  adds what the action came to: the control layer's verdict, what the world reported, and the facts afterwards. The
  loop-side `ExampleRecorder` alone cannot see the seat's notes (the roads the truck distrusts), so it is attached as
  well, unchanged, as the generic seam, writing its own examples beside the convoy's richer records.
- **The language-model export uses the JSON protocol** (one object, `{"tool": …, "why": …}`), rendered by the same
  code that builds a model seat's prompt, so a model trained on it sees at training time exactly what it sees in the
  seat.

## Pre-registered hypotheses

All judged on the battery's seeds 0 to 49, never trained on.

| id | claim | falsifier |
|---|---|---|
| T1 | the recorder changes nothing: a run with it replays byte for byte as one without it | any difference |
| T2 | the network trained by example delivers at least 95% of what arm J's planner delivers | less than 95% |
| T3 | trained further by trial, the network delivers at least as much as arm J and acts on fewer lies | less delivered, or as many lies or more |
| T4 | the network replays byte for byte for the same seed | any difference |
| T5 | the adapter-tuned model answers with a listed action at least 95% of the time, and delivers more than the raw model | below 95%, or no more delivered |
| T6 | the world model's surprise at a lie exceeds its surprise at 95% of landed actions | below 95% |

## What gets built, in order

1. The recorder attached to the convoy, the feature encoder, the action vocabulary, transitions, the seat's prompt
   exposed, and the exporter (T1).
2. The policy network: training by example in Python, `NetworkSeat` in C#, and an arm with it (T2, T4).
3. Training by trial in the simulator, and the arm again (T3).
4. The trained-model catalog.
5. The adapter (T5) and the world model (T6), each as its own step.

## Step 1, built (2026-10-28)

**T1 held.** A recorded run replays byte for byte as an unrecorded one, in arms J, C, D and L. Five deliberate breaks
each fail a test:
- recording that nudges a decision (T1 fails in all four arms);
- facts afterwards that are the facts before;
- an export that skips the briefing (the exported prompt no longer matches what a model seat sends);
- the battery's seeds exported;
- the served-runs filter ignored.

What it is, in the code:
- `ConvoyRecorder` (`src/Creatura.Scenarios`): a recording wrapper at each truck's seat, an observer for what each
  action came to, and the generic `ExampleRecorder` beside them, unchanged.
- `ConvoyFeatures`: 61 features for the flooded valley, schema hash `ad24852ae9d8f47a`. `ConvoyActions`: 36 actions.
- `ModelPolicy.RenderPrompt` and `ConvoyModelSeat.Briefed`: the seat's own prompt code, now public. A test checks
  that every exported prompt is exactly the one a model seat sent.
- `ConvoyDatasets.Export`: `policy.jsonl`, `chat.jsonl`, `transitions.jsonl` and `card.json`. It refuses seeds below 50.
- The command: `--record <file>` for any run or battery, and `--dataset <dir>` to record and export in one step.

A first dataset: arm J, seeds 100 to 299, recorded and exported in 18 seconds. All 200 runs served every village and
were kept: 15,423 decisions and as many transitions. On disk: `policy.jsonl` 4.7 MB, `transitions.jsonl` 14 MB,
`chat.jsonl` 69 MB (each line carries the seat's full prompt), and `runs.jsonl` 39 MB (the recording itself, to export
again with other filters). Datasets are not committed: the command makes them again from the code.

```bash
dotnet run --project samples/Convoy -c Release -- --dataset datasets/convoy-j --arms J --seeds 100-599
```

Next is step 2: train the policy network by example from `policy.jsonl`, and put it in the seat.

## Step 2, registered before training (2026-10-28)

Written before the network was trained or its seat was built.

- **The data.** Arm J, seeds 100 to 599, only runs that served every village (T-D5). Split by seed, not by decision:
  seeds 100 to 549 train, seeds 550 to 599 are held out to pick the stopping point and report accuracy. The battery's
  seeds 0 to 49 are not touched until the arm runs.
- **The network.** Its input is the 61 features plus one bit per action in the vocabulary that says whether the
  action is visible this tick (97 inputs). The visible bits are there because the planner's choice depends on them: a
  road the control layer has set aside is not driven. Two hidden layers of 64 with ReLU, 36 outputs, a softmax over the
  visible actions only. Trained by example, cross-entropy against the planner's choice, Adam, a fixed random seed, in
  numpy (`tools/train/train_policy.py`, run with `uv`). No PyTorch: at this size plain numpy is enough, and the
  arithmetic the C# seat repeats is exactly the arithmetic trained.
- **The weights.** Plain JSON, `models/convoy-policy-j.json`: the format, the feature schema hash, the action vocabulary,
  the layers, the seeds trained on, and the held-out accuracy. Kept in the repository (T-D6) and embedded in
  `Creatura.Scenarios` so arm N needs no path.
- **The seat.** `NetworkSeat : IPolicy` in `Creatura.Seats`, package-free: it encodes the situation, runs the layers,
  and plays the visible action with the highest score (the lowest index on a tie). `ConvoyNetworkSeat` gives it the
  convoy's encoder and refuses weights whose schema hash or vocabulary differs from the map's.
- **Arm N.** As J (all three fixes), with the network in every truck's seat. It is cheap and repeatable, so it joins the
  default battery.

Judged on seeds 0 to 49:
- **T2**: arm N delivers at least 95% of what arm J delivers.
- **T4**: arm N replays byte for byte for the same seed (the battery's M6 check covers it, on six runs of one seed).

Reported without a claim: the held-out accuracy, arm N's wasted trips, lies acted on and stranded trucks against J's.
A network that copies the planner well may still drift once one wrong choice puts it in a situation the planner never
reached; if T2 fails, that is the likely reason, and training by trial (step 3) is what addresses it.

## Step 2, built (2026-10-28)

**T2 held, and T4 held.** On the battery's seeds 0 to 49, which it never saw, arm N delivered 1500 of 1500, as arm J
did (100%). Wasted trips: 0 against 0. Lies acted on: 54 against 54. Stranded trucks: 7 against 6. Six runs of seed 0
replayed byte for byte, and so did seed 7 in the tests. The rest of the battery is unchanged.

How close the copy is: arms N and J made the same decision at every tick of every truck in 48 of the 50 seeds. In the
other two, the network chose once where the planner would not have: in seed 4 Truck 1 reported done at tick 27 where
the planner waited, and in seed 27 Truck 2 took road r02 instead of r01 at tick 13. Neither cost a delivery; the extra
stranded truck comes from one of them.

The numbers on the way:
- The dataset: arm J, seeds 100 to 599, all 500 runs served every village, 38,772 decisions. Seeds 550 to 599 (3,897
  decisions) were held out.
- Training: 24 seconds on the laptop, numpy only, kept at epoch 42. The network chose the planner's action on 99.98% of
  the training decisions and 99.97% of the held-out ones, with its weights rounded to six digits as they are written.
- The weights: `models/convoy-policy-j.json`, 123 KB, 97 → 64 → 64 → 36, embedded in `Creatura.Scenarios`.
- The C# seat agrees with the planner on 99% or more of the decisions of held-out seeds 560 to 563 (a test). If its
  arithmetic differed from the trainer's, that agreement would collapse.

Five deliberate breaks, each failing a test:
- the mask ignored, so a hidden action can be played;
- no ReLU;
- the visible bits not given to the network;
- the feature schema not checked;
- a tie going to the highest index instead of the lowest.

What it shows, and what it does not:
- The planner is a pure function of what the seat is given each tick (it keeps no state between ticks), so a small
  network can copy it almost exactly. Whatever the trucks remember lives in the world, the ledger, the control layer's
  quarantine and the distrust notes, all of which the network reads as inputs.
- A copy is as good as its teacher and no better. Arm N acts on as many lies as J (54), because J's planner believes its
  sensors and the network learned that too. Doing better than the teacher is step 3's question (T3), by trial in the
  simulator.
- The network knows the flooded valley only. Its schema hash ties it to this map, and the seat refuses any other.

```bash
dotnet run --project samples/Convoy -c Release -- --dataset datasets/convoy-j --arms J --seeds 100-599
uv run tools/train/train_policy.py datasets/convoy-j models/convoy-policy-j.json --held-out-from 550
dotnet run --project samples/Convoy -c Release -- --battery --seeds 50 --arms J,N
```

## Step 3, registered before training (2026-10-28)

Written before the sampling seat, the trial trainer or any trial run existed.

**What trial can reach, read from the training data first.** In arm J's 500 training runs (seeds 100 to 599) the
trucks acted on 490 road lies. 384 were the first time any truck tried that road. A truck cannot avoid those: the road
reports itself open, as every open road does, and nothing in its view tells the lying one apart. The other 106 came
after another truck had already caught the same road, but a truck's view does not include another truck's
distrust (distrust is per truck), so no seat with these inputs can avoid them either. What remains is the other kind
of lie the judge counts: a truck stranded because its fuel gauge reads three units high (35 strandings in those 500
runs). That one is within a seat's reach: keep a margin of fuel. So training by trial can lower the lies only by
lowering the strandings, and the prediction is that it will do that and nothing more.

- **Arm R.** As N, with the network trained further by trial: `models/convoy-policy-j-trial.json`, embedded like N's.
- **The start.** Arm N's network, trained by example. Trial changes it from there.
- **The trial runs.** The network plays arm N's setup with its choices sampled (scores divided by a temperature of 2,
  so it tries what it would not otherwise), on seeds 1,000 to 8,999. Seeds 9,000 to 9,199 are kept apart to choose
  which trained network to keep. The battery's seeds 0 to 49 are not touched until arm R runs.
- **The reward**, per truck and per decision, from the judge's own reading: +1 per unit delivered, -5 for an action the
  control layer judged a lie, -20 when the truck becomes stranded, and -0.02 per tick it is still working.
- **The method.** PPO in numpy (a clipped policy update, a separate value network, advantages with λ = 0.95 and
  γ = 0.99, a small entropy bonus): 64 runs per round, at most 150 rounds. The sampling happens in C#, in the same
  `NetworkSeat`, with a random generator seeded from the run's seed, so a round can be run again exactly.
- **Which network is kept.** Every 10 rounds the network plays seeds 9,000 to 9,199 greedily (no sampling), and the
  one with the best total reward there is kept. Arm N's network is scored the same way first, as round 0; if no round
  beats it, that is the finding.

Judged on seeds 0 to 49:
- **T3** (as registered above): arm R delivers at least as much as arm J and acts on fewer lies.
- **T3b**: arm R strands fewer trucks than arm J. This is the mechanism the prediction names; if T3 holds without it,
  the prediction was wrong.
- **T4 again**: arm R replays byte for byte for the same seed.

Reported without a claim: the road lies and the strandings apart, wasted trips, and when the villages were served
(a fuel margin costs refuelling trips, so arm R may serve later).

## Step 3, built (2026-10-28)

**T3 failed. T3b held, and T4 held for arm R.** On the battery's seeds 0 to 49:

| | arm R (trial) | arm J (planner) | arm N (copy) |
|---|---|---|---|
| delivered | 1445/1500 | 1500/1500 | 1500/1500 |
| runs that served every village | 37 of 50 | 50 of 50 | 50 of 50 |
| lies acted on | 46 | 54 | 54 |
| of them road lies | 44 | 48 | |
| of them strandings by the lying gauge | 2 | 6 | |
| stranded trucks | 2 | 6 | 7 |
| villages served by tick (unserved counts 61) | 36.0 | 25.5 | 25.3 |

Arm R acts on fewer lies, but delivers less, so T3, which asks for both, failed. T3b, the mechanism the prediction named,
held: strandings fell from 6 to 2. Six runs of seed 0 replayed byte for byte.

**What trial taught the network: caution.** Over the same 50 seeds, arm R's trucks refuelled 427 times against J's
189, waited 2,420 ticks against 1,072, and drove 1,371 roads against 1,448. Claims rose to 494 from 366, so trucks
claimed and let go more often. Keeping the tank fuller is what stopped the gauge strandings. Waiting and refuelling
cost the time it needed to finish: in 13 runs the 60 ticks ran out with a village still short.

**The prediction was partly wrong.** It said trial could lower the lies only through the strandings. Road lies fell
too, 48 to 44. The likely reason is not that the network learned to tell the lying road apart (nothing in its inputs
does that), but that it drove 5% fewer roads and so came to the lying one less often. Fewer lies by doing less is not
the improvement the step was after.

**Why it failed, as far as the training shows.** The reward priced one undelivered unit at 1 and one stranding at 20.
Under that price list, giving up a few units to avoid a stranding is a good trade, and trial found it. The selection
rule chose by the same reward, so it kept that trade too: round 110, with 5,740 units delivered on the selection seeds
against the start's 5,998, but 1 stranding against 23 and 152 lies against 198. Every round after round 10 delivered
less than the start. In short, the training did what the reward asked; the reward did not ask for what T3 asks.

The numbers on the way:
- The gradient was checked against finite differences before the run: the largest difference was 1.6e-10.
- 150 rounds of 64 sampled runs on seeds 1,000 to 8,999 took 26 minutes on the laptop. Round 110 was kept.
- The weights: `models/convoy-policy-j-trial.json`, embedded beside arm N's.
- Four deliberate breaks of the sampling and scoring code each fail a test: sampling a hidden action, the sample seed
  unused, T3b accepting as many strandings, and the temperature ignored. The last one at first passed: the sampling
  test drew only at temperature 1. A draw at temperature 2 was added, and the break now fails.

What a next attempt would change, each to be registered before it runs:
- **Price an unserved unit at the end of the run**, so that a village left short costs more than a stranding saves.
- **Keep only networks that deliver at least what the start delivers** on the selection seeds, and choose among them
  by reward.
- **Hold the network near its teacher** (a penalty for drifting from arm N's choices), so trial changes only what it
  has a reason to change.
- **Share distrust through the ledger**, a change to the design rather than to training. 106 of the 490 road lies in
  the training data came after another truck had already caught that road. No seat can avoid them today, because a
  truck's view does not include another truck's distrust. Built since, as arm W (docs/design-machine-vessels.md, "Shared
  distrust"): on seeds 0 to 49 it took those lies from 9 to 0.

```bash
dotnet build samples/Convoy -c Release
uv run tools/train/train_trial.py models/convoy-policy-j.json models/convoy-policy-j-trial.json
dotnet run --project samples/Convoy -c Release -- --battery --seeds 50 --arms J,N,R
```

## Step 3, second attempt, registered before training (2026-10-28)

Written before the new reward, the anchor or the selection rule existed in the trainer, and before any trial run of
this attempt. It applies the three changes the first attempt's failure pointed to, on the setup with shared distrust.

- **The setup: arm W's** (all three fixes, distrust shared through the ledger), so the trucks that train are the ones
  the comparison is against. With warnings shared, no lie on an already-caught road is left to remove, so the only lies
  a seat can still avoid are the strandings caused by the lying fuel gauge (arm W: 6 over seeds 0 to 49).
- **The start:** arm N's network, trained by example on arm J. It reads the same features; the warnings reach it as the
  same `avoid:` notes.
- **The reward**, as before (+1 per unit delivered, -5 per action judged a lie, -20 when stranded, -0.02 per tick), plus
  **-5 per unit still needed at the end of the run, on each truck's last decision**. A run left 4 units short now costs
  60, three times what a stranding costs.
- **The anchor:** each update also pulls the network toward the start, with a penalty of 0.2 times the divergence
  (KL) between the start's choices and the network's, both at the sampling temperature, on the states of the round.
- **Which network is kept:** every 10 rounds, as before, on seeds 9,000 to 9,199 without sampling. Only a network that
  delivers at least as much there as the start does may be kept; among those, the best total reward. If no round
  qualifies, the start is kept, and that is the finding.
- Unchanged: PPO, 64 runs a round, 150 rounds, temperature 2, trial seeds 1,000 to 8,999.
- **Arm Q:** as W, with the network this attempt keeps (`models/convoy-policy-w-trial.json`).

Judged on seeds 0 to 49:

| id | claim | falsifier |
|---|---|---|
| T7 | trained by trial on W's setup, the network delivers at least as much as arm W and acts on fewer lies | less delivered, or as many lies or more |
| T7b | it strands fewer trucks than arm W | as many or more |
| T7c | arm Q replays byte for byte for the same seed | any difference |

The prediction: if T7 holds, it holds through fewer strandings (T7b), with the road lies at W's level, and the villages
served somewhat later, since a fuller tank costs refuelling trips.

## Step 3, second attempt, built (2026-10-28)

**T7, T7b and T7c held.** On the battery's seeds 0 to 49, which training never touched:

| | arm Q (second attempt) | arm W (planner, shared distrust) | arm R (first attempt) |
|---|---|---|---|
| delivered | 1500/1500 | 1500/1500 | 1445/1500 |
| lies acted on | 41 | 45 | 46 |
| of them road lies | 39 | 39 | 44 |
| of them strandings by the lying gauge | 2 | 6 | 2 |
| stranded trucks | 2 | 6 | 2 |
| villages served by tick | 24.8 | 25.4 | 27.2 |

Arm Q delivered everything, as W did, and stranded 2 trucks where W stranded 6: that is the whole of the drop in lies,
since its road lies are W's 39. Six runs of seed 0 replayed byte for byte.

The prediction held in two parts and not in the third. The gain came through fewer strandings, and the road lies stayed
at W's level, as predicted. But the villages were served sooner (24.8 against 25.4), not later: the network did not
pay for its margin with refuelling trips.

**What trial taught it this time: a targeted change, not caution.** Over the same 50 seeds arm Q refuelled 193 times
against W's 189 and waited 1,006 ticks against 1,069. In 36 of the 50 seeds it made exactly W's choices at every tick of
every truck. The first attempt doubled the refuels and the waits; this one changed a few decisions in 14 seeds.

How it got there:
- **The start was worse than the planner on this setup.** On the selection seeds 9,000 to 9,199, arm N's network
  (copied from J's planner) delivered 5,963 of 6,000 and stranded 41 trucks, where arm W's planner delivered 6,000 and
  stranded 16. Shared warnings put trucks in situations J's planner never reached, so a copy of it had never learned
  them. Trial closed that gap first. By round 20 it delivered all 6,000 with 18 strandings, and by round 60 it stranded
  12, fewer than the planner.
- **The kept network is round 150**, the last: 5,998 delivered on the selection seeds, 148 lies, 11 stranded. Every
  round checked after round 10 delivered at least the start's 5,963, so the floor kept none out. The reward that
  priced a village left short did most of the work.
- 23 minutes on the laptop. The anchor's gradient was checked against finite differences first (largest difference
  9e-12), and the trainer ran a two-round smoke test.
- The weights: `models/convoy-policy-w-trial.json`, embedded with the others.

What the margin is worth: 4 strandings fewer over 50 seeds, on one battery. It is the effect the reward aimed at and
it agrees with the selection seeds (11 against 16), but a larger battery would say more than this one can.

The breaks: the T7b scoring was checked by letting it accept a tie, and its test failed. The trainer's new parts (the
end-of-run price, the anchor, the floor) were not broken deliberately, since each break would need a training run to
show; the anchor's gradient check and the smoke test are what checked them.

```bash
uv run tools/train/train_trial.py models/convoy-policy-j.json models/convoy-policy-w-trial.json \
    --arm W --end-penalty 5 --anchor 0.2 --keep-at-least-start --work datasets/trial-w
dotnet run --project samples/Convoy -c Release -- --battery --seeds 50 --arms W,Q
```

## Step 5, the world model, registered before training (2026-10-28)

Written before the exporter, the trainer or any world model existed.

- **What it predicts.** Given what a truck sees before an action (the 61 features) and the action (one of the 36),
  the features it will see after: where it is, its fuel and load, the villages' needs, the claims, the roads. A small
  network, 97 inputs (the features and the action as one bit each) to 61 outputs between 0 and 1, trained by example
  with cross-entropy against what was observed. It decides nothing.
- **Surprise.** For an action that ran, the surprise is the cross-entropy between what the model predicted and what
  was observed after, summed over the 61 features. A lie is a drive the world reported as arrived while the truck did
  not move: the model, which has seen thousands of drives arrive, should find "still here" surprising.
- **The data.** Training: arm J's transitions from seeds 100 to 549; seeds 550 to 599 held out to choose the stopping
  point. Judging: arm J's transitions on the battery's seeds 0 to 49, recorded fresh, and DeepSeek's transitions on seeds
  0 to 4 from the arm S run. Those were recorded on the battery's seeds, so by the rule they are never trained on: they
  serve here only as a test of a driver the model never saw. DeepSeek runs on seeds from 50 up could be training data
  later.

Judged on those seeds:

| id | claim | falsifier |
|---|---|---|
| T6 | the median surprise at a lie exceeds the surprise at 95% of the actions that landed (arm J, seeds 0 to 49) | the median at or below that 95th percentile |
| T6b | the same holds for DeepSeek's transitions (seeds 0 to 4), a driver whose choices the model never saw | as for T6 |

Reported without a claim: how often the model predicts the next features exactly (rounded) for landed actions, the
share of lies above the 95th percentile (the lies it would catch for a 5% false-alarm rate), and the surprise by kind
of action.

The prediction, and its limit: T6 should hold, because a lie breaks the most regular pattern in the data (a drive on
a road the sensor calls open arrives). That makes the learned model a rediscovery of what the control layer's effect
check does by construction: it measures, after the act, that what was reported did not happen. It cannot tell the
lying road apart before the truck tries it, since nothing in the features does (step 3 found the same). What it adds
over the hand-coded check is a graded expectation that a regulator's prediction slot can read, learned from data
rather than written for one world.

## Step 5, the world model, built (2026-10-28)

**T6 held. T6b failed.**

| set | transitions | lies | landed, 95th percentile of surprise | lies, median surprise | lies above that percentile | landed predicted exactly |
|---|---|---|---|---|---|---|
| arm J, seeds 0 to 49 (the planner) | 3,981 | 54 | 4.25 | **9.48** | **100%** | 63% |
| DeepSeek, seeds 0 to 4 (arm S, both passes) | 1,242 | 89 | 14.54 | **11.24** | 38% | 9% |

On the planner's own driving, every lie stood out: all 54 were more surprising than 95% of the actions that landed,
with a median surprise more than twice that line. The model rediscovered from data alone what the control layer's
effect check does by construction, as predicted.

On DeepSeek's driving it did not, for two reasons the breakdown shows:
- **A driver it never saw.** Trained only on the planner's transitions, it found DeepSeek's ordinary actions
  surprising: it predicted only 9% of them exactly, against 63% of the planner's, and the 95th percentile of
  "normal" rose from 4.25 to 14.54. Waits and claims in situations the planner never reaches were the most surprising
  (median 5.7 and 5.5, against 3.6 and 1.5 for the planner).
- **Not every lie is the world's.** 29 of DeepSeek's 89 lies were its own actions that did not land: 23 claims, 4
  unloads and 2 early reports of done, which the two-way judge counts as lies. The model's surprise at them was
  moderate (medians 8.0, 7.3 and 11.2). Its 60 drive lies, the lying road itself, had a median of 14.1, just under
  the line.

What it shows: a world model is only as good as the variety of driving it learned from. This is the concrete use of
other drivers' runs as training material, the question that led here. The DeepSeek runs sit on the battery's seeds,
so they stay a test set; the next step is to train on varied driving from training seeds: the sampled trial runs of
step 3 (free), other arms' planners, or DeepSeek runs on seeds from 50 up (about $0.075 each). Then T6b can be judged
again on the same DeepSeek set.

The numbers on the way:
- The model: 97 → 128 → 128 → 61, sigmoid outputs, trained in 9 seconds on 34,875 transitions (seeds 100 to 549),
  kept at epoch 53 by the held-out surprise on seeds 550 to 599. The weights are `models/convoy-world-j.json`
  (355 KB), not embedded: nothing reads them in C# yet.
- The surprise includes the spread of the features that are fractions (fuel, load, time), which no prediction can
  remove; that sets the floor of about 3 for an ordinary action.
- A deliberate break, scoring surprise against the features before the action instead of after, makes T6 fail
  (the landed actions' 95th percentile rises to 27.0): the measure depends on what was observed, as it should.
- The exporter refuses the battery's seeds unless a set is marked for evaluation, and a test checks it.

```bash
dotnet run --project samples/Convoy -c Release -- --world datasets/convoy-j/runs.jsonl datasets/world/train.jsonl
dotnet run --project samples/Convoy -c Release -- --world datasets/world/j-battery-runs.jsonl datasets/world/eval-j.jsonl --evaluation
uv run tools/train/train_world.py datasets/world/train.jsonl models/convoy-world-j.json \
    --judge datasets/world/eval-j.jsonl datasets/world/eval-deepseek.jsonl
```

## Step 5, varied driving, registered before training (2026-10-28)

Written before the new data was recorded or the model retrained.

- **The data, all free and all from training seeds.** What the first model learned from (arm J's planner, seeds 100
  to 599), plus:
  - the planner in ten other setups, arms A, B, C, D, E, G, H, I, K and L, on seeds 100 to 199 each: no ledger, a
    lossy radio, a breakdown, the designs, the fixes one at a time;
  - the policy network of arm N with its choices sampled at a temperature of 2, in arms N and W, on seeds 1,000 to
    1,099 each: a driver that makes odd choices, claims what is held, reports done too early, as DeepSeek did.
- **Held out:** every transition from a seed ending in 9, to choose the stopping point.
- **Unchanged:** the model's shape and training, the surprise, and the two test sets (arm J on seeds 0 to 49, DeepSeek
  on seeds 0 to 4).

Judged on those test sets:

| id | claim | falsifier |
|---|---|---|
| T6c | trained on varied driving, the world model passes T6b: on DeepSeek's driving the median surprise at a lie exceeds the 95th percentile at landed actions | the median at or below it |
| T6d | it still passes T6 on the planner's driving | the median at or below it |

Reported without a claim: the share of DeepSeek's landed actions predicted exactly (9% before), its drive lies apart
from its own failed actions, and the share of lies above the line on both sets.

## Step 5, varied driving, built (2026-10-28)

**T6c held, and T6d held.** Trained on varied driving, the world model passes on DeepSeek's driving, which it still
never saw, and keeps passing on the planner's.

| test set | model | landed, 95th percentile | lies, median | lies above the line | road lies above the line | landed predicted exactly |
|---|---|---|---|---|---|---|
| DeepSeek, seeds 0 to 4 | planner only | 14.54 | 11.24 | 38% | 50% | 9% |
| DeepSeek, seeds 0 to 4 | **varied** | **6.12** | **7.29** | **63%** | **80%** | 13% |
| arm J, seeds 0 to 49 | planner only | 4.25 | 9.48 | 100% | 100% | 63% |
| arm J, seeds 0 to 49 | **varied** | 4.29 | 9.51 | **100%** | 100% | 52% |

What changed is the line. A model that has seen many ways of driving no longer finds an unfamiliar driver's ordinary
actions surprising: the 95th percentile of DeepSeek's landed actions fell from 14.5 to 6.1, while its road lies stayed
well above it. Four in five of the lying road's drives now stand out, against one in two.

What did not: DeepSeek's failed claims (23 of its 89 lies) stay below the line (22% above it, median 4.5). The varied
data taught the model that a driver sometimes claims a village already held, which is a driver's mistake and not the
world lying, so it is less surprised by it. That is arguably right, and it is why the claims are reported apart.

The price: on the planner's own driving the model predicts fewer landed actions exactly (52% against 63%). Spread over
more kinds of driving, it is less sharp on any one, and still flags every lie there.

The data, all free and all from training seeds: 158,874 transitions. Arm J's 38,772, the planner in ten other setups
(arms A to L but J, seeds 100 to 199: 101,351), and arm N's network sampled at a temperature of 2 in arms N and W
(seeds 1,000 to 1,099: 18,751). Trained in 26 seconds, kept at epoch 41; the weights are
`models/convoy-world-varied.json` (353 KB), not embedded.

```bash
dotnet run --project samples/Convoy -c Release -- --rollouts datasets/world/planners-runs.jsonl --arms A,B,C,D,E,G,H,I,K,L --seeds 100-199
dotnet run --project samples/Convoy -c Release -- --rollouts datasets/world/sampled-runs.jsonl --arms N,W --seeds 1000-1099 \
    --network models/convoy-policy-j.json --temperature 2 --sample-seed 7
# --world each recording, concatenate with train.jsonl into train-varied.jsonl, then:
uv run tools/train/train_world.py datasets/world/train-varied.jsonl models/convoy-world-varied.json --held-out-digit 9 \
    --judge datasets/world/eval-j.jsonl datasets/world/eval-deepseek.jsonl --results datasets/world/results-varied.json
```

Next for the world model: read its surprise in a regulator's prediction slot, so a truck that finds an outcome
surprising can act on it the way the designs act on an anomaly.

## Step 5, the world model in the loop, registered before the code (2026-10-28)

Written before the C# world model, the extension or arm X existed. The threshold below was fixed from training seeds
only; the battery's seeds were not looked at.

- **Where surprise enters.** The Core has no slot named "prediction" that a learned model could fill; what it has is
  the control layer's re-judge chain. After the layer's effect check, each extension may replace the verdict on the
  action, and the replaced verdict is the one the quarantine, the regulator's memory (`Record`), the distrust
  extension and every observer see. A `LearnedJudgeExtension` puts the world model there.
- **What it does.** Before each decision it encodes what the truck sees; after the action it encodes what the truck
  sees then, and asks the world model how surprising the change was. Above the threshold the action is judged a lie;
  at or below it, landed. It overrides the layer's own verdict entirely, so in this arm the hand-coded effect check
  decides nothing.
- **The model and the threshold.** `models/convoy-world-j.json`, the model trained on arm J's planner (its truck
  drives like the planner). Threshold **4.606**: the 99.5th percentile of its surprise at landed actions on its held-out
  training seeds 550 to 599, where it flagged 0.52% of landed actions and caught all 50 lies.
- **Arm X:** arm J with the learned judge in every truck. The rest of the battery is unchanged.

Judged on seeds 0 to 49:

| id | claim | falsifier |
|---|---|---|
| T8 | with the learned judge in place of the hand-coded check, the trucks deliver at least as much as arm J and act on no more lies | less delivered, or more lies acted on |
| T8b | the learned judge gives false alarms (an action that landed, judged a lie) on fewer than 1% of the actions it judges | 1% or more |
| T8c | arm X replays byte for byte for the same seed | any difference |

Reported without a claim: lies the learned judge missed, its false alarms by kind of action, and when the villages
were served. The prediction: it catches nearly every lie; a few false alarms cost little, unless one falls on a drive,
which makes the truck distrust a good road for the rest of the run.

## Step 5, the world model in the loop, built (2026-10-28)

**T8 failed, T8b failed, T8c held.** On seeds 0 to 49:

| | arm X (learned judge) | arm J (hand-coded check) |
|---|---|---|
| delivered | 1500/1500 | 1500/1500 |
| lies acted on | 63 | 54 |
| of them road lies | 56 | 48 |
| trucks stranded | 8 | 6 |
| villages served by tick | 27.7 | 25.5 |
| lies the judge missed | **0** | (the check misses none by construction) |
| false alarms | **131 of 4,197 judged (3.1%)** | none |

The learned judge caught every lie: not one lying drive was judged landed, and no truck took a lying road twice. It
also called 131 actions lies that had landed, six times the 0.52% its calibration had shown. Arm X still delivered
everything, but acted on more lies and served the villages two ticks later.

**Why: the judge changed the driving it judged.** The threshold was fixed on arm J's own driving. In arm X, 34 of the
false alarms fell on drives that had in fact arrived, and road distrust then made the truck avoid a good road for the
rest of the run. The detours took trucks into situations the planner never reaches, which the model, trained only on
the planner's driving, found more surprising still: 62 false alarms fell on waits, the rest on refuels, loads, unloads,
claims and early reports of done, and all of them after tick 10. The detours also sent more trucks onto the lying road
for the first time (56 road lies against 48). A fixed threshold held on the driving it was set on and not on the
driving it caused.

What would change it, each to be registered before it runs:
- **Judge only what the world can contradict.** A wait, a claim or a report changes nothing the model can check
  against the world; leave those to the hand-coded check, and judge drives, loads, unloads and refuels.
- **Let a learned lie quarantine, not condemn.** A road distrusted for good on one surprise is the costliest mistake;
  the layer's three-tick quarantine is enough until the hand-coded check, or a second surprise, agrees.
- **Train on varied driving, and calibrate on it.** The varied model of the last step held up on a driver it never
  saw; calibrated on varied driving, the threshold would allow for the detours its own alarms cause.

T8c held: the world model's arithmetic is deterministic, and six runs of seed 0 replayed byte for byte.

The code: `WorldModel` (`Creatura.Seats`) reads a model from `train_world.py` and repeats its arithmetic exactly (a
test pins one surprise to the Python value, 234.71997016536898); `LearnedJudgeExtension` and arm X are in
`Creatura.Scenarios`, with `ConvoyMeasures.Judged`, `JudgeFalseAlarms` and `JudgeMisses`. Four deliberate breaks each
fail a test: surprise against what the truck saw before (95 false alarms on one seed: this one passed until a test
counted false alarms), no sigmoid on the output, the learned verdict ignored, and T8b accepting 1%.

## Step 5, the learned judge's second attempt, registered before the code (2026-10-28)

Written before arms Y and Z existed. Every threshold below was fixed on training seeds; the battery's were not looked at.

The three changes the first attempt pointed to:
1. **Judge only what the world can contradict:** drives, loads, unloads and refuels. For a wait, a claim or a report
   the layer's own verdict stands.
2. **A learned lie quarantines; it condemns only on strong evidence.** Above the first threshold (t1) an action is
   judged a lie, so the layer sets the tool aside for its three ticks. A road is distrusted for good only when the
   surprise is above a second, higher threshold (t2), or when the learned judge has called a drive on that road a lie a
   second time.
3. **The varied model, calibrated on varied driving.**

Fixing the thresholds showed that change 3 pulls against the others. Calibrated on its own held-out training seeds
(ending in 9), the varied model's t1 is 7.628 (0.49% of landed actions above it) and its t2 is 16.813 (the 99.95th
percentile of landed drives); on arm J's held-out driving it catches 72% of road lies above t1 and none above t2. It
has seen so many lies that it is calm about them. The planner model on its held-out seeds 550 to 599: t1 = 4.720 (the
99.5th percentile of landed judged actions; 0.47% above it, 98% of road lies above it) and t2 = 5.785 (the most
surprising landed drive; 96% of road lies above it). So both are run:

- **Arm Y:** arm J with the learned judge, changes 1 and 2, the planner model (`convoy-world-j`), t1 = 4.720, t2 = 5.785.
- **Arm Z:** arm J with the learned judge, changes 1, 2 and 3, the varied model (`convoy-world-varied`), t1 = 7.628,
  t2 = 16.813.

Judged on seeds 0 to 49:

| id | claim | falsifier |
|---|---|---|
| T9 | arm Y delivers at least as much as arm J and acts on no more lies | less delivered, or more lies |
| T9b | arm Y's learned judge gives false alarms on fewer than 1% of the actions it judges | 1% or more |
| T9c | arm Z delivers at least as much as arm J and acts on no more lies | as for T9 |
| T9d | arm Z's learned judge gives false alarms on fewer than 1% of the actions it judges | as for T9b |
| T9e | arms Y and Z replay byte for byte | any difference |

The prediction: Y holds T9 and T9b. Z keeps its false alarms low (T9d) but misses lies, so its trucks take the lying
road more than once and T9c fails.

## Step 5, the learned judge's second attempt, built (2026-10-28)

**T9 failed by three lies; T9b, T9d and T9e held; T9c failed, as predicted.** On seeds 0 to 49:

| | arm J (hand-coded) | arm X (first attempt) | **arm Y** (planner model, fixes 1 and 2) | arm Z (varied model, fixes 1 to 3) |
|---|---|---|---|---|
| delivered | 1500/1500 | 1500/1500 | **1500/1500** | 1442/1500 |
| lies acted on | 54 | 63 | **57** | 622 |
| a lying road taken again | 0 | 0 | **2** | 555 |
| trucks stranded | 6 | 8 | **6** | 5 |
| villages served by tick (unserved counts 61) | 25.5 | 27.7 | **25.7** | 33.7 |
| false alarms | — | 131 of 4,197 (3.1%) | **19 of 2,396 (0.79%)** | 6 of 2,898 (0.21%) |
| lies missed | — | 0 | **0** | 556 |

**Arm Y nearly does what the hand-coded check does.** It delivered everything, served the villages in the same time
(25.7 ticks against 25.5), stranded as many trucks, and missed no lie, with false alarms down from 3.1% to 0.79%. Two
changes did it: judging only what the world can contradict removed the waits and claims that made up most of the
first attempt's false alarms, and quarantining before condemning kept a false alarm from costing a good road for the
rest of the run.

**Why T9 still failed: the price of patience.** Two lies were less surprising than the condemn threshold (t2), so
they only quarantined the lying road; after the three ticks the planner came back to it, met the lie a second time,
and only then distrusted it for good. That is two lies more than the hand-coded check, which condemns at once; the third
extra lie is one more first encounter than J had, not traced further. Patience is what keeps false alarms cheap, and it costs a second encounter whenever the
model is only moderately surprised.

**Arm Z confirms the prediction.** The varied model, calibrated on varied driving, kept its false alarms lowest
(0.21%), but at its thresholds it judged 556 lies landed: it has seen so many lies that it is not surprised by them.
Its trucks took the lying road 555 more times and 58 units went undelivered. A world model that is to judge needs to
be calibrated on the driving it will judge, and a model that has learned lies are common is a poor judge of them.

What it shows across both attempts: a learned model can stand in for the hand-coded effect check, and nearly match it,
once it is kept away from what it cannot check and its alarms are made cheap. It does not beat the check, which is
exact by construction in a world whose facts are true; the place for a learned judge is a world where no such check
can be written.

The code: `LearnedJudgeExtension` takes the judged kinds and a condemn threshold, and `RoadDistrustExtension` asks it
whether a learned lie condemns. Four deliberate breaks each fail a test: every kind judged, every learned lie
condemning, no escalation at the second lie, and T9b accepting 1%. A first version of the quarantine test used a seed
where the planner never came back to the lying road, so it could not show the second encounter; the test now uses
training seed 101, where it does.

## Step 6, a noisy world, registered before the code (2026-10-29)

Written before any of it exists. Nothing below has been run.

**Why.** Step 5 ended where the convoy's design put it: the learned judge nearly matched the hand-coded effect check
and could not beat it, because in the convoy a truck's position reading is always true, so "did the truck arrive?"
has an exact answer. The claim left standing is that a learned judge earns its place where no exact check can be
written. Step 6 tests that claim by building such a world: the convoy with noisy sensors.

**The world change.** A new setting, `ConvoySetup.Noise`, off by default, so every existing arm, golden trace and
battery result stays byte for byte as it is. When it is on, each truck's sensors are noisy, every tick, independently,
from a random stream seeded by the run's seed and the truck, so a noisy run still replays exactly:

- **Position:** with probability 0.10 the truck's position reads as one of the neighbouring places, chosen uniformly,
  instead of where it is.
- **Fuel gauge:** the reading is the truth plus −1, 0 or +1 units, with probabilities 0.15, 0.70 and 0.15 (on top of
  the one gauge that reads high, which stays). A drive burns 1 unit; tanks hold 8 to 12.

The noise is in the sensor, so everything the truck reads sees it: the planner, the control layer's check and the
features a model reads. The judge that scores a run still reads the world's truth. The lying road stays as it is.

What this does to the hand-coded check: after a drive that really happened, the position can read a neighbour
instead of the destination, so the check calls it a lie and the truck distrusts a good road for the rest of the run.
After a real refuel the gauge can read one short of full, which is a lie by the same check. Lies are still caught
almost always; the damage is false alarms, and each one on a drive costs a road.

**What has to be built first.**
1. The noise, as above.
2. **The truth beside the verdict.** The world knows whether each action landed, lied or failed honestly; the
   recorder writes it beside the layer's verdict, and every arm gets two measures scored against it: the check's
   false alarms and its misses. In the clean world these are zero by construction for arm J, which is itself a test.
3. **A hand-written fused check**, the one an engineer who knows the noise would write: a drive is a lie only when the
   position reads the starting place and the gauge did not fall; a refuel landed when the gauge reads at least one
   short of full. Loads and unloads keep the exact check (the noise does not touch them).
4. **A learned landed-judge**, `tools/train/train_landed.py`: a small network that reads the features before, the
   action and the features after, and gives the probability that the action was a lie. It is trained on the world's
   truth, which the simulator has and a real machine would not; that is the honest price of this design, and it is
   why the world-model judge (which needs no labels) is run beside it.
5. The world model of step 5, retrained on noisy driving.

**Data.** Noisy runs on training seeds 50 to 599 only: arm U's driving, plus the policy network sampled at
temperature 2 for variety (as for the varied set). Seeds ending in 9 are held out to fix thresholds. The battery's
seeds 0 to 49 are never trained on.

**The arms**, all in the noisy world, all with arm J's regulators and fixes:

- **Arm U:** arm J as it is: the exact check, no longer exact.
- **Arm V:** arm U with the hand-written fused check.
- **Arm F:** arm U with the learned judge by surprise (the world model retrained on noisy driving), with step 5's two
  fixes (judge only drives, loads, unloads and refuels; quarantine before condemning).
- **Arm P:** arm U with the learned landed-judge, with the same two fixes.

Thresholds for F and P follow arm Y's recipe on the held-out training seeds: t1 at the 99.5th percentile of the
score on landed judged actions, t2 at the highest score of a landed drive. They are written here before the battery
runs.

Judged on seeds 0 to 49, against the world's truth:

| id | claim | falsifier |
|---|---|---|
| T10 | the noise breaks the exact check: arm U's check gives false alarms on more than 1% of judged actions, and arm U serves the villages later than arm J in the clean world | 1% or fewer, or no later. If T10 fails the noise is too weak, and the step stops there |
| T10b | before the loop: on arm U's recorded battery actions (an evaluation set), the landed-judge at t1 calls more than 90% of the lies lies and fewer than 1% of the landed actions lies, and its false alarms plus misses are fewer than the fused check's on the same actions | any of the three. If it fails, arm P is not built |
| T10c | arm P delivers at least as much as arm U, serves the villages no later on average, and acts on no more lies | any of the three |
| T10d | arm P's judge has a lower error rate (false alarms plus misses, over the actions it judges) than arm V's fused check, and arm P delivers at least as much as arm V | either |
| T10e | arm P's judge gives false alarms on fewer than 1% of the actions it judges | 1% or more |
| T10f | arm F's judge misses fewer than 10% of lies with false alarms under 1% | either |
| T10g | with the noise off every existing arm replays as before (golden traces unchanged), and arms U, V, F and P replay byte for byte | any difference |

**The predictions.** T10, T10b, T10c, T10e and T10g hold. **T10f fails:** the surprise sums over every feature, and a
lie on a drive looks, in the position features, like a misread (the starting place is a neighbour of the destination);
what tells them apart is the gauge, one unit in a tank of ten, which barely moves the sum. **T10d is the open
question.** The fused check is close to the best rule there is for this noise, so the honest prediction is a narrow
result either way. If arm P only matches arm V, the finding is still worth having: a judge learned from examples
reached what an engineer wrote knowing the noise model, without being told it.

**Cost.** No paid model: recording, training and the battery all run on the laptop.

**Decided (2026-10-29; the owner left the three choices to the author):**
- **The noise levels stay at 0.10 and 0.15, with one check on training seeds.** Before any battery run, arms J and U run
  noisy on training seeds 50 to 149. If arm U delivers less than 95% of the demand there, the convoy is too broken for
  the comparison to mean anything and both levels are halved, once. If arm U's check gives false alarms on 1% or fewer
  of its judged actions there, the noise is too weak and the position level doubles, once. The levels that come out
  are written here before seeds 0 to 49 run. **The check, run 2026-10-29:** arm U on seeds 50 to 149 delivered 2,956
  of 3,000 (98.5%), and its check gave false alarms on 395 of 5,458 checked actions (7.2%), missing 2 of 129 lies; it
  served the villages by tick 36.9 on average against arm J's 24.7 there. Neither rule applies: **the levels stay at
  0.10 and 0.15.**
- **Arm V stays.** Beating a check that ignores the noise would prove little; the fused check is the bar.
- **The landed-judge learns from the simulator's truth.** A real machine would not have it, which is the design's
  stated price; arm F, which needs no labels, runs beside it so the step says what each kind of judge can do.

**Thresholds, fixed before any battery run (2026-10-29).** Data: arm U on seeds 50 to 599 (550 runs, 60,744
transitions) and the policy network sampled at temperature 2 in the noisy world on the same seeds (64,367
transitions); seeds ending in 9 held out.

- **Arm P, the lie judge** (`models/convoy-judge-noisy.json`, 158 → 128 → 128 → 1, trained on 54,007 checked actions
  with 1,464 lies): **t1 = 0.555544, t2 = 0.955596.** On arm U's held-out seeds (2,960 landed, 77 lies) it calls 66% of
  the lies lies at t1 and 5% at t2. Four training settings were tried (lies weighted up or not; 64 or 128 units; two
  rates) and the one with the fewest held-out errors kept; the choice was made on training seeds only.
- **Arm F, the world model** (`models/convoy-world-noisy.json`, retrained on the same data): **t1 = 10.82, t2 = 12.534.**
  On the same held-out actions only 3.9% of lies are above t1 (median surprise at a lie 8.46, at a landed action 3.75):
  the misreads make landed actions as surprising as lies.

What the held-out seeds already say about T10b: on those 77 lies the fused check catches 64% with 0.37% false alarms (39
errors); the judge 66% with 0.51% (41 errors). The 90% bar in T10b was set without working out what the noise allows:
when a truck that really drove misreads its position as the starting place, the readings are those of a lie, and only
the gauge (one unit, itself noisy) can tell them apart. T10b is scored as registered all the same.

## Step 6, a noisy world, built (2026-10-29)

**T10 held. T10b failed, so arm P was not run on the battery, and T10c, T10d and T10e are not scored. T10f failed,
as predicted. T10g held.** On seeds 0 to 49:

| | arm J (clean world) | arm U (effect check) | arm V (fused check) | arm F (world model) |
|---|---|---|---|---|
| delivered | 1500/1500 | 1471/1500 | **1500/1500** | 1406/1500 |
| lies acted on | 54 | 81 | **75** | 1118 |
| a lying road taken again | 0 | 3 | 14 | 1036 |
| trucks stranded | 6 | 8 | 6 | 2 |
| villages served by tick (unserved counts 61) | 25.5 | 35.9 | **29.1** | 43.7 |
| false alarms, of checked actions | 0 of 2,387 | 204 of 2,882 (7.1%) | **10 of 2,519 (0.40%)** | 16 of 3,423 (0.47%) |
| lies missed | 0 of 48 | 3 of 75 | 14 of 69 | 1,078 of 1,116 |

With the noise off nothing moved: the 900 runs of the clean battery make the same decisions, one for one, as before
the change (cell ids, which are random, scrubbed), so every earlier result stands.

**The noise breaks the exact check (T10).** Arm U's check still catches lies (3 missed of 75), but it calls 7.1% of
landed actions lies, and each false alarm on a drive costs the truck a good road for the rest of the run. The
villages were served ten ticks later than in the clean world, and five runs ended short (29 units).

**The fused check is the best judge in the noisy world.** Arm V delivered everything, served the villages 6.8 ticks
sooner than U, acted on fewer lies (75 against 81), and cried wolf on 0.40% of actions. Its price is the lie it reads
as a misread: it missed 14, and each was a second trip on the lying road.

**T10b failed, so the lie judge is not scored.** On arm U's battery actions (2,882 checked, 75 lies) at t1 it called
62.7% of the lies lies (the bar was 90%), called 0.61% of landed actions lies (under 1%, held), and made 45 errors
against the fused check's 32. A slip to own: the code for arm P was written before T10b was scored, where the
registration said it would be built only after. It was not run on the battery's seeds, and it is left out of the
default battery.

**Why the lie judge loses, found on training seeds only.** Arm P run on seeds 50 to 149 to find out, not as a test:
it delivered 2,991 of 3,000 (U 2,956, V 2,998) and served the villages by tick 34.1 (U 36.9, V 30.1), but acted on 591
lies (U 144, V 152), 462 of them a second time on the same road. On seed 77 one truck took the lying road from the
depot towards Alba 33 times. Each time it read the depot before and after, with a full gauge (it was the truck whose
gauge reads high, so the gauge said nothing), and the judge gave the drive 0.02 to 0.34, under t1. For one drive seen
in training that is close to the right answer: a truck that really reaches Alba misreads its position as the depot
about once in thirty, lies are rare, so "the depot again" alone is roughly an even bet. But the judge was trained on
single actions, from driving where a lie was caught at once, and it has no memory. In the loop a missed lie is tried
again, and each try looks like the same even bet. The fused check does not weigh how rare lies are: it calls the same
readings a lie every time. This is the trouble that DAgger names for policies (a learner's errors take it where its
teacher never went), here in a judge.

**T10f failed, as predicted.** Arm F's world model missed 1,078 of 1,116 lies (96.6%): a misread makes a landed drive
as surprising as a lie, and the one feature that tells them apart, the gauge, barely moves a sum over 61 features.
Its trucks took the lying road 1,036 more times; 94 units went undelivered in 17 runs.

**A flaw found afterwards** (while registering the second attempt): the roads a truck is offered come from where it
really is, and each place has its own set of roads, so the offered roads give the true position away one tick late.
No judge here used them, so the comparison between the judges stands; but an exact check could have been written, a
tick late. The second attempt seals that leak first.

What step 6 shows: in a world where the readings are the only evidence a judge looks at, a hand-written rule built on knowing the noise beat both
learned judges, and the reason is not that the networks are too small. The world model scores what is surprising, not
what is a lie; the lie judge scores how likely a lie is for one action, as its training data saw it, and the loop
changes those odds. A second attempt, not registered, has three directions: give the judge memory (the tries on this
road, or the last few readings), train it on the loop's own driving (record arm P and relabel by the truth), or set its
threshold by what each error costs in the loop rather than by a fixed false-alarm rate.

The code: `SensorNoise` and `ConvoySetup.Noise` (off by default); `TruckView.LastTruth`, recorded as `truth` beside
each verdict and exported in world sets; `VerdictTally` and the measures `Checked`, `CheckedLies`, `CheckFalseAlarms`
and `CheckMisses` for every arm; `FusedCheckExtension`; `LieJudge` in `Creatura.Seats` and
`tools/train/train_landed.py`; arms U, V, F and P; T10, T10c to T10g in the battery. Twelve tests in
`NoisyWorldTests`. Eight deliberate breaks each fail a test: the position never misread, the gauge only reading low, a
lie recorded as landed, the fused check taking position or gauge, false alarms counted on caught lies, T10 accepting
exactly 1%, the lie judge reading its inputs in the wrong order, and the learned judge counting against the noisy
check instead of the truth. That last one passed at first (on the seed the test used, both counts agreed); the test
now compares the judge's counts with the truth's over six more seeds. A slip in the break script also showed up and was
fixed: it restored each file with its old timestamp, so the build kept the broken code, and two tests failed for a
break they did not cover; every break was run again with the files touched.

## Step 6, the lie judge's second attempt, registered before the code (2026-10-29)

Written before any of it exists. Nothing below has been run.

**What the first attempt pointed to.** The lie judge scored each action alone, from training data where lies were
caught at once; in the loop a missed lie is tried again, and every try looked like the same even bet. Two changes
answer that: **memory** (the judge is told whether this truck just took this same action) and **training on the
loop's own driving** (the judge's own runs, labelled by the truth, added to its data, as DAgger does for policies).

**A flaw in step 6's world, found while writing this.** The roads a truck is offered are the roads from where it
really is, and in the flooded valley every place has its own set of roads (eight places, eight sets). So the offered
roads give away the true position, one tick late. None of step 6's judges looked at them (the effect check, the fused
check and both networks read only the position and fuel readings), so step 6's comparison between them stands. But
its claim that no exact check could be written was wrong: one could, a tick late. A judge with memory would learn
that leak and nothing else, so it has to be sealed first.

**The world change: noise sealed.** A new setting, `SensorNoise.Sealed`, off for arms U, V, F and P so step 6 replays
as it is. When it is on, the truck is offered the roads from where its position *reads* it is, and the depot's load
and refuel when it reads the depot. A truck that misreads takes a road that does not start where it is, and the world
answers "no such road from here", an honest failure. Nothing the truck is given then tells it its true place, except
readings that are each wrong one time in ten and the world's own reports.

**What is built.**
1. The sealed noise, as above.
2. **Two memory inputs** for the lie judge, computed by the judge from the truck's own history and nothing else:
   `again`, 1 when the truck's previous action was this same action and was reported a success; and `tries`, the
   earlier tries of this same action in the run, over 5, at most 1. The judge reads 160 inputs: the features before,
   the action, the features after, and the two.
3. **The fused check with memory** (arm V2's), the rule an engineer would add to the fused check after seeing step 6:
   a drive is also a lie when the position reads the starting place and the truck's previous action was this same
   drive, reported arrived. The rest of the fused check is as in arm V.
4. **Loop data, in rounds.** Round 0: arm U2's driving and the policy network sampled at temperature 2, both sealed, on
   seeds 50 to 599. Then two rounds: run arm P2 with the judge so far on seeds 50 to 599, add its recorded actions
   labelled by the truth, and train again from the start on everything. Two rounds, fixed now, whatever they show.
5. Thresholds by arm Y's recipe, on the driving the judge will judge: the last round's own runs, seeds ending in 9.
   t1 at the 99.5th percentile of the score on landed checkable actions, t2 at the highest score of a landed drive.
   Written here before the battery runs.

**The arms**, all sealed, all with arm J's regulators and fixes:

- **Arm U2:** the effect check.
- **Arm V2:** the fused check with memory.
- **Arm P2:** the lie judge with memory, trained on loop data, quarantining before it condemns (as arms Y and P).

**The gate before the battery, and its bar.** This time the bar is one the noise allows. On the last round's held-out
training actions, the judge at t1 makes fewer errors (false alarms plus misses, against the truth) than the fused check
with memory makes on the same actions, computed from the recorded readings. If not, arm P2 is not run on the battery.

Judged on seeds 0 to 49, against the world's truth:

| id | claim | falsifier |
|---|---|---|
| T11 | sealed, the noise still breaks the effect check: arm U2's check gives false alarms on more than 1% of checked actions, and arm U2 serves the villages later than arm J | 1% or fewer, or no later. If it fails, the step stops |
| T11b | the gate above, on training seeds | as stated. If it fails, arm P2 is not run |
| T11c | arm P2 delivers at least as much as arm U2 and serves the villages no later on average | either |
| T11d | arm P2's judge has a lower error rate (false alarms plus misses over checked actions) than arm V2's check, and arm P2 delivers at least as much as arm V2 | either |
| T11e | memory ends the repeats: arm P2 takes a lying road again no more often than arm V2 does | more repeats than V2 |
| T11f | arm P2's judge gives false alarms on fewer than 1% of checked actions | 1% or more |
| T11g | arms U2, V2 and P2 replay byte for byte, and every earlier arm replays as before | any difference |

**The predictions.** T11, T11c, T11f and T11g hold. T11e holds: with `again`, a lie met twice in a row is plain to
both. **T11b and T11d are the open question**, for the same reason as before: on a first encounter the readings of a
lie and of a misread are the same, so both judges are near the limit the noise sets, and the result should be narrow
either way. If the judge only ties the hand-written rule, the finding is that memory and loop data cured what broke
the first attempt, and that the bar was reached without the rule being written.

**Cost.** Nothing paid; three rounds of recording and training on the laptop.

## What this note does not cover

- Training on data from real machines or real users. Every dataset here comes from simulated worlds.
- Models for worlds other than the convoy, until the convoy's have verdicts.
- Running training on a server: the first steps fit on the laptop.
- The Claude Code hooks' ledger, which records real work in real repositories and is not training data.
