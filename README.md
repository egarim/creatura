# Creatura

An agent framework for building agents out of regulation patterns taken from how living things and minds
keep themselves on course, as described by the people who studied them. The core is one generic agent: a
loop, a control layer between the model and its tools, and a contract for worlds that can lie. Everything
else plugs in: a design is a regulator that fills the loop's slots, a behaviour is an extension that hooks
into its verbs, and a world is where both are trained and tested.

This repository is the framework's future home. The code is being extracted step by step from the research
repository [egarim/biological-agents](https://github.com/egarim/biological-agents), following its
[design-framework.md](https://github.com/egarim/biological-agents/blob/main/design-framework.md), with
golden traces as the gate for every step. The Core moves here once its shape stops changing (after
migration step 9); until then the branches `creatura-goldens` and `creatura-core` over there are where the
work is.

*Creatura*, Late Latin: that which has been made; first the whole created order, then each made thing.
