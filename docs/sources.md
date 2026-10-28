# Sources

Creatura's designs are the author's own readings of other people's public work. The people named here did not
take part in Creatura and do not endorse it. Nothing in this repository is copied from their talks or books: the
code and the notes describe their ideas in the author's words and say where each idea comes from.

## Conversations

Comments in the code anchor a design decision to the moment in a conversation where the idea comes up, written as
a timestamp, for example `(01:21:27)`. Each timestamp refers to one episode of the Lex Fridman Podcast:

| person | episode | where it shows up |
|---|---|---|
| Joscha Bach | Lex Fridman Podcast #392 | the self-model, coherence, stages, predictions (`SelfModelRegulator`, `SelfModel`, `Coherence`, `Stages`, `PredictionTracker`) |
| Michael Levin | Lex Fridman Podcast #486 | the colony, stress shared between levels, goal states and the cognitive light cone (`ColonyRegulator`, `Control`) |
| George Hotz | Lex Fridman Podcast #387 | the critic with no hands: claims, routes, the interrupt budget (`Creatura.Hotz`) |

The author also listened to Joscha Bach's earlier conversations on the same podcast (#101 and #212) while the
framework began; the code cites #392.

## Books

| work | where it shows up |
|---|---|
| Jordan Peterson, *Maps of Meaning: The Architecture of Belief* (1999) | the map-keeper (`MapKeeperRegulator`, `Story`, `Distrust`) and the orienting agent (`Creatura.Orienting`); "Figure n" in the comments is the book's figure |
| Evgeny Sokolov's work on the orienting reflex and habituation | the orienting agent and its hypotheses (S1 to S7) |
| Robert Kegan's stages of adult development, as Bach describes them | `Stages` |

## Design notes referred to by name

Some comments name design notes such as `design-framework.md`, `design-sokolov.md`, `design-batteries.md` and
`design-peterson.md`. These were written in a private research repository where the framework was first worked
out, and are not part of this repository. The decisions they record are carried by the code, its tests and the
notes in `docs/`.
