# 4. Core ECS Systems (Logic)

### 4.1 Utility AI System

**Goal:** Determine what an agent does this tick.

* Query all entities with `AgentState` and `Tier1LodTag`.
* Iterate through available actions (Work, Rest, Socialize) loaded from `actions.json`.
* Score formula: `BaseScore + (TraitModifiers) - (Fatigue/Stress Penalties)`.
* Assign highest-scoring action to `AgentState.CurrentActionHash`.

### 4.2 Interaction & Discovery System

**Goal:** Handle target interrogation/surveillance based on Perception vs Willpower.

* When `Source` interacts with `Target`, calculate: `Source.Perception` vs `Target.Willpower` (modified by Target's `Paranoid` trait).
* On success, perform bitwise `OR` on `EdgeData.KnownTraitMask`. (e.g., `KnownTraitMask |= 0x0004` to reveal the Greedy trait).
* Recalculate `Affinity` by checking shared traits: `Target.Psychology.TraitMask & EdgeData.KnownTraitMask`.

### 4.3 Fatigue and Stress System (Milestone 1)

**Goal:** Advance the short-term state of active agents every simulation tick.

* Query entities with `AgentState` and `Tier1LodTag` using Friflo's `QuerySystem`.
* Increase `Fatigue` and `Stress` by `0.1` per update by default.
* Reset each value independently to `0` when its updated value reaches or exceeds `100`.
* Entities without `Tier1LodTag` are excluded, leaving Tier 2 and Tier 3 agents for later systems.
* The application executes the system through `SystemRoot.Update(default)` before the Raylib rendering phase.

The system is configured with an optional positive per-tick increase so simulation tests can use a larger value without changing production defaults.

---
