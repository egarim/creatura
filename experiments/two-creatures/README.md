# Two creatures build and publish an app

The harness for the experiment in [docs/design-two-creatures.md](../../docs/design-two-creatures.md): two agents on
their own machines get the same goal (a shared shopping list, published to a sandbox host) and no roles, and an
outside browser test judges whether the app shipped.

This is slice **X0**: everything runs in containers on one Docker host, with scripted fake agents. Slice X1 moves the
same three machines to VMs and wires the real agents (Claude Code, Codex, a small local model).

## Run it

Needs Docker, the .NET 10 SDK (to publish the cells CLI and the hub from this checkout) and Node 22.

```bash
experiments/two-creatures/build.sh                                      # the three images
node experiments/two-creatures/runner/run.mjs --reference                # the reference app: must score 5/5
node experiments/two-creatures/runner/run.mjs --reference live           # one feature broken: must fail step 3
node experiments/two-creatures/runner/run.mjs --arm CC-L --agents fake,fake --check-every 30
```

Each run writes `results/<time>-<arm>/`: `summary.json`, every judge check (`checks.jsonl`), the message board, both
creatures' ledgers, both repositories as git bundles, the deploy logs and the agents' logs. `--keep` leaves the
machines up afterwards (`docker exec -it tc-creature-a bash`).

## The machines

| container | address | what it is |
|---|---|---|
| `tc-world` | 100.127.200.10, `world` | the sandbox's outside: git over ssh with push-to-deploy, its own Docker for the published app, the message board on 7000, the deployer on 7001, the ledger hub on 5181 |
| `tc-creature-a`, `tc-creature-b` | .21, .22 | the creatures: Node, git, `sudo`, the `creatura` CLI with a `cell` wrapper, `say` and `listen` |
| `tc-judge` | one-shot | the outside test (Playwright), which no creature can see |

The sandbox network is inside 100.64.0.0/10 because the ledger hub only answers mesh and loopback addresses; on
VMs, give the sandbox a subnet in that range too.

**Who can do what.** Each creature's git key can push only to its own repository and read the other's (`solo`
pushes to both); the runner's key pushes to both. The ledger cells are endorsed by an **experiment owner key**
made fresh for each run, never the real owner's; the hub on `world` holds only its public key. The run's keys
live in `state/<run>/` and are deleted at the end of the run.

**Arms.** The channel is the arm's suffix: `L` starts the hub only, `M` the board only, `LM` both; `C-0` and `X-0`
start one creature that owns both repositories.

## Files

| path | what |
|---|---|
| `build.sh` | publishes the cells CLI and the hub for the Docker host's architecture, builds the images |
| `images/world/` | world: Docker in Docker, sshd, `git-acl.sh` (who may push where), the deploy hook, `deployer.mjs`, `board.mjs` |
| `images/creature/` | a creature's body and its channel tools (`cell`, `say`, `listen`) |
| `images/judge/test.mjs` | the outside test: five steps, two browsers, a restart |
| `reference/` | a reference shopping list split in two repositories, with `BREAK` switches that each break one step |
| `agents/fake.mjs` | the scripted fake agents: server, UI, or both alone |
| `agents/goal.md` | the goal, word for word, for the real agents of X1 |
| `runner/run.mjs` | one run: fresh machines, the agents, the judge, the claims, the record |
