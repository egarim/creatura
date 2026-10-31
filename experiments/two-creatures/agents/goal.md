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

messages (M):
> ## Your channel: the message board
> `say <text>` posts a message your peer can read; `listen` shows the messages you haven't seen yet
> (`listen --all` all of them, `listen --wait 30` waits up to 30 seconds for one).

ledger + messages (LM): both of the above.

**`{{done}}`**: L "with `cell release --evidence`"; M "on the board, in a message that starts with DONE"; LM both;
alone "in your final message".
