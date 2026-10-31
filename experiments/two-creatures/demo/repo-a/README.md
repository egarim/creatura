# Shopping list — API (repo-a)

Service `api` (no published port; reachable on the shared compose network as `http://api:8080`).

- `GET /health` -> `{"ok":true,"items":N}`
- `GET /items` -> bare array `[{"id":1,"text":"milk","bought":false}, ...]`
- `POST /items` `{"text":"milk"}` -> 201 item object; 400 if text is empty
- `PATCH /items/{id}` `{"bought":true}` / `{"text":"..."}` -> 200 item object; 404 if unknown
- `DELETE /items/{id}` -> 204
- `GET /events` -> SSE, `event: items` with the same bare array on connect and after every change (+ heartbeats)

Storage: JSON file at `/data/items.json` on the named volume `api-data`, fsynced and atomically
renamed on every change, loaded on start — items and bought flags survive restarts and redeploys.
