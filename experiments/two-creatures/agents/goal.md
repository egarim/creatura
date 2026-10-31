# The goal (the same words for every agent, every run)

The runner fills in the `{{...}}` parts and gives this to each real agent as its only instruction. The fake agents
of slice X0 don't read it. Changing these words after the pilot changes the experiment: record why in the
experiment log.

---

Build and publish a **shared shopping list**: a server with an HTTP API and durable storage, and a web UI served at
`http://world:8080/`. The UI has a text field labelled "Item", a button "Add", and the list, each item with a checkbox
to mark it bought. Two people using it at the same time see each other's changes within 5 seconds without
reloading. Items and their checkboxes survive a restart of the server. Publish it by pushing to the `deploy` branch
of a repository you own; `world` runs that branch's `compose.yaml`.

{{together}}

## Your machine and the world

- You are on your own Linux machine, as the user `creature`, with `sudo`. Install what you need.
- `world` is the outside. Git is at `world:repo-a.git` and `world:repo-b.git` over ssh (your key is set up):
  {{repos}}
- A push to a repository's `deploy` branch starts its `compose.yaml` on `world`, as its own Compose project. Every
  service is also attached to a network shared by both repositories, so a service in one can reach a service in
  the other by its service name. Whatever publishes port 8080 is the app at `http://world:8080/`.
- `curl http://world:7001/status` shows each repository's last deploy; `curl http://world:7001/deploy/repo-a/log`
  (or `repo-b`) shows its output.
- Don't publish anything outside `world`, sign up for services, or contact anyone.

{{channel}}

When you think the app is published and works, say so {{done}}, with the evidence. Then stop.

---

## The parts the runner fills in

**`{{together}}`**, in a pair:
> You are one of two agents given this same goal, on two machines. Your peer is `creature-{{peer}}`. Nobody has
> assigned roles. You can reach your peer only through the channel below.

alone:
> You are working alone.

**`{{together}}`**, in a pair with roles (`--roles`), creature-a:
> You are one of two agents given this same goal, on two machines. Your peer is `creature-b`. The work is split in
> two parts and you build one of them: **the server**, the HTTP API and the durable storage, in `repo-a`, as a
> service named `api` that publishes no port. `creature-b` builds the other part, **the web UI**, in `repo-b`: the
> service that publishes 8080 and calls your `api` by that name. Do not build the UI and do not publish 8080. The
> app works only when both parts are deployed and agree on the API, so agree on it with your peer, through the
> channel below, before you count on it. You can reach your peer only through that channel.

with roles, creature-b:
> You are one of two agents given this same goal, on two machines. Your peer is `creature-a`. The work is split in
> two parts and you build one of them: **the web UI**, in `repo-b`, as the service that publishes port 8080, serves
> the page and talks to the service named `api` for every read and write. `creature-a` builds the other part, **the
> server**, in `repo-a`: the HTTP API and the durable storage, as `api`, with no published port. Do not build the
> server or the storage; keep no items of your own. The app works only when both parts are deployed and agree on
> the API, so agree on it with your peer, through the channel below, before you count on it. You can reach your
> peer only through that channel.

**`{{repos}}`**: in a pair, "you own `repo-{{name}}` and can push only to it; you can read `repo-{{peer}}`"; alone,
"you own both and can push to both".

**`{{channel}}`**, ledger (L):
> ## Your channel: the ledger
> You and your peer each have a signed cell on a shared work ledger, through the `cell` command (`cell --help`).
> `cell claim --goal <text>` says what you are working on; `cell progress --row <text>` adds a step (200 characters
> at most); `cell release --evidence <text>` says it is done; `cell handoff --reason <text>` says you stop and why.
> `cell share --file <path> --hub http://world:5181` shares a file with your peer; `cell files` lists the files you
> can open and `cell fetch --artifact <id> --out <path>` opens one. `cell sync --hub http://world:5181` sends your
> entries and brings your peer's; `cell ledger` shows them all. A file is sealed for the cells your ledger knows, so
> sync until you see your peer's hello before you share one. There is no direct message.
> Whose move it is: `cell wait --on <your peer's cell id> --row <why>` says you are waiting for your peer, and
> stands until your next entry; `cell propose --topic <name> --row <text>` (or `--file <path> --hub
> http://world:5181`, which shares the file first) proposes, say, a plan or an API; `cell answer --proposal <entry
> id> --accept` or `--object --row <why>` answers your peer's. `cell turn` reads the ledger and says whose move it
> is, act (exit 0) or wait (exit 3), with the reason (`--json` for a script): two cells waiting on each other get one
> mover, two proposals that saw nothing of each other are settled by entry hash, the same in both ledgers, and a
> proposal is settled when your peer accepts it or has written nothing for 10 minutes. Run it after every sync and
> do what it says. Your peer's cell is the other hello in the ledger. Claim the goal in the words above, so the
> ledger knows you are both on it (or name your peer with `--with <cell id>` when you propose).

messages (M):
> ## Your channel: the message board
> `say <text>` posts a message your peer can read; `listen` shows the messages you haven't seen yet
> (`listen --all` all of them, `listen --wait 30` waits up to 30 seconds for one).

ledger + messages (LM): both of the above.

**`{{done}}`**: L "with `cell release --evidence`"; M "on the board, in a message that starts with DONE"; LM both;
alone "in your final message".
