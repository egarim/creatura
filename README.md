# Creatura

An agent framework for building agents out of regulation patterns taken from how living things and minds
keep themselves on course, as described by the people who studied them. The core is one generic agent: a
loop, a control layer between the model and its tools, and a contract for worlds that can lie. Everything
else plugs in: a design is a regulator that fills the loop's slots, a behaviour is an extension that hooks
into its verbs, and a world is where both are trained and tested.

This repository is the framework's home. The code is being extracted step by step from the research
repository [egarim/biological-agents](https://github.com/egarim/biological-agents), following its
[design-framework.md](https://github.com/egarim/biological-agents/blob/main/design-framework.md), with
golden traces as the gate for every step. The Core arrived here after migration step 5 (the control layer,
the reference loop, the first two regulators); step 6 added the hybrid regulator and the join laws; the
remaining steps continue over there, on the branch `creatura-core`, against this checkout.

## Layout

```
Creatura.slnx
src/Creatura/                 the framework Core: no package references, no project references
  Core/                       the loop, the control layer, worlds, tasks, trace, identity (namespace Creatura)
  Core/Regulators/            SelfModelRegulator, ColonyRegulator, HybridRegulator; Regulators.Join (namespace Creatura)
  Hotz/                       the critic: claims, routes, the interrupt budget (namespace Creatura.Hotz)
  Orienting/                  habituation by match, valence, affect (namespace Creatura.Orienting)
tests/Creatura.Tests/         xunit; the tests that need nothing but the Core and its toy domain
```

```sh
dotnet build Creatura.slnx
dotnet test tests/Creatura.Tests
```

## Building beside biological-agents

The research repository references this one as a **sibling checkout**: its `BiologicalAgents.csproj` points at
`../../../../creatura/src/Creatura/Creatura.csproj`, so the two must sit next to each other:

```
~/Projects/creatura/
~/Projects/biological-agents/
```

Clone both into the same parent folder, build this one first if you like (it has no dependencies either way),
then build the research solution; its golden traces are the gate that the Core here still behaves exactly as
it did there. The research repository's rebuild benchmark also grades the Core modules (`Planner`,
`CoherenceAudit`, `PredictionTracker`, `Table`) from this checkout; `REBUILD_CREATURA_ROOT` overrides the
sibling path.

*Creatura*, Late Latin: that which has been made; first the whole created order, then each made thing.
