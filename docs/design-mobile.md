# Design: the control layer on the phone, and what big models teach the small one

**Status:** design note, nothing built. Written 2026-10-30 on the owner's laptop; the part on transferring
knowledge from big models was added the same day. All eleven decisions were answered by the owner the same day
(see "Decided"); two of them (D4, D9) change parts written above, and the table says how.

## The problem, in the owner's words

> Most of my apps use the Singular framework, so I guess if the model will be on mobile we have to have a version
> that works with that framework.

The owner's apps are React Native / Expo apps on **Singular Framework**, his own backend-as-a-service (private, not
part of this repository). Each app can have an assistant. Today the whole assistant runs on the server: the model,
the tool loop, and the tools, which read **as the signed-in person** and never write. Writes come back as
*proposals* that the person confirms. The phone only shows a stream of events: text as it is generated, a tool
starting and ending, a card, a proposal, and a final `done`.

Moving the model onto the phone moves the tool loop with it, and the loop is exactly where Creatura's control layer
sits. Creatura is C#; the phone runs JavaScript. So the phone needs its own copy of the control layer, small enough
to ship inside an app, and an adapter that makes it look to the app like the assistant it already has.

**What an on-device model is for.** The prompt never leaves the phone and no tokens are spent with a provider.
It works where the provider can't be reached, and with local checkers it works offline. It is not for broad chat: a
small model on a phone is weak at choosing and filling tools. The control layer can stop its bad calls. It cannot
make it good at the job. So the first uses are narrow ones, with a few tools whose effects can be checked, and the
second half of this note is about making the small model better at them with what bigger models know.

The owner's second ask, the same day:

> I want to transfer agent knowledge that we learn in the bigger models, like the JEPA type of model that we can
> train, to see if that improves the local small model on mobile. Also, how can we fix the tool calling? Maybe an
> adapter trained on Creatura.

## Three principles carried over from the framework

1. **Judge by effect, not by report.** The model saying "I booked it" is a report. What counts is a fact the
   backend or the app's own checker confirms.
2. **Nothing is written without the person.** On the server the assistant only proposes. On the phone it must stay
   that way: a write is a release-kind action, and the person is the only gate that releases it.
3. **The rules that keep data private stay on the server.** The phone's tools are ordinary SDK calls made as the
   signed-in person, so the server's access rules still decide what the model can read. The phone adds checks; it
   never replaces one.

## The parts

### `creatura-js`: the control layer, in TypeScript

A port of `Creatura.Core`'s control layer only (`ControlLayer`, `Quarantine`, `StageInterlock`, `PredictionTracker`,
`CoherenceAudit`, the host frame, the verbs and their records) and the restore world with its planner, which the
tests need. Strict TypeScript, no runtime dependencies, runs in React Native, a browser and Node.

Not ported: the designs (Bach, Levin, hybrid, map-keeper), the bench, the convoy, the seats, the training tools,
the hosts. They stay in C#.

The same verbs as in C#, one model call per tick:

| C# | TypeScript | what it does on the phone |
|---|---|---|
| `Offer()` | `offer()` | the tools the model is shown this round, with quarantined ones hidden |
| `Admit(toolId)` | `admit(toolId)` | before a tool runs: is it in the offer, and is it allowed now; a write is refused and becomes a proposal |
| `Judge(tool, report)` | `judge(tool, report)` | after it runs: did the promised facts land, by the backend's data and the app's checks |
| `Stop()` | `stop()` | no progress, failures over budget, or the round limit: stop and use the fixed reply |
| `Close(reply, noRoute)` | `close(reply, noRoute)` | the turn is done when the goal's facts hold, not when the model says so |

**Kept identical by the goldens.** The four controlled goldens (`tests/Creatura.Hosts.AgentFramework.Tests/golden/
restore-*-controlled.jsonl`, the planner in the model's seat) are replayed by `creatura-js`'s own tests and must
match byte for byte. The TypeScript reads the files from this repository, so a change in C# that moves a golden
breaks the TypeScript build too until both agree. That is how two languages stay one control layer.

### Facts, tools and checks for an app

The control layer reads the world as **facts** and **tools that require and grant facts** (`Tool(Id, Requires,
Grants, Kind)`). An app's assistant maps onto that like this:

| assistant | Creatura | example (a booking app) |
|---|---|---|
| a read of a collection | a tool that grants a fact | `read-availability` grants `availability-known` |
| a proposal | a tool that grants a fact, checked by the app | `propose-booking` grants `slot-proposed`, true only if the app's check says the slot is free |
| an app tool | a tool with its own `Requires` and `Grants` | `quote-price` requires `service-chosen`, grants `price-known` |
| confirming a proposal | a release-kind tool | only the person releases it, from the proposal card |
| the turn's goal | a predicate over facts | "a valid proposal exists for the slot the person asked for" |

**Checks are the app's**: small functions that turn backend data into facts (`slotFree(proposal)`,
`priceMatchesList(quote)`). They are the effect check's registry on the phone. A tool whose promised fact doesn't
land is quarantined for a few rounds, exactly as in C#. With no check for a fact, the layer can only take the
tool's word for it, and the design says so in the trace.

