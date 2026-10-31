# Shared shopping list — web UI (repo-b)

Serves the page on port 8080 and proxies every read and write to the `api` service
(`http://api:8080`, configurable with `API_BASE`).

- `GET /items`, `POST /items`, `PATCH /items/{id}` are forwarded verbatim to `api`.
- `GET /health` reports whether `api` is reachable.
- The page polls `GET /items` every 2 s, so two people see each other's changes within 5 s.
