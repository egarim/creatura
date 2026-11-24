# The status file: show a program in the tray

A program that works on this computer puts one file in Creatura's home, `status/<name>.json` (default `~/.creatura/status/`), and
`creatura-tray` shows it. UTF-8. Write it whole to a temporary name in the same folder (not one
ending in `.json`, except `.tmp.json`) and rename it over the old one. Overwrite it when
something changes, and often enough not to be stale; keep no history in it. Put no secret in it.

```json
{ "format": 1, "name": "Vivarium", "state": "working", "line": "T-FW-06, building, 12 min", "updatedAt": "2026-11-24T14:00:00Z",
  "staleAfterSeconds": 180, "waiting": 0, "lines": ["Opus 5.5, limit 60 min"], "link": "https://viv.os.sv" }
```

| Field | Meaning |
|---|---|
| `format` | 1. Required. |
| `updatedAt` | When it was written, with `Z` or an offset (read as UTC without one). Required. |
| `name` | Shown; the file's name when missing. |
| `state` | `stopped`, `idle`, `working` (green dot) or `attention` (counts at least one in the badge). |
| `line` | What it is doing. |
| `lines` | Up to 10 more lines. |
| `waiting` | How many things wait for a person: in the badge. |
| `staleAfterSeconds` | Older than this is "not answering since ..." (180 when missing or 0, a day at most). |
| `link` | `https`, or `http` on 127.0.0.1 or localhost; opened by "Open" inside the program's line. |

The tray reads at most 10 files of 64 KB and shows text of 100 characters a line. It writes nothing there and runs nothing from it.
