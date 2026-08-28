# 4. Core ECS Systems (Logic)

### 4.1 Utility AI System

**Goal:** Determine what an agent does this tick.

* Query all entities with `AgentAttributes` and `Tier1LodTag`.
* Iterate through available actions (Work, Rest, Socialize) loaded from `actions.json`.
* Resolve numeric values by name through `AgentAttributeSchema`.
* Score formula: `BaseScore + (TraitModifiers) - (Fatigue/Stress Penalties)`.
* Assign highest-scoring action to `AgentState.CurrentActionHash`.

### 4.2 Interaction & Discovery System

**Goal:** Handle target interrogation/surveillance based on Perception vs Willpower.

* When `Source` interacts with `Target`, calculate the schema-defined `perception` value vs the schema-defined `willpower` value (modified by Target's `Paranoid` trait).
* On success, perform bitwise `OR` on `EdgeData.KnownTraitMask`. (e.g., `KnownTraitMask |= 0x0004` to reveal the Greedy trait).
* Recalculate `Affinity` by checking shared traits: `Target.Psychology.TraitMask & EdgeData.KnownTraitMask`.

### 4.3 Fatigue and Stress System (Milestone 1)

**Goal:** Advance the short-term state of active agents every simulation tick.

* Query entities with `AgentAttributes` and `Tier1LodTag` using Friflo's `QuerySystem`.
* Resolve `fatigue` and `stress` indexes from `AgentAttributeSchema`.
* Increase both values by `0.1` per update by default.
* Reset each value independently to `0` when its updated value reaches or exceeds `100`.
* Entities without `Tier1LodTag` are excluded, leaving Tier 2 and Tier 3 agents for later systems.
* The application executes the system through `SystemRoot.Update(default)` before the Raylib rendering phase.

The system is configured with an optional positive per-tick increase so simulation tests can use a larger value without changing production defaults.

### 4.4 World Clock System (Milestone 2)

**Goal:** Advance a shared world calendar independently of rendering frame rate.

* Store one `WorldTime` component as the world-time singleton.
* Convert real elapsed seconds to simulation seconds using `600` real seconds per in-world day by default.
* Keep the last simulation delta on `WorldTime` so time-based systems consume the same elapsed interval.
* Job schedules use Monday as day `1`, integer minutes from midnight, and non-overnight intervals.

### 4.5 Commuting System (Milestone 2)

**Goal:** Move Tier 1 agents between assigned homes and workplaces according to their job schedules.

* Query `AgentLocation`, `AgentTravel`, `Identity`, and `AgentState` together.
* Use the assigned job hash in `Identity.OccupationId` to resolve workdays and work interval data.
* Begin travel early enough for the agent to arrive at the scheduled work start.
* Traverse the precomputed shortest-travel-time route, using each network edge's duration.
* Begin the reverse route at the scheduled work end and set the agent to rest when home.
* Agents remain home on non-workdays. Missing routes fail during assignment or raise a clear topology error rather than creating partial agent state.

### 4.6 Debug Agent Inspector

**Goal:** Provide an opt-in development view of the complete simulated agent population.

* Debug mode is enabled only when the process receives the `-debug` command-line argument (case-insensitive).
* `DebugSnapshotBuilder` copies the current agent component values into immutable, UI-facing snapshots once per frame.
* The `Debug` ImGui window lists every agent and lets the user select one to inspect identity, faction, job, attributes, all trait states, current action, locations, and travel state.
* The ImGui layer consumes snapshots only; it never queries or mutates the Ground Truth ECS store.

---
