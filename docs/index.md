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
- [The control layer on the phone](design-mobile.md): a TypeScript port beside an on-device model, for Expo apps,
  and a router, world model and adapter trained from big models in the app's emulated world (slice S1, `creatura-js`, built).
- [The operating system as the creature's body](design-embodiment.md): senses, limbs, inner senses, set points and a
  skin on Windows, Linux and macOS, with Telekinesis as senses and limbs (proposed, nothing built).
- [Setting up a model for the convoy](convoy-models.md).
- [The replay format](replay-format.md): how a UI watches a run.
- [Sources](sources.md): whose public work the designs read, in the author's words.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md).
