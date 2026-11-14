# AxiAgent: Creatura's agent for Singular (AXI, #91)

Creatura's half of AXI slice 1 (Singular ADR-0108 §7 steps 3-4), over HTTP only; it references nothing of Singular.
For each tenant it:

1. polls `GET /api/app/experience/agent/proposals?state=accepted` at most once every 10 s (longer when a 429 says
   `Retry-After`);
2. reads each `suggest-a-group` proposal with `GET …/agent/proposals/{id}`, and stops when it shows `outcome: recorded`
   or is gone. Singular answers 404 `proposal.unknown` for a gone proposal and for its own failed read alike (and
   `agent.only` when its caller check fails), so a 404 ends a proposal only when that cycle's list also left it out;
   otherwise it is read again next cycle;
3. runs `suggest-a-group` once, under Creatura's control layer: one read of `GET /api/app/data/groups?visibility=eq.public`;
   a failed read is reported as `tool.failed`, a read that serves a non-public group as `tool.lied` (and suggests nothing).
   An app with no public groups is a `tool.failed` too (a policy choice): two failure signals on a proposal start
   Singular's person-wide cooldown for the capability, so the agent stops offering a group where none exists;
4. posts its `axi.event` lines and one `axi.outcome` (`proposalId`, `status`, `reported.lies`, at most 5
   `reported.suggested` refs, never `confirmed`) to `POST /api/app/experience/agent`, and re-sends the same lines (same
   `eventId`s) every cycle until the proposal shows `outcome: recorded`. Only a refusal of the lines stops it (HTTP
   400/409/413/422, or a refused item, such as 409 `proposal.not-accepted`); a refused caller (401, 403, 404
   `agent.only`), a 429, a 5xx or a fail-closed 202 (`dropped`) means the same lines next cycle.

The lines live in memory only: after a restart a proposal not yet recorded runs again with new `eventId`s. Singular
stores one outcome per `proposalId` and at most 2 failures per proposal, so nothing is stored twice.

Only ids, codes, numbers and times go on the wire: no `detail`, no subject, no group payload. The replies are read
into records that hold only ids, codes, keys and visibility; the rest of each group row (owner, name) still arrives in
the response, since the data API has no field selection, and is skipped there.


## Run it against staging

The service key is the tenant's named agent account (`Axi:Apps:<app>:AgentAccount`, a non-admin service account).
Set these in the environment, never on the command line:

| variable | what |
|---|---|
| `SINGULAR_URL` | the Singular base URL (staging) |
| `AXI_APP` | the app slug, sent as `X-App-Id` |
| `AXI_TENANTS` | the tenant slugs, comma separated, each sent as `X-Tenant-Id` (at least one; two slugs that map to the same key variable are refused) |
| `AXI_KEY_<TENANT>` | that tenant's service key (tenant slug upper-cased, other characters as `_`), sent as `Authorization: Service …` |

```sh
dotnet run --project samples/AxiAgent
```

It logs to stderr (tenant, proposal id, how each ended); it never prints a key. Ctrl+C stops it. Tests:
`tests/Creatura.Tests/AxiAgentTests.cs`, against a fake Singular built from the shared vectors.
