# Setting up a model for the convoy demo

The convoy runs without a language model by default: an offline planner decides for every truck (decision D3 in
[design-machine-vessels.md](design-machine-vessels.md)). Arm **S** puts a language model in every truck's seat
instead. This page says how to choose that model, where it is configured, how to run it, what it costs, and what to
expect.

## What the model does

Each tick, each truck's seat is asked for one action. With a model in the seat, `ConvoyModelSeat` sends it:

- a **briefing**: which truck it drives, the places and roads, what the facts mean, and how a delivery goes (claim,
  load, drive, unload, refuel, wait, report done);
- the truck's **facts**: where it is, what its fuel gauge reads, its load, what its road sensor says, what each village
  still needs, who holds which village on the ledger;
- the **actions** it can take right now, such as `drive:r01:j1`, `load`, `claim:alba` and `wait`.

It must answer with exactly one of those actions. Everything around it stays as it is with the planner: the regulator,
the control layer, the ledger, the faults and the judge. An answer that is not a listed action is refused and never
run; a model that answers with nothing, or whose call fails, makes its truck wait. So a weak model wastes ticks, but
it cannot make a truck do something impossible.

## Quick start: a local model, free

[Ollama](https://ollama.com) runs models on your own machine at no cost.

```bash
ollama pull qwen2.5:7b
```

```bash
dotnet run --project samples/Convoy -- --arm S --seed 1 --ticks 12 --calls calls.jsonl --replay run.jsonl
```

`qwen2.5:7b` is arm S's default model, already in Creatura's catalog. `--ticks 12` shortens the run: a local 7B
model takes about 20 seconds a call on a laptop, and a full run is up to 180 calls. `--calls` writes every call
(prompt, answer, tokens, timings) to a JSON lines file, and `--replay` writes the run for the viewer.

Expect a small local model to drive badly. On the first runs (2026-10-28), `qwen2.5:7b` delivered 5 units in 8 ticks where
the offline planner delivered 15, and half its answers were refused; see "Results" below.

## The model catalog

Models are chosen by name from Creatura's catalog. It is read from `models.json` beside the program when that file
exists, from the file given with `--models-file`, and otherwise from the table compiled into `Creatura.Seats`.
[`samples/models.json`](../samples/models.json) is that table written out: copy it and edit it.

A row:

```json
{
  "name": "deepseek-chat",
  "provider": "openai",
  "baseUrl": "https://api.deepseek.com",
  "model": "deepseek-chat",
  "keyEnv": "DEEPSEEK_API_KEY|deepseek",
  "nativeTools": true,
  "reasoning": false,
  "inputPerMillion": 0.28,
  "outputPerMillion": 0.42
}
```

| field | what it is |
|---|---|
| `name` | what you pass to `--model` |
| `provider` | `ollama`, `openai` (any OpenAI-compatible server) or `azure-foundry` |
| `baseUrl` | where the server is. The OpenAI shape appends `/chat/completions`, Ollama `/api/chat` |
| `baseUrlEnv` | an environment variable holding the base URL, used instead of `baseUrl` when it is set |
| `model` | what the provider calls the model |
| `keyEnv` | the **names** of environment variables holding the key, separated by `\|`; the first one set is used. Never a key itself |
| `nativeTools` | `true`: the model calls one function per tick. `false`: it writes one JSON object, `{"tool": …, "why": …}` |
| `inputPerMillion`, `outputPerMillion` | US dollars per million tokens, for the cost report. Check them against the provider's current prices |

Keys stay in the environment. The catalog, the call log and the replay only ever hold the variable's name. If a
key is missing, the command stops before the first call and names the variables it looked for.

### Rows for common setups

**Ollama, with the JSON protocol.** Small local models often answer badly when asked to call a function. The same
model with `nativeTools: false` writes a JSON object instead:

```json
{ "name": "qwen2.5:7b-json", "provider": "ollama", "baseUrl": "http://localhost:11434", "model": "qwen2.5:7b",
  "keyEnv": null, "nativeTools": false, "reasoning": false, "inputPerMillion": 0, "outputPerMillion": 0 }
```

**DeepSeek's own API.** Already in the catalog as `deepseek-chat`. Set `DEEPSEEK_API_KEY` (or `deepseek`).

**A LiteLLM gateway**, or any other OpenAI-compatible proxy. Set `LITELLM_BASE_URL` to the gateway's address and
`LITELLM_API_KEY` to its key:

```json
{ "name": "gateway-deepseek-flash", "provider": "openai", "baseUrlEnv": "LITELLM_BASE_URL", "model": "deepseek-v4-flash",
  "keyEnv": "LITELLM_API_KEY", "nativeTools": true, "reasoning": false, "inputPerMillion": null, "outputPerMillion": null }
```

Leave the prices `null` when you do not know them: the cost is then reported as unknown, never as zero.

**OpenRouter**, OpenAI-compatible:

```json
{ "name": "openrouter-qwen", "provider": "openai", "baseUrl": "https://openrouter.ai/api/v1", "model": "qwen/qwen-2.5-72b-instruct",
  "keyEnv": "OPENROUTER_API_KEY|openrouter", "nativeTools": true, "reasoning": false, "inputPerMillion": null, "outputPerMillion": null }
```

**Azure OpenAI and Azure AI Foundry.** The catalog's `gpt-4.1-mini`, `gpt-4.1-nano` and `*@azure` rows read their
base from `AZURE_AOAI_BASE` or `AZURE_FOUNDRY_BASE` and their keys from `AZURE_AOAI_KEY` or `AZURE_FOUNDRY_KEY`.

## Running arm S

One run, as a story, with a replay for the viewer:

```bash
dotnet run --project samples/Convoy -- --arm S --seed 1 --model deepseek-chat --calls calls.jsonl --replay run.jsonl
```

`--model` works with any arm: `--arm L --model deepseek-chat` puts the model in arm L's trucks, the three
designs. `--arm` defaults to S when a model is named.

A small battery: the model arm beside the offline planner on the same seeds, with S1 and S2 scored:

```bash
dotnet run --project samples/Convoy -- --battery --arms J,S --seeds 5 --model deepseek-chat --calls calls.jsonl
```

The default battery never runs arm S: it costs money, and a model is not repeatable, so the byte-for-byte check (M6)
does not apply to it.

At the end of a run the command prints what the seat used, for example:

```
The seat (deepseek-chat) made 114 calls (0 failed, 0 invalid), 187,000 prompt and 9,000 completion tokens, cost $0.0561, 140 s of model time.
```

## What it costs

From the first calls: a prompt is about **1,600 tokens** (briefing, facts and actions), and an answer is 30 to 150
tokens. A run is at most 3 trucks × 60 ticks = **180 calls**; trucks stop once every village is served, so a
successful run is nearer 110.

| model | per run, at most | 5 seeds | 50 seeds |
|---|---|---|---|
| a local Ollama model | free (about an hour of compute on a laptop for a full run) | free | free |
| `deepseek-chat`, at the catalog's $0.28 / $0.42 per million | about $0.09 | about $0.45 | about $4.50 |
| `gpt-4.1-mini`, at the catalog's $0.40 / $1.60 per million | about $0.14 | about $0.70 | about $7 |

These come from the catalog's prices, which can be out of date. The cost printed after a run is computed from the
tokens the provider reported.

## Watching it

Open the replay in [`tools/convoy-viewer/index.html`](../tools/convoy-viewer/index.html). Each truck's agent popup
shows the model, where it runs, its price, and the C# that builds the seat, `new ConvoyModelSeat(SeatFactory.Model("…"), …)`.

## Recording training data

`--record runs.jsonl` keeps every decision of a run or a battery: what the seat was given, what it chose, and what it
came to. `--dataset <dir>` records and exports training sets in one step, from seeds 50 up only (the battery's seeds
are never exported). See [design-trained-models.md](design-trained-models.md#step-1-built-2026-10-28).

```bash
dotnet run --project samples/Convoy -c Release -- --dataset datasets/convoy-j --arms J --seeds 100-299
```

## When something goes wrong

| what you see | what it means |
|---|---|
| `needs an API key in one of these environment variables: …` | set one of the named variables; the command makes no call without it |
| `no model '…' in the catalog (…); known: …` | the name is not in the catalog in use; check `--models-file` or add a row |
| the trucks mostly wait | the model's answers are being refused; read `calls.jsonl`: `Valid` is false for each refused answer |
| a connection error to `localhost:11434` | Ollama is not running: start it, or `ollama serve` |
| a run takes very long | a local model is slow; shorten the run with `--ticks`, or use a hosted model |

## Results

The first runs, 2026-10-28, seed 1 cut to 8 ticks, on the owner's laptop:

| seat | delivered | answers refused | model time |
|---|---|---|---|
| the offline planner | 15 units | none | none |
| `qwen2.5:7b`, calling functions | 5 units | 12 of 24 | 371 s |
| `qwen2.5:7b-json`, answering in JSON | 0 units | 0 of 24 | 699 s |

The JSON protocol made every answer valid but not useful. The full account is in
[design-machine-vessels.md](design-machine-vessels.md), "First runs with a model"; how a trained model could do
better is in [design-trained-models.md](design-trained-models.md).