### The adapter: the same assistant, run on the phone

A second package (where it lives is decision D1) with a local assistant whose `chat()` emits **the same event
stream** as the server's assistant. The app's chat screens don't change; the app chooses local or server per
phone and per assistant.

```ts
const assistant = canRunLocally(device) ? localAssistant(client, { model, checks }) : client.assistant;
for await (const e of assistant.chat({ message, conversation })) render(e);
```

Inside, one turn is the control layer's round:

1. `offer()` the tools the person's assistant has, minus quarantined ones.
2. The on-device model answers or calls a tool. Text streams out as `delta` events.
3. `admit()` the call. Refused calls are fed back to the model with the reason.
4. Run the tool **as the signed-in person**: a read through the SDK, or an app tool through the server (see the
   platform asks). Emit its `tool` start and end, and its card.
5. `judge()` the effect with the app's checks. A proposal that fails its check is not shown to the person.
6. Repeat until `close()` says the goal's facts hold, or `stop()` ends the turn with the fixed reply.
7. Emit `done` with the proposals that passed.

### The model on the phone

Through llama.cpp's React Native binding (`llama.rn`), which runs GGUF models on the phone's GPU. Candidates are
2B–4B instruct models with tool calling, quantised to 4 bits: a download of roughly 1–2.5 GB on first use, over
Wi-Fi, resumable. It needs a native build (EAS), not Expo Go. A phone below the memory line (decision D3), or one
that hasn't downloaded the model yet, keeps the server's assistant.

Everything in this section is to be confirmed by spike S0 before any Creatura code: tool calling through the
binding, memory, speed and heat on a real phone.

### The ledger on the phone (later)

A phone is a vessel like any other. The cell key is ECDSA P-256 as in `Creatura.Cells`, generated on the phone and
kept in the platform key store (Keychain, Android Keystore). Signing uses a pure-JS P-256 library, since React
Native has no WebCrypto signing. The hardware id is the vendor id (iOS) or Android ID, hashed with the owner's salt
as on other vessels. The vendor id changes when the app is reinstalled, so a reinstall is a new vessel for the
same creatura. Each turn writes signed entries (claim, progress, release), and their signatures must verify in the
C# cells code. Where they sync to is decision D5.

## Knowledge from big models to the phone

### What Creatura's own experiments already say

The convoy was the test bed for this question, and its results (docs/design-trained-models.md, steps 1 to 10) set
the plan:

- **A raw small model is bad at choosing, not only at formatting.** `qwen2.5:7b` in the seat with function calling
  had half its answers refused as not a listed action. Answering in JSON, every answer was valid and none was
  useful. Fixing the format alone does not fix the agent.
- **A tiny network copies a careful teacher almost exactly.** Trained by example on the planner, a 97-64-64-36
  network chose what the planner chose 99.97% of the time, in microseconds, and as a guide it let the planner be
  called on 0.3% of decisions with nothing lost (step 8, arm MN).
- **Copying a big language model needs much more data.** A copy of DeepSeek matched it 63% of the time from 10
  runs and 71% from 50. Alone in the seat, the 10-run copy delivered 73% of the units, where DeepSeek itself
  delivers about 95%. Behind its 50-run copy, DeepSeek was called 85% as often with no loss (steps 9 and 10).
- **A world model is a judge, not a driver.** The forward model and the JEPA caught lies well where the driving
  looked like their training (AUC near 1.0), and the JEPA held up better on a driver it had never seen (0.92
  against 0.87). As a guide that saves calls, a plain policy network beat the JEPA (step 8). A learned judge nearly
  matched the exact check, so its place is where no exact check can be written.

So the knowledge transfers best when the job is **split**, with each part learned by the smallest thing that can
do it, and the language model left with only the part that needs language.

### One turn, four jobs

