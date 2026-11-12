# Creatura

An agent framework built from the regulation patterns living things use to keep themselves on course: one loop, a control layer between the model and its tools, and worlds that can lie. The code, the tests and the
full README are at [github.com/egarim/creatura](https://github.com/egarim/creatura).

## Anatomy

**[The anatomy plates](anatomy.html)** draw Creatura in five plates: the project's icon with each part named for
the part of the framework it stands for; the parts and who talks to whom; one tick of the loop; the mesh; and the
agent tools around it, as hosts and as cells.

## Design notes

- [Machine vessels acting together](design-machine-vessels.md): the convoy, its arms and the battery.
- [Trained models for a world](design-trained-models.md): the recorder, the policy network, training by trial and
  the learned judge, each pre-registered before it was built.
- [Identity, vessel and the cell network](design-cells.md): cells, the signed work ledger and the hub.
- [Sharing files between cells](design-shared-files.md).
- [Handing work from one session to the next](design-handoff.md): a hand-off with notes, a claim that continues it,
  shown to the person at the next session's start; Codex sessions as cells through its hooks or its notify command
  (H1 and H2 built).
- [The console](design-console.md): where a person watches the creatures work, Fleet, Run and Compare;
  `creatura-console` shows a run tick by tick (slice U1).
- [Ratings](design-ratings.md): a Weng-Lin rating per seat (model, design, prompt) read from the rows the bench, the
  matrices and the two-creatures runs write, with its uncertainty and whether more runs would change the order (R1
  built; R2 to R4, cell reputation, a barrier curriculum and memory credit, designed).
- [The control layer on the phone](design-mobile.md): a TypeScript port beside an on-device model, for Expo apps,
  and a router, world model and adapter trained from big models in the app's emulated world (S1, the port, and T0, the
  maths tutor's emulated world, built; the rest proposed).
- [The operating system as the creature's body](design-embodiment.md): senses, limbs, inner senses, set points and a
  skin on Windows, Linux and macOS, with Telekinesis as senses and limbs (proposed, nothing built).
- [Two creatures build and publish an app](design-two-creatures.md): an experiment with two agents on their own VMs,
  three pairings and three channels, judged by an outside test (slice X0, the local harness, built).
- [Turns, so two cells neither wait on each other nor both lead](design-turns.md): waits, proposals and answers in
  the ledger, and `creatura cell turn`, which says whose move it is, the same in every home (T0 built; T1, the fake
  pairs with no roles, built).
- [A language for agents to talk in](design-agent-language.md): Latin 1 r0, ten typed messages whose claims carry
  checks the world runs, decoded into the owner's language, with a digest for the person in the loop (v0 built as the
  two-creatures arm MT, with fake agents; issue #47).
- [AXI with Creatura](design-axi.md): the Adaptive Experience & Intelligence Framework's agent side as an example
  and a sample world, a virtual TikTok with synthetic users whose truth is known, and a pre-registered thesis that
  compares what the recommender learns with that truth (proposed; issue #62).
- [Web worlds](design-web-worlds.md): a persona uses a real web app through Playwright, sees only the visible text
  and the numbered controls, and is judged by the app's own data; barriers between the browser and the app (phase 1
  built, with an offline demo).
- [Setting up a model for the convoy](convoy-models.md).
- [The replay format](replay-format.md): how a UI watches a run.
- [Sources](sources.md): whose public work the designs read, in the author's words.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md).
