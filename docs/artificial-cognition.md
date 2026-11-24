# Creatura: an experimental framework for artificial cognition

## Mission

Creatura exists to investigate how artificial entities perceive, reason, remember, regulate their behavior, adapt through experience, and act in environments. Building useful agents is an application of that research, not its only purpose.

A language model is a component of cognition, not necessarily the entire cognitive system. We study the organization of the system around it: goals, observations, actions, feedback, memory, regulation and embodiment. Artificial cognition here is an engineering research objective, not a claim of consciousness or human-equivalent intelligence.

## What distinguishes Creatura

Agent orchestration is valuable. Creatura adds a research emphasis: interchangeable cognitive designs operating under comparable conditions, worlds with observable consequences, independent outcome verification, and experiments that can falsify architectural hypotheses. Other frameworks may support these ideas too; our distinction is making them central to the project.

## Research principles

1. Hypothesis → experiment → evidence → architectural decision.
2. Separate observations, hypotheses, actions and verified outcomes. A model's assertion of success is not proof.
3. Compare designs under equivalent model, evidence, tool and resource budgets; include simple baselines and negative controls.
4. Publish reproducible fixtures, configuration, raw redacted results, uncertainty, costs and failures. Report negative results.
5. Keep the core small and dependency-free; add optional capabilities only when justified.
6. Treat observations as untrusted, enforce authorization outside the model, and keep secrets out of cognitive memory.
7. Prefer independently usable components and versioned interfaces over duplicated functionality.

## Cognitive research program

- Perception and action: how semantic observations and specialized organs affect performance.
- Regulation: how different cognitive designs defend goals, respond to errors and stop safely.
- Memory and adaptation: whether episodic, relational and hybrid retrieval improve held-out task performance without reinforcing stale beliefs.
- Embodiment: how worlds, hosts and sensorimotor capabilities change what agents can do.
- Collaboration: how agents share verified knowledge without losing provenance or isolation.
- Efficiency: whether better cognitive organization improves success at a fixed model and budget.

Existing foundations include the cognitive loop, regulators, worlds and Bench. The planned integrations and graph research are tracked in [#107](https://github.com/egarim/creatura/issues/107), [#108](https://github.com/egarim/creatura/issues/108), [#109](https://github.com/egarim/creatura/issues/109) and [#110](https://github.com/egarim/creatura/issues/110). These are research directions, not claims of completed capabilities or proven improvements.

## Ecosystem

Creatura is the cognitive system; Vessel hosts it; Worlds define its environment contract; Telekinesis supplies perception and control; Sentiora supplies specialized sensorimotor runtimes; Medium enriches application semantics; Vivarium supplies the supervisory habitat. Custodia is a proposed credential authorization broker. These are conceptual roles, not mandatory dependencies.

Our goal is not merely to build agents that act, but to understand and experimentally improve the architecture that makes their actions intelligent.
