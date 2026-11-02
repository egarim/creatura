# Design: ratings, so the runs say who is best and when to stop running

**Status:** proposed 2026-11-02 (issue #28, from the owner through another session); slices R1 and R2 built the same
day, results at the end. R3 and R4 are designs.

## The problem, in the owner's words

Creatura now has many things that can sit in a seat: the planner, a dozen catalogued models, five designs, personas
written for an app, prompts; and many things that test them: the bench's batteries, the convoy, the web-world
matrices, the two-creatures runs. Each writes its own rows and reports, and each report answers its own question
well. What none of them answers is the owner's: **which seat is best, how sure are we, and do we need more runs?**
Today that is read by eye across files, and a new model or design means re-reading everything. The owner asked for
ratings: one number per seat with its uncertainty, updated from the reports that already exist, a leaderboard, and a
plain statement of whether more runs would change the order. Then, in later slices, the same idea turned on the
cells (a reputation in the ledger), on the worlds and barriers (an opponent's rating, so the next barrier is picked
near the agent's level), and on memories (credit for what helped, without TrueSkill).

## Three principles carried over from the framework

1. **The rows are the record; the rating is a reading of them.** A rating is recomputed from every row each time,
   in a fixed order, so two people with the same files get the same leaderboard, and no rating file can drift from
   the rows. Nothing in the rows changes.
2. **Compare only what met on the same job.** A seat is rated against the seats that ran the same job: the same
   world or task, the same barriers and environment, with the other seats held fixed. A model is never rated against
   a design, and a run on an easy world never counts against a run on a hard one. A seat that met nobody keeps the
   prior and the leaderboard says so.
3. **Say what the numbers cannot.** A leaderboard that cannot tell two seats apart says which pair is open and that
   more runs are needed between them; one that can says the order is settled. The judge of a run stays the run's own
   (the data, the outside test); the rating only orders the verdicts.

## The rating

**Weng-Lin** (Weng and Lin, 2011, *A Bayesian approximation method for online ranking*), the open TrueSkill-style
model, in its Bradley-Terry full-pairing form: each seat is a Gaussian belief (μ, σ), a game ranks several seats at
once, and every pair in the game updates both. With β the spread of a performance around its mean and
c = √(σᵢ² + σⱼ² + 2β²), the probability that i beats j is pᵢⱼ = e^{μᵢ/c} / (e^{μᵢ/c} + e^{μⱼ/c}), the observed
result sᵢⱼ is 1, ½ or 0, and after a game μᵢ += Σⱼ (σᵢ²/c)(sᵢⱼ − pᵢⱼ) and σᵢ² *= max(1 − Σⱼ (σᵢ²/c²) γ pᵢⱼ(1 − pᵢⱼ), ε)
with γ = σᵢ/c. Defaults μ = 25, σ = 25/3, β = 25/6, ε = 0.0001; the **ordinal** a leaderboard sorts by is μ − 3σ, a
conservative estimate. No pairing with a central server, no proprietary parameters, and a tie is a result, not a
missing one.

**A job** is everything about a run except the axis being rated: the source and the world or task, the barriers or
arm, the environment, and the other seats. **A contest** is the runs of one job; its games are formed round by
round: the first run of each seat on the job is game 1, the second run of each is game 2, and a seat with fewer runs
sits out the later games. A game's ranking is the job's own order of outcomes:

| source | rows | the order (best first) |
|---|---|---|
| the bench (`scoreboard.csv`, `results.csv`, `Row`) | one row per run | `Real` (done, by the registry) > `HandedOff` (stopped honestly) > `Open` (ran out) > `Claimed` (said done, was not); among equals, fewer ticks |
| a web-world matrix (`report.json`) | one cell per run | done with no uncaught lie > done > not done and honest > said done and was not (`FalseDone`); among equals, fewer ticks |
| two creatures (`summary.json`) | one record per run of a pair | score 0 to 5, then fewer false claims, then the earlier first ship |

**Three axes** are rated, each with its own leaderboard: the **model** (the bench's `seat` and `model`, the matrix's
tester, the pair's agents), the **design** (the bench's `agent`, the matrix's design), and the **prompt** (the
matrix's persona; the bench and the pair have none yet). Rating the designs holds the model fixed in the job, and the
other way round.

**Settled or open.** Two seats adjacent on a leaderboard are told apart when the Bradley-Terry probability that the
higher beats the lower, with their current μ and σ, is at least 0.95. A leaderboard where every adjacent pair is told
apart says *the order is settled*; otherwise it names each open pair, with how many games the two have played, and
says more runs are needed between them. This is the first slice's answer to "when more runs are not needed"; a
calibrated stopping rule (how many more, at what cost) is an open point below.

## Slices

| slice | what | passes when |
|---|---|---|
| R1 | `Creatura.Ratings`: the Weng-Lin update, contests from the three sources, three leaderboards with uncertainty and the settled-or-open verdict; `creatura rate` | G1 to G5 (built) |
| R2 | cell reputation: a rating per cell identity written into the ledger after each shared job (shipped, false claims, contract kept), as an entry of the experiment owner's cell, signed by the owner key; `cell turn` may break a tie by reputation instead of the entry hash when both parties' ledgers agree on it; who may rate whom: the owner of the experiment rates the cells that ran it, a cell never rates itself or its peer | G6 to G8 (built) |
| R3 | barrier curriculum: worlds and barriers as opponents with ratings of their own, updated from the same games (a barrier "wins" when the seat loses); the next barrier for a seat is the one rated nearest its level; applied to web worlds' `--barriers` and, if a game-worlds note exists by then, to it | G9 to G11 |
| R4 | memory credit, not TrueSkill: per memory (a lesson, a record's case), how often it was retrieved and how often the run then ended well; prune what is retrieved and never helps, promote what helps; applied to the web-world persona memory (`UxMemory`) | G12 to G14 |

R1 and R4 are independent; R2 and R3 need R1. No paid model call is needed by any of them: every slice reads rows
that exist.

## Pre-registered tests, written before the code

| id | claim | falsifier | status |
|---|---|---|---|
| G1 | the Weng-Lin update behaves: after one game between equals the winner's μ rises by what the loser's falls, both σ shrink, a tie between equals moves nothing but shrinks σ, σ never grows, and a worked example matches the published formulas to 1e-9 | any number off | holds |
| G2 | a contest holds only runs of one job with the other axes fixed: a model is never ranked against a design, two worlds never mix, and a seat that met nobody keeps the prior and is marked so on the leaderboard | a game across jobs, or an unmet seat with a changed rating | holds |
| G3 | a seat that wins every game against another ends above it with the pair settled; alternating wins leave the pair open, and the leaderboard names it and says more runs are needed between the two | a settled pair with alternating wins, or an open pair with a sweep | holds |
| G4 | the same files given in any order, and the same rows split across files, give the same leaderboard | any difference | holds |
| G5 | `creatura rate` reads the three sources (the bench's two CSV shapes, a matrix's `report.json`, a pair's `summary.json`), prints the three leaderboards with μ, σ, the ordinal, games played and the verdict, and `--json` round-trips the same numbers | a source unread, a number missing, or a difference between the text and the JSON | holds |
| G6 | (R2) after a shared job the owner's cell writes one reputation entry per cell that ran it, signed by the owner key; a cell cannot write one for itself or its peer, and a ledger refuses one not signed by an owner it trusts | a self-rating accepted, or an unsigned one | holds |
| G7 | (R2) `cell turn` with reputation on breaks a competing-proposal tie by the higher reputation when both ledgers hold the same entries, and by the hash as before when they do not | a tie broken differently in the two homes | holds |
| G8 | (R2) a reputation rises with shipped runs and no false claims and falls with false claims, by the same update as R1's | anything else | holds |
| G9 | (R3) a barrier's rating rises when seats lose under it and falls when they win, from the same games that rate the seats | anything else | proposed |
| G10 | (R3) the next barrier picked for a seat is the one whose rating is nearest the seat's, never one already settled far above or below it | a farther barrier picked | proposed |
| G11 | (R3) a matrix run with `--barriers next` runs the picked barrier and the report says why it was picked | anything else | proposed |
| G12 | (R4) every retrieval of a memory is counted, and every run that retrieved it is credited to it as ended well or not | a retrieval uncounted | proposed |
| G13 | (R4) a memory retrieved often and never followed by a good run is pruned; one followed by good runs more than its persona's base rate is promoted (shown first) | anything else | proposed |
| G14 | (R4) the web-world persona memory applies it: the lessons a persona is shown are the promoted ones first, and a pruned one is not shown | a pruned lesson shown | proposed |

## Decisions for the owner

| | decision | options | recommendation |
|---|---|---|---|
| G-D1 | the rating model | (a) Elo; (b) Glicko-2; (c) Weng-Lin Bradley-Terry full pairing | **(c)**: multi-seat games and ties for free, an uncertainty to print, open formulas |
| G-D2 | the score of a run | (a) the outcome class alone; (b) the class, then ticks; (c) the class, then cost | **(b)**; cost is reported beside the leaderboard, not folded in, since the owner reads price and skill apart |
| G-D3 | "settled" | (a) adjacent pairs told apart with p ≥ 0.95; (b) a fixed gap in ordinals; (c) a bootstrap over the games | **(a)** first; (c) when the leaderboards are read for money |
| G-D4 | where ratings live | (a) recomputed from the rows each time; (b) a ratings file updated run by run | **(a)**, principle 1; (b) only if the rows ever grow too large to read |
| G-D5 | the rated axes | (a) model, design, prompt; (b) also the extensions set and the protocol | **(a)** first; (b) when a matrix varies them |

## Open points

- A stopping rule with a cost: how many more games between an open pair would settle it at what price, from the
  seats' prices in the catalog.
- A run's secondary measures (cost, tokens, seconds) as their own leaderboards, read beside skill.
- Whether the convoy's battery (`ConvoyMeasures`, per arm and seed) becomes a fourth source; its arms mix design and
  seat, so the job would be the seed and the arm's parts the axes.

## Results (R1, 2026-11-02)

Built as `src/Creatura.Ratings` (package-free, references Creatura and Creatura.Bench): `Rating` and `WengLin`
(the update), `Contest` (a job's games from runs), `Sources` (the bench's two CSV shapes through `LegacyCsv`, a
matrix's `report.json` and a pair's `summary.json` read with the JSON reader alone, so the package needs no browser),
`Leaderboard` (the three axes, the verdict, text and JSON); `creatura rate` in the CLI. G1 to G5 hold:
`RatingsTests` (6, no files beyond fixtures the tests write). One thing the checks taught: with β = 25/6 a sweep of
12 games between two seats leaves p ≈ 0.90, under the 0.95 bar, and 40 settle it; so "more runs are not needed" is a
demanding verdict by design, and the stopping rule with a cost (open points) is worth having. The first real rows:
`creatura rate experiments/two-creatures/records` over the seven runs of 2026-10-31, in the log (#87). The research
rows live on the owner's other machine and the laptop keeps no matrix `report.json` yet; `creatura rate <those
folders>` is the next thing to run.

## Results (R2, 2026-11-02)

**What a reputation is.** A `Reputation` entry (`CellReputation`): the rated cell, the job, μ, σ and how many jobs
so far, what the job showed (shipped, the false claims, the contract kept), and the owner's signature over all of it,
carried by one of the owner's cells like an offer. A ledger refuses it when the owner inside is not one it trusts, when
the signature does not verify, when the carrying cell is the rated one, or when the carrying cell holds a claim: **a
worker never rates**, which is the rule for "itself or its peer" in one line, since the creatures are the cells with
claims and the owner's cell has none.

**Who rates, and how.** `creatura cell rate --job <text> --results <file.json>` on the owner's vessel reads
`[{"cell", "shipped", "falseClaims", "contractKept"}]`, ranks the cells (shipped, then fewer false claims, then the
contract), applies R1's Weng-Lin update to each cell's latest reputation (the prior for a first job) and writes one
entry per cell; `cell reputations` lists the latest per cell, best first. The two-creatures runner does it at the end
of every ledger arm from the run's own verdicts (shipped; a creature's false claims; the contract kept when a plan was
accepted and the app shipped), and keeps the lines in the record as `reputation.jsonl`. This run's creatures never see
them, since their world is torn down; the later arm that carries the owner's ledger into the next run (X-D11) will.

**The tie-break.** Among competing proposals, `turn` now picks the proposer with the higher reputation when every
competitor's cell was rated **before any of them proposed** (so every home that holds those entries reads the same)
and one stands above the rest; else the hash, as before. The topic says which (`liveBy`), and T6's reason names it.
A rating written after the proposals, or held by one home only, changes nothing: both homes still pick by hash.

G6 to G8 hold: `ReputationTests` (3). The runner: the free pair CC-LM with fake,liar (log #88) ends with `rated 2 cells` and its record's `reputation.jsonl` holds two owner-signed entries, the honest creature rank 1 at μ 27.64 and the liar rank 2 at μ 22.36 with its 2 false claims, both shipped, neither with a contract kept (the fake pair's roles are written in, so no plan was proposed). Not run: a pair whose next run starts with the
reputations in its ledgers, which is the arm that would show whether a rated pair settles faster.

## What this note does not cover

Rating a world or a barrier as an opponent (R3), a cell's reputation in the ledger (R2), memory credit (R4): each
is a slice above with its own checks. A rating of a person.
