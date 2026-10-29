# Experiment log

Every run that shaped a result, failures and exploration included, written by `tools/xlog.py` from
`experiments/log.jsonl`. Pre-registered hypotheses live in docs/design-trained-models.md; this log is
everything around them: what was tried, on which seeds, on which commit, and what it was used for.
A commit marked * ran with uncommitted changes on top of it. The log starts with step 6; earlier steps'
runs are recorded only in the design note.

## Step 6

15 entries; 4 touched the battery's seeds 0 to 49.

| # | when | commit | kind | seeds | what | result | used for |
|---|---|---|---|---|---|---|---|
| 1 | 2026-10-29 07:46 | `f75d952`* | check | 0-49 **battery** | Clean battery recorded before any step 6 code (all 18 clean arms) | 900 runs; same report as before | baseline for the no-change check (#2) |
| 2 | 2026-10-29 07:53 | `f75d952`* | check | 0-49 **battery** | Clean battery again with the noise code in, compared with #1 | 87,890 lines, 0 differ; reports identical | T10g first half: no earlier result moved |
| 3 | 2026-10-29 07:54 | `f75d952`* | test | 50-149 | Registered noise-level check: arms U and J on training seeds | U delivered 98.5% (rule: <95% halves), false alarms 7.2% (rule: <=1% doubles); served 36.9 vs J 24.7 | levels kept at 0.10 / 0.15 |
| 4 | 2026-10-29 07:56 | `f75d952`* | data | 50-599 | Training data: arm U, and the policy network sampled at temperature 2, sealed=no | 60,744 + 64,367 transitions | training both judges |
| 5 | 2026-10-29 07:58 | `f75d952`* | tune | 50-599 | Lie judge v0: lies weighted up, 64x64, rate 1e-3 | held-out (seeds ending 9): 54.5% of 77 lies at t1, 15 false alarms, 50 errors; fused check on the same actions: 63.6%, 11, 39 errors | dropped |
| 6 | 2026-10-29 07:59 | `f75d952`* | tune | 50-599 | Four more settings meant to be tried; zsh did not split the options, so all four ran v0 again | four identical copies of v0 | nothing; rerun as #7 |
| 7 | 2026-10-29 07:59 | `f75d952`* | tune | 50-599 | Lie judge settings a-d (a: unweighted 64x64; b: unweighted 128x128; c: weighted 128x128 rate 3e-4; d: unweighted 64x64 rate 3e-4 patience 40) | held-out errors: a 46, b 41, c 50, d 47 (fused 39); lies caught a 59.7%, b 66.2%, c 54.5%, d 58.4% | b kept: fewest held-out errors. Chosen among 5 on the same 77 held-out lies, so its held-out figure flatters it a little |
| 8 | 2026-10-29 08:00 | `f75d952`* | tune | 50-599 | World model retrained on noisy driving; F thresholds by arm Y's recipe | t1 10.82, t2 12.534; 3.9% of held-out lies above t1 | arm F as registered (one setting, no tuning) |
| 9 | 2026-10-29 08:02 | `f75d952`* | probe | 77 | Arms F, P, U, V on training seed 77, one run each | lies: P 76 (73 repeats), F 23, U 2, V 3 | found the lie judge's repeats before any battery run |
| 10 | 2026-10-29 08:03 | `f75d952`* | probe | 77 | Trace of arm P on seed 77: replay events and offline scores of each missed lie | 33 tries on the lying road from the depot, each scored 0.02-0.34 (t1 0.556): reads the depot before and after, gauge full (the high-reading truck) | the diagnosis: per-action odds, no memory |
| 11 | 2026-10-29 08:05 | `f75d952`* | test | 0-49 **battery** | Battery, arms J, U, V, F (the step 6 test run) | T10 held (U false alarms 7.1%, served 35.9 vs 25.5); T10f failed (F missed 96.6%); T10g held; V 1500/1500, 0.40% false alarms | step 6's verdicts |
| 12 | 2026-10-29 08:06 | `f75d952`* | test | 0-49 **battery** | Gate T10b: the lie judge on arm U's battery actions (evaluation set from #11) | 62.7% of 75 lies at t1 (bar 90%), 0.61% false alarms (bar <1%), 45 errors vs fused 32: failed | arm P not run on the battery |
| 13 | 2026-10-29 08:08 | `f75d952`* | probe | 50-149 | Arms P and V on training seeds 50-149, to measure the loop problem | lies P 591 (462 repeats), V 152, U 144; served P 34.1, V 30.1, U 36.9; delivered P 2991, V 2998, U 2956 | the explanation in the write-up |
| 14 | 2026-10-29 08:10 | `f75d952`* | build | — | Eight deliberate breaks, first run | all caught, but restored files kept old timestamps, so later breaks ran on stale broken builds; two tests failed for breaks they did not cover | discarded; script fixed (touch after restore) |
| 15 | 2026-10-29 08:20 | `f75d952`* | build | — | Eight deliberate breaks, rerun clean; one test strengthened first | 8 of 8 caught, each by its own test; 'judge counts against the noisy check' was missed until the test compared counts over six more seeds | the build record in the write-up |

<details><summary>Commands</summary>

- #1: `dotnet run --project samples/Convoy -c Release --no-build -- --battery --record datasets/noisy/baseline-clean.jsonl`
- #2: `--battery --record datasets/noisy/after-clean.jsonl; compare with cell ids scrubbed and new fields removed`
- #3: `--rollouts datasets/noisy/probe-U.jsonl --arms U --seeds 50-149 (and J)`
- #4: `--rollouts datasets/noisy/u-runs.jsonl --arms U --seeds 50-599; --rollouts datasets/noisy/u-sampled.jsonl --arms U --network convoy-policy-j --temperature 2 --sample-seed 6 --seeds 50-599; --world each`
- #5: `uv run tools/train/train_landed.py train-u train-sampled --calibrate train-u`
- #6: `a for-loop over unquoted option strings`
- #7: `train_landed.py ... --unbalanced / --hidden 128 128 / --rate 3e-4 / --patience 40`
- #8: `train_world.py train-noisy --held-out-digit 9; thresholds on train-u seeds ending 9`
- #9: `--arm P --seed 77 --quiet (and F, U, V)`
- #10: `--replay; --record then --world; score with the judge`
- #11: `--battery --arms J,U,V,F --record datasets/noisy/battery-juvf.jsonl`
- #12: `--world u-battery.jsonl eval-u.jsonl --evaluation; train_landed.judge(...)`
- #13: `--rollouts probe-P.jsonl --arms P --seeds 50-149 (and V)`
- #14: `scratchpad breaks.py`
- #15: `breaks.py with os.utime after restore`

</details>

## Step 6b

11 entries; 3 touched the battery's seeds 0 to 49.

| # | when | commit | kind | seeds | what | result | used for |
|---|---|---|---|---|---|---|---|
| 16 | 2026-10-29 09:01 | `a57a143`* | check | 0-49 **battery** | Every earlier arm (A-L, N, R, W, Q, X, Y, Z, U, V, F) replayed with the second attempt's code, compared run by run with step 6's recordings | 1,050 runs, 0 differ | T11g second half: nothing earlier moved |
| 17 | 2026-10-29 09:01 | `a57a143`* | data | 50-599 | Round 0 data, sealed: arm U2, and the policy network sampled at temperature 2 | U2: 16,245/16,500 delivered, 879 lies; sampled: 15,610, 1,018 lies; 62,592 + 74,999 transitions | round 0 of the lie judge |
| 18 | 2026-10-29 09:01 | `a57a143`* | data | 50-599 | Round 0 judge (memory, unweighted, 128x128: step 6's chosen setting, not tuned again) | kept epoch 4; t1 0.615321, t2 0.895806 on U2 held-out; 49.4% of 81 held-out lies above t1 | drives round 1 |
| 19 | 2026-10-29 09:02 | `a57a143`* | data | 50-599 | Round 1: arm P2 drives with the round 0 judge | 16,214 delivered, 4,466 lies (3,718 repeats), served 35.7; judge missed 3,470 of 4,413 lies, 150 false alarms of 31,808; 60,123 transitions | round 1 data |
| 20 | 2026-10-29 09:03 | `a57a143`* | data | 50-599 | Round 1 judge: trained on rounds 0 and 1, thresholds on round 1 driving (seeds ending 9) | 93,669 actions (6,032 lies); t1 0.751424, t2 0.985831; 87.5% of 263 held-out lies above t1 | drives round 2 |
| 21 | 2026-10-29 09:04 | `a57a143`* | data | 50-599 | Round 2: arm P2 drives with the round 1 judge | 16,486 delivered, 1,252 lies (577 repeats), served 31.9; judge missed 282 of 1,187 lies, 124 false alarms of 28,846; 54,282 transitions | round 2 data; the final thresholds are fixed on it |
| 22 | 2026-10-29 09:04 | `a57a143`* | data | 50-599 | Final judge (round 2): trained on all four sets, thresholds on round 2 driving (seeds ending 9) | 122,515 actions (7,219 lies); t1 0.499305, t2 0.972585; 82.4% of 125 held-out lies above t1, 35.2% above t2 | arm P2's judge, fixed before the gate and the battery |
| 23 | 2026-10-29 09:05 | `a57a143`* | test | 50-599 | Gate T11b: final judge vs the fused check with memory, on round 2's held-out actions (seeds ending 9) | 2,912 actions, 125 lies: judge 14 false alarms + 22 misses = 36; fused with memory 9 + 38 = 47: held | arm P2 runs on the battery |
| 24 | 2026-10-29 09:06 | `a57a143`* | test | 0-49 **battery** | Battery, arms J, U2, V2, P2 (the second attempt's test run) | T11 held (U2 7.31%, served 36.2 vs 25.5); T11c held (P2 1500 vs 1466, 31.1 vs 36.2); T11d failed (error rate P2 1.56%, V2 0.79%); T11e failed (repeats P2 43, V2 10); T11f held (0.95%); T11g held | the second attempt's verdicts |
| 25 | 2026-10-29 09:06 | `a57a143`* | probe | 0-49 **battery** | After the verdicts: P2's and V2's repeats split by whether the previous lie on that road was caught (reads the recording of the test run, runs nothing) | P2: 27 repeats after a caught lie (quarantine, then back), 16 after a miss; V2: 0 and 10 | explanation only; no setting changed |
| 26 | 2026-10-29 09:10 | `a57a143`* | build | — | Seven deliberate breaks on the second attempt's code (files touched after each restore) | 7 of 7 caught, each by its own test: sealed offers ignoring the reading, again ignoring the report, export off by one, fused check forgetting, judge given no memory, memory in the wrong inputs, T11e strict | the build record |

<details><summary>Commands</summary>

- #16: `--battery --arms A,...,F --record datasets/sealed/check-old.jsonl; compare with after-clean + battery-juvf, cell ids scrubbed`
- #17: `--rollouts datasets/sealed/r0-u2.jsonl --arms U2 --seeds 50-599; --rollouts r0-sampled.jsonl --arms U2 --network convoy-policy-j --temperature 2 --sample-seed 6; --world each`
- #18: `train_landed.py r0-u2-world r0-sampled-world --calibrate r0-u2-world --unbalanced --hidden 128 128 --memory`
- #19: `--rollouts datasets/sealed/r1-p2.jsonl --arms P2 --judge judge-r0.json --judge-threshold 0.615321 --judge-condemn 0.895806 --seeds 50-599; --world`
- #20: `train_landed.py r0-u2-world r0-sampled-world r1-p2-world --calibrate r1-p2-world --unbalanced --hidden 128 128 --memory`
- #21: `--rollouts datasets/sealed/r2-p2.jsonl --arms P2 --judge judge-r1.json --judge-threshold 0.751424 --judge-condemn 0.985831 --seeds 50-599; --world`
- #22: `train_landed.py r0-u2 r0-sampled r1-p2 r2-p2 models/convoy-judge-sealed.json --calibrate r2-p2-world --unbalanced --hidden 128 128 --memory`
- #23: `uv run tools/train/gate_sealed.py models/convoy-judge-sealed.json datasets/sealed/r2-p2-world.jsonl datasets/sealed/r2-p2.jsonl`
- #24: `--battery --arms J,U2,V2,P2 --record datasets/sealed/battery-j-u2-v2-p2.jsonl`
- #25: `python over battery-j-u2-v2-p2.jsonl`
- #26: `scratchpad breaks2.py`

</details>
