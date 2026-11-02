# Demo: the app the two creatures built (run DD-LM-R, 2026-10-31)

`repo-a` is creature-a's server (service `api`, storage on a volume), `repo-b` is creature-b's web UI (port 8080).
Both are exactly what the agents pushed; only `compose.yaml` here is ours, it puts the two services on one network.

```bash
docker compose up -d --build
```

Open http://localhost:8080 in two windows: an item added or ticked in one shows in the other within 2 s.
Stop with `docker compose down -v`.

Record: `../records/2026-10-31/2026-10-31T14-38-20-DD-LM-R/` (transcripts, ledger, board, judge checks, bundles).
