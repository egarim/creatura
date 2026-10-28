# Design: trained models for a world

Status: decided 2026-10-28; step 1 (the recorder) built.

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

## What this note does not cover

- Training on data from real machines or real users. Every dataset here comes from simulated worlds.
- Models for worlds other than the convoy, until the convoy's have verdicts.
- Running training on a server: the first steps fit on the laptop.
- The Claude Code hooks' ledger, which records real work in real repositories and is not training data.
