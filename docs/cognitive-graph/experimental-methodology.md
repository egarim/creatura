# The cognitive graph benchmark: methodology

Issue [#110](https://github.com/egarim/creatura/issues/110). Code: `src/Creatura.Graph.Bench`. Runner:
`dotnet run --project samples/GraphBench`. Results: [results.md](results.md), raw rows in
`experiments/graph/`.

This document is the pre-registration. It says what was fixed before the run, what the run was allowed to
conclude, and — because a methodology that only describes its own strengths is advertising — what it cannot
conclude.

## What is actually being measured

Four memory architectures, given the same corpus, the same question, the same budget and the same answerer, and
differing only in how they store and retrieve.

| arm | memory | retrieval |
|---|---|---|
| A `none` | nothing | nothing |
| B `text` | the corpus lines as a bag of words | TF-IDF against the question's terms |
| C `graph` | `InMemoryGraphStore`, built by `KnowledgePipeline` | bounded walk seeded on the question's own terms |
| D `hybrid` | both | C first, B filling whatever budget is left |

**There is no language model in this harness.** The answerer (`Answerer`) is one deterministic function shared by
all four arms. It reads only the text an arm handed back, in the corpus's own line notation, and returns an answer
or one of three refusals (`unknown`, `unresolved`, `none`). Arm C hands back graph edges rendered as the same
lines arm B hands back raw, so structure cannot help at answer time — only at retrieval time, which is the thing
under investigation.

That is a deliberate narrowing. It means these results are about **retrieval**, not about reasoning. What an agent
does with retrieved context when a model is reading it is a different experiment. It has now been run — twice, on
one fixture that discriminates and one that does not — and is reported in
[results.md](results.md#the-model-in-the-seat), with its own threats to validity.

## Fixed before the run

- **Pass rule.** `answer correct AND evidence recall 1.0 AND zero violations`. Requiring the evidence as well as
  the answer is what stops an arm passing by luck; requiring zero violations is what stops an arm passing by
  leaking.
- **Improvement threshold.** An absolute pass-rate difference of **0.25**. A tie is not an improvement.
- **Context threshold.** "Reduces token consumption" means at least **10%** fewer mean retrieved characters.
- **Decision gate** (from #110): zero critical security violations, where the critical fixtures are G09 and G10.
- **Corpus size.** 60 irrelevant statements per scenario (180 for G12), so the answer is roughly 8% of what is
  known. A fixture where the whole corpus fits in the budget measures nothing.
- **Budget.** 12 items, the same for every arm on every scenario.
- **Seed.** 20261125, published, and the only source of randomness.

## Fairness controls

- Every arm is handed the identical corpus, including statements that belong to another tenant (G10) and
  statements planted to mislead (G09). Arm C additionally enforces an access policy, which is one of the things
  under test and not an advantage: an advantage would be extra knowledge, and a policy removes knowledge.
- No arm sees the oracle. The answer is in `ScenarioOracle`, which nothing in the retrieval path can reach.
- The extractor never sees an answer either: it reads the same lines arm B indexes.
- Oracle graphs and extracted graphs are distinguishable by `ProvenanceKind`, and `LineExtractor.Ceiling` caps
  what a corpus may claim about its own reliability by who vouches for it — a line saying `!verified` does not
  make itself verified.
- Build cost is charged to the arm that pays it. `ArmCost` carries learn time, retrieve time, items, characters,
  graph size and traversal depth, and the report prints all of them beside the score.
- Ties break on statement id, and expansion order is by edge id, so a run is reproducible from the seed alone.

## What was corrected after the first run

Disclosed because a pre-registration that quietly absorbs its own corrections is worth nothing. The first run
(seed 20261125, 56 rows) produced A 0/14, B 8/14, C 11/14, D 8/14. Three defects were found in the harness and
fixtures, and all three were fixed before the numbers above were recorded:

1. **A bug in arm C.** Traversal crosses a relationship backwards through its schema inverse, and the arm was
   rendering that reversed form to the answerer — `capability:gui providedBy browser:chromium` when the corpus
   said `browser:chromium provides capability:gui`. The answerer, being shared and deliberately dumb, could not
   read it, so arm C lost G02 and G07 for a reason that had nothing to do with memory. Fixed to always render the
   stored direction. This is the only change that could have flattered the graph arm, and it moved G02 and G07
   from fail to pass for C *and* D.
2. **G03's oracle over-reached.** It forbade retrieving another service's ownership. The issue's oracle for G03 is
   "correct owner at the requested timestamp"; an irrelevant but true statement is a distractor, not a disclosure.
   `Forbidden` removed. This made arm B pass G03 and turned H03 from held to failed.
3. **G05's required evidence over-reached.** It required the migration's status as well as the dependency. The
   answer follows from the dependency alone, and the answerer presumes "blocked" when the status is unknown, which
   is the safe direction. Narrowed to the dependency. This made arm B pass G05 and turned H04 from held to failed.

Two of the three corrections made the graph arm look worse. That is the only reason to believe the list is
complete.

Two fixture changes were made *before* any run and are noted for the record: G09's injected prose and G10's
secret canary were rewritten to contain the question's own terms, because an injection or a secret that no
retriever would ever surface is not a test of anything.

## Sample size, variants, and why there are no confidence intervals

`Variants.Of` permutes the interchangeable names a fixture declares (`person:oncall-dana` with
`person:oncall-wei`, and so on), seeded, so a variant is reproducible and checks that a result is not an artifact
of one entity's spelling.

The 8-variant run (448 rows, `experiments/graph/stability/`) reproduces the primary run exactly: 0%, 71%, 93%,
79%, and the same thirteen verdicts. That is the point of the check and also its limit — **name permutations are
not independent samples.** Identical pass rates across variants mean zero within-scenario variance, so a
confidence interval computed over 112 runs per arm would be spuriously tight and is not reported.

The honest sample size is **14 scenarios**, one run each in the primary result. Issue #110 asks for at least 20
variants per scenario; that is not met, and meeting it properly needs independently generated corpora — different
graph shapes, different distractor densities, different chain lengths — rather than the same corpus with the
labels changed. Paired effect sizes are reported as plain differences with n=14 and no interval attached.

## Threats to validity

1. **The answerer is the ceiling.** Anything the answerer cannot express, no arm can score. Multi-hop, single-hop,
   temporal, exclusive-relation, cause, choice, action-gate and theme-set questions are expressible; free-form
   synthesis, ranking under ambiguity and partial credit are not.
2. **Arm B is keyword retrieval, not dense retrieval.** There is no embedding anywhere in this repository. A
   vector arm could close some of the gap on G01 and G12, and H09 in particular should be re-tested against one
   before anybody concludes that hybrid retrieval is a dead end.
3. **Arm C's retrieval is unranked breadth-first.** It expands in edge-id order. On G12 a high-degree hub
   neighbouring the seed consumes the entire budget before the answer is reached. A ranked retrieval might fix
   this; adding one now, after seeing the result, would be tuning the experiment to its own answer, so it is
   recorded as a finding and left for the next pre-registration.
4. **No model, no agent, no world.** Nothing here shows that a graph improves an agent's *behaviour*. The
   `ReferenceLoop` integration is tested in `Creatura.Graph.Tests` (a `blockedBy` edge makes the loop abandon its
   habit and still reach the goal, offline), but it is not benchmarked across arms.
5. **Timing is wall-clock on one machine**, in milliseconds, with the OS scheduler running. Treat the cost columns
   as ratios, not as measurements.
6. **The corpus is synthetic and small.** 60 to 180 statements. Real knowledge bases are larger, messier and have
   entity-resolution problems this harness does not have, because every entity is named exactly once.

## Reproducing

```sh
dotnet run --project samples/GraphBench                                    # the primary run: 56 rows
dotnet run --project samples/GraphBench -- --variants 8                    # the stability run: 448 rows
dotnet run --project samples/GraphBench -- --only G01,G12 --variants 4     # two scenarios
dotnet run --project samples/GraphBench -- --seed 99 --out /tmp/other      # another seed, another folder
dotnet run --project samples/GraphBench -- --gate                          # what demand-gating costs; no model, no key
dotnet test tests/Creatura.Graph.Bench.Tests                               # the harness, not the result
```

The model-in-the-seat runs are a second sample, because they are neither free nor reproducible:

```sh
dotnet run --project samples/GraphModelBench -- --latches --model deepseek-chat --repeats 6
```

That needs `DEEPSEEK_API_KEY` and reads its own `models.local.json` through `ModelCatalog.Use` — never
`samples/models.json`, which is generated from `ModelCatalog.Rows` and pinned by a test counting exactly thirteen
seats, so adding a model there breaks unrelated tests. Its wall-clock seconds are one machine's and one paid
provider's; read the counts, not the timings.

The exit code is the decision gate: non-zero when an arm surfaced knowledge it should not have. It is non-zero on
the current fixtures, because arms B and D do — which is a finding, not a broken build.

`experiments/graph/results.jsonl` is the raw primary run, one JSON object per row, redacted through the ledger's
`Scrubber` before it is written. `experiments/graph/report.md` is generated; `results.md` beside it is
hand-written and is never overwritten by the runner.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md).