| job | done by | trained from | size on the phone |
|---|---|---|---|
| **choose** the next tool, or answer | a **tool router**: a small policy network over the turn's features, masked to the offered tools | the teacher's choices that the control layer admitted and that landed | kilobytes, microseconds |
| **fill** the tool's arguments | the small language model, **constrained to the tool's JSON schema** by a grammar at decoding time | nothing: the grammar makes a malformed call impossible | none |
| **talk** to the person | the small language model with a **LoRA adapter** | the teacher's turns that passed the control layer | tens of megabytes |
| **check** what a tool did | the app's exact checks; where none can be written, a **small world model** whose surprise quarantines the tool | transitions recorded in the app's world: facts before, tool, facts after, verdict | kilobytes |

**Choosing.** The router reads the turn as numbers (the facts the turn has, which tools are offered, what the
person's request is about as a small embedding) and scores every tool in the app's vocabulary. When it is sure (the
margin rule of steps 8 and 10), it chooses; when it is not, the language model chooses, constrained to the offered
tools' names. Either way the control layer admits the choice. This is where most of a small model's tool-calling
mistakes come from, and the convoy shows a network learns it from a careful teacher with little data.

**Filling.** llama.cpp can constrain decoding to a grammar or a JSON schema. Given the chosen tool's schema, the
model can only write a call that parses and has the right fields and types. Whether a value is *right* (an existing
service, a free slot) is still the admit and judge steps' business.

**Talking.** The adapter is trained on the teacher's replies in the app's own world, in exactly the prompt the
phone's model sees: the app's instructions, the offered tools, the cards it has shown. Only turns the control layer
accepted are used, plus pairs of a refused and an accepted turn from the same situation, for preference training.
That is "an adapter trained on Creatura": the data is recorded and labelled by the control layer. Training one
behaviour into a small model can cost it others, so every adapter is also scored on a fixed set of turns that
don't use tools, and must not lose on them.

**Checking.** A forward model or a JEPA over the app's facts predicts what a tool's call should change. When the
backend reports a change the model finds surprising, the tool is quarantined like any other liar. It is used only
for facts no exact check covers, since an exact check is better where it exists.

### Where the knowledge comes from: the app's world

Creatura's point is to test agents in emulated worlds before they touch the real one. For an app, the world is an
**emulated backend**: the app's collections and their schemas, seeded data, the assistant's tools, and barriers,
which are tools that fail or lie on purpose (a booking that reports success and writes nothing). It runs offline,
so it can be replayed seed by seed, and the control layer's verdicts are its labels.

1. **Requests.** A big model writes requests in the voices of the app's users (an owner, a client, a new client),
   from the app's own vocabulary. Each request has a seed. Seeds are split before anything is recorded: the test
   requests are never trained on.
2. **The teacher drives.** A big model in the seat answers each request through the control layer in the emulated
   world. The recorder writes every decision: the rendered prompt, the features, the offered tools, the choice, the
   arguments, the verdict, and the turn's outcome.
3. **Export.** The exporter writes the four training sets: features to tool (router), prompt to reply (adapter),
   accepted and refused pairs (preference), and transitions (world model). Only turns whose goal's facts held count
   as good examples.
4. **Train.** The networks with the numpy trainers that exist (`tools/train/`); the adapter with MLX on the laptop,
   converted for llama.cpp.
5. **Catalogue.** Each trained part is catalogued with the app's world family: the app, the hash of its feature
   schema and of its tool vocabulary, the seeds it was trained on. The phone refuses a part whose hashes don't
   match the app it is in, as the convoy's seats do. When the app's tools change, the old parts are refused and
   the phone falls back to the language model alone until they are retrained.

**What it costs.** The convoy's paid runs cost about $0.0006 per DeepSeek call at list price. An app world of 5,000
requests at about four calls each is about 20,000 calls, roughly $12 per app and per teacher. No paid run happens
without the owner's yes and a cap.

**Where it runs.** The recorder, exporter, trainers and catalogue exist in C# and Python, and step 10 already drives
a paid model through them. The emulated app world is new. The phone needs only the forward passes, which are a few
dozen lines of TypeScript per kind of network, reading the same JSON weight files.

## What the platform needs (generic, nothing Creatura-specific)

Today only the server knows an assistant's instructions, which collections it may read and propose, and its app
tools, whose handlers are C# on the server. A local loop needs two things any client could use, so the framework
stays app-agnostic:

| | ask | why |
|---|---|---|
| A1 | describe an assistant to the person allowed to use it: instructions, readable and proposable collections, tool schemas | the local model needs the same tools and instructions the server's has |
| A2 | run one app tool as the person, without the model | app tools stay on the server, with their own access checks; the phone only decides when to call them |

A third, optional: report a local turn's usage, so the app's existing quota and audit see local turns too.

With A2, app tools need the network, so the first version is **online with a local model**. Offline turns are
possible only for assistants whose tools are local checkers (a tutor with a maths checker, for example).

## The same rules on the server

The server's assistant loop is C#, so the existing `Creatura` package can wrap it with no port: the same verbs
around each tool call, the same checks written in C#. It is a separate piece of work, and needs a generic seam in
the framework ("a guard sees every tool call"). It is listed here because both placements use the same facts,
tools and checks, so an app's checks could be written once per language and tested against the same cases.

## Decisions for the owner

| | decision | options | recommendation |
|---|---|---|---|
| D1 | where the adapter lives | (a) in this repository; (b) in the framework's repository, as its own optional package; (c) inside the framework's SDK | **(b)**: the SDK is private and unlicensed, and this repository is public. Creatura stays out of the SDK itself, so apps that don't use it don't carry it |
| D2 | the first app | an app with a few checkable tools; a broad assistant | **a narrow one**: three to five tools, each with a check |
| D3 | model and memory line | 2B, 3B or 4B; phones with 6 GB or 8 GB of memory and up | after S0; start with the smallest model that passes S0's tool-calling bar |
| D4 | where app tools run | (a) on the server through A2; (b) ported to the phone | **(a)**: one copy of each tool, and the server's access rules apply |
| D5 | where the phone's ledger goes | none in the first version; the cells hub; a collection in the app's own backend | **none at first**, then the hub, so phones show up in the same activity stream as other cells |
| D6 | `creatura-js` licence | the same PolyForm Noncommercial as the rest; a separate licence | **the same**. The owner can use it in his own apps; anyone else running it needs a licence from him |
| D7 | package names | `creatura-js` and `creatura-singular`; a scope | the plain names, unpublished until S2 passes |
| D8 | the teacher | DeepSeek; a larger open model run locally; several teachers | **DeepSeek first**, as in steps 9 and 10, then a second family to see what is the teacher's habit and what is the app |
| D9 | where the emulated app world is written | C#, beside the convoy and the recorder; TypeScript, beside the phone's control layer | **C#**: the recorder, exporter, catalogue and paid-model seat exist there. The phone only runs what was trained |
| D10 | what the router reads about the request | the facts and offered tools only; plus a small text embedding of the request | **both, compared** in K2: the embedding costs a second small model on the phone |
| D11 | the adapter's base model | the same base as the phone's model (D3), always | yes: an adapter only fits the base it was trained on, so changing D3 means retraining |

## Decided (owner, 2026-10-30)

| | decided | what it changes |
|---|---|---|
| D1 | **(b)** the adapter is its own optional package in the framework's repository | nothing above |
| D2 | **a new small app built for this: a maths tutor**. A student asks for help with a problem; the tools look up the problem, check an attempt exactly, and give the next hint | every effect has an exact check, and the whole turn can run offline. The world model (K5) has little to judge here, so it is scored only if the tutor gets facts without a check |
| D3 | **after S0**: try a 2B model first, move up only if it can't call tools reliably; phones with 6 GB of memory and up | nothing above |
| D4 | **(b) ported to the phone**: app tools are TypeScript and run on the phone | A2 is no longer needed. Ported tools read only through the SDK as the signed-in person, so the server's rules still decide what they see (principle 3). Turns can be offline where a tool needs no fresh data |
| D5 | **none at first, then the hub** | nothing above |
| D6 | **PolyForm Noncommercial**, the same as the rest | nothing above |
| D7 | **`creatura-js` and `creatura-singular`**, unpublished until S2 passes | nothing above |
| D8 | **DeepSeek first**, a second family later | nothing above |
| D9 | **TypeScript**, changed from the recommendation because of D4 | the emulated world runs the very tool code the phone runs, so nothing drifts. The recorder and exporter get a small TypeScript version writing the same JSON files the trainers in `tools/train/` read. The catalogue check is ported with them |
| D10 | **both, compared in K2** | nothing above |
| D11 | **same base always**: the catalogue records the base model's hash with each adapter, and the phone refuses one trained on another base | nothing above |

## Pre-registered tests, written before the code

The numbers are proposals; the owner may change them, but before the code, not after the results.

| id | claim | falsifier |
|---|---|---|
| M1 | `creatura-js` reproduces the four controlled goldens byte for byte | any difference in any row, note, goal or tick count |
| M2 | the local assistant's events pass the same contract tests as the server's: the same types, tool start before end, proposals before `done`, exactly one `done` | any contract test failing on the local stream |
| M3 | nothing is written by a local turn: in a 200-turn run against a test tenant, the only writes are ones the person confirmed | any document changed without a confirmation |
| M4 | a local turn reads nothing the person can't: with two test roles, one barred from a collection and a hidden field, no reply, card or proposal shows the barred data | the barred data anywhere in a turn's output |
| M5 | the guard helps: on 50 requests written before the run, with checks that fail on purpose in a fifth of them, the guarded local model shows the person wrong proposals in at most 2% of turns, against the unguarded model's measured rate, and uses the fixed reply in at most 20% | over 2% wrong proposals shown, or over 20% fixed replies |
| M6 | it is usable on a reference phone (named before the run): model loaded in at most 10 s, first text in at most 3 s, median turn at most twice the server assistant's, no memory crash in 50 turns | any of these missed |
| M7 | a phone below the memory line, or without the model, gets the server assistant on the same screens | a local load attempted, or a different screen |
| M8 | ledger entries signed on the phone verify in the C# cells code, and a changed byte fails | an entry that doesn't verify, or a changed one that does |

For the trained parts, scored on the emulated app world's test requests, never trained on:

| id | claim | falsifier |
|---|---|---|
| K1 | with the grammar, at most 1% of the small model's tool calls fail to parse or miss a required field, against its measured rate without | over 1% |
| K2 | the router, where its margin says it is sure, chooses the tool the teacher chose and the control layer accepted at least 90% of the time, and is sure on at least half the decisions | under 90%, or sure on under half |
| K3 | the small model with the adapter, router and grammar finishes at least 80% of the turns whose goal the teacher finished, and is within 10 points of the teacher | under 80%, or more than 10 points behind |
| K4 | the adapter costs nothing elsewhere: on a fixed set of turns without tools, its replies are judged no more than 5 points worse than the base model's | more than 5 points worse |
| K5 | the world model, on facts no exact check covers, catches at least 80% of the seeded lies with at most 2% false alarms | under 80%, or over 2% |
| K6 | the whole stack beats the raw small model on M5's measures (wrong proposals shown, fixed replies), each by at least 10 points | any measure better by under 10 points |
| K7 | what was learned is the app, not its data: trained on one seeded tenant, scored on another with different data, K3 drops by at most 5 points | a drop of more than 5 points |
| K8 | on the reference phone the router and world model together take at most 5 ms per decision and 1 MB, and the adapter at most 100 MB | any of these over |

## What gets built first

| slice | what | passes when |
|---|---|---|
| S0 | spike, no Creatura: one model in a native dev build of one app, calling one tool | tool calls are valid in at least 9 of 10 tries; memory, speed and heat recorded |
| S1 | `creatura-js`: the control layer, the restore world, the planner, the golden replay | M1 |
| S2 | the adapter against a test tenant, with A1 and A2 in the framework | M2, M3, M4 |
| S3 | the first app's checks and the guard battery | M5, M6, M7 |
| S4 | the ledger on the phone | M8 |

And the trained parts, which can start in parallel with S1, since they run on the laptop:

| slice | what | passes when |
|---|---|---|
| T0 | the emulated world of the first app, with its barriers, and the request writer | the planner, or a scripted driver, finishes every test request in the clean world; the barriers make it fail where they should |
| T1 | the teacher recorded on the training requests (paid, owner's yes and cap first) and the export | the four training sets written; no test request in them |
| T2 | the router and the grammar, measured on the laptop's copy of the phone model | K1, K2 |
| T3 | the adapter | K3, K4, K7 |
| T4 | the world model for the facts without checks | K5 |
| T5 | all of it on the reference phone | K6, K8 |

S0 comes first because it can kill the idea cheaply: if a small model on a phone can't call tools reliably, the
port can wait. T2 is the cheapest test of the second half: if the router and grammar alone fix most of the tool
calling, the adapter may not be needed.

## What this note does not cover

- One model shared by all the apps. Each app's world family gets its own router, world model and adapter; whether
  one adapter could serve several apps is a question for after K7.
- Training on real people's conversations. Everything here is trained on the emulated world; real turns could
  later be recorded only with the people's consent and the app's privacy terms.
- Offline sync of a local turn's proposals. The framework's offline outbox already queues writes; whether a proposal
  made offline can wait for confirmation there is a question for S3.
- The web build of the apps. A browser can run the same TypeScript, but on-device models in the browser are their
  own spike.
- Other people's apps on the framework. This note is for the owner's apps only.
