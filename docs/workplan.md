# Development Milestones for Coding Agent

## Milestone 1: The Core Framework & Dummy Simulation
- [x] Set up a .NET 8 Console Application project.
- [x] Install NuGet packages: Friflo.Engine.ECS, Raylib-cs, rlImGui-cs.
- [x] Implement the Program.cs bootstrapper as defined in #3. Application Bootstrapper (Raylib + ImGui) of agents.md.
- [x] Implement the struct definitions from Section 2.
- [x] Write a spawner function to instantiate 1,000 dummy entities with randomized stats and traits.
- [x] Replace the dummy spawner with a JSON schema-driven numeric attribute generator and prevalence-driven binary trait generation.
- [x] Write a basic Friflo System that slowly increases Fatigue and Stress on all entities and resets them when they hit 100.

## Milestone 2: World Time, Jobs & Commuting
- [x] Add validated JSON job definitions with work intervals, workdays, and workplace types.
- [x] Add a validated JSON world network with typed locations and bidirectional travel connections.
- [x] Assign spawned agents a job, home, compatible workplace, and deterministic shortest-time route.
- [x] Add a continuously advancing world clock with a default ten-minute in-world day.
- [x] Move agents between home and work using schedule-aware timed travel.
- [x] Add unit tests and update ECS, data structure, and project documentation.

## Milestone 3: Social Graph & Bitwise Discovery
- [x] Implement the EdgeData relationship entities.
- [x] Assign 5 random relationships (Edge Entities) to each Agent upon generation.
- [x] Implement an InteractionSystem that runs every 60 ticks, forcing an agent to roll Perception to reveal a bit of their target's Psychology.TraitMask.
- [x] Update the EdgeData.KnownTraitMask accordingly.

## Milestone 4: The ImGui Intelligence Terminal
- [x] Add opt-in `-debug` mode with a `Debug` window listing all agents and displaying selected agent details through ECS-isolated snapshots.
- [x] Add a shared bottom bar showing the current in-game world day and time in every mode.
- [x] Add four shared-bar simulation speed controls: default 10-minute day, 5-minute day, 1-minute day, and 30-second day.
- [x] Add a Windows 3.1-style `Applications` launcher with `Dossiers` and optional `Debug Window` icons; double-clicking an icon opens its application window.
- [x] Create an ImGui window titled "Surveillance Terminal" from the `Dossiers` launcher icon.
- [x] Select five distinct random Operatives (or the full population when smaller) and mark them with an ECS tag.
- [x] Draw a list of all Agents. When the user clicks an Agent, open their "Dossier".
- [x] Crucial Security Check: The Dossier UI displays only traits unlocked in the union of the five Operatives' Knowledge Masks. It uses bitwise AND (&) logic; if the mask bit is 0, it renders "Trait: ???", and if 1, it renders the trait name.
- [x] Add JSON-defined agent secret states with a default `None` state, debug-only inspection, and preservation across public action updates.
- [x] [#156: Add operative management and intelligence reports](https://github.com/wnj00524/proxystate/issues/156) — standalone Agents and Reports windows, player-authored weekly rotas, simulation-time follow/talk assignments, sourced evidence, and separate assessments through immutable intelligence projections.

## Milestone 5: Agent Networks — Families and Companies

Tracked by [#22: Add data-driven agent networks with flat and hierarchical membership](https://github.com/wnj00524/proxystate/issues/22).

### Design direction

- [ ] Keep **network types** as validated static definitions in `data/networks.json` (for example, `family` and `company`).
- [ ] Represent each runtime **network instance** (for example, “Family 17”) as an ECS entity with compact `AgentNetworkData` containing a type hash, optional anchor location ID, and deterministic ordinal.
- [ ] Represent **membership** as an `AgentNetworkMembership : ILinkRelation` from an agent to a network entity, keyed by the network and carrying compact role and optional supervisor data.
- [ ] Keep categorical network membership separate from `EdgeData`, which remains the directional, intelligence-bearing representation of interpersonal relationships.
- [ ] Store hierarchy structurally on membership through `Supervisor`; do not infer reporting lines from roles or add a redundant manager-keyed reverse relation until profiling justifies one.
- [ ] Keep generated company sizes bounded so member enumeration and direct-report filtering remain predictable without per-network member arrays or transitive closures.
- [ ] Treat networks as Ground Truth only for this milestone. Expose copied immutable debug snapshots, and do not add network knowledge to `PlayerIntelligenceDB` until a separate intelligence model defines confidence, provenance, and known or suspected membership.

### Slice 1 — Catalog and schema

- [x] Add `data/networks.json` with network type, role, and generator definitions for flat families and single-supervisor companies.
- [x] Keep roles data-driven rather than adding family or company role enums to ECS components.
- [x] Add validated runtime definitions for hierarchy mode, partition key, roles, network types, size weights, and generators; convert JSON identifiers and references to cached integer-hash lookups during catalog loading.
- [x] Use a controlled set of registered partition strategies, initially `home-location` and `work-location`, instead of a JSON query or reflection-based rule language.
- [x] Validate empty or duplicate IDs, duplicate hashes, missing roles, cross-type role references, incompatible hierarchy fields, invalid sizes and weights, impossible remainder handling, invalid membership cardinality, span/depth constraints, unknown partition keys, and company sizes that exceed hierarchy capacity.
- [x] Add malformed-content tests for every validation category and lookup tests by both ID and hash.

### Slice 2 — ECS primitives and service

- [x] Add `AgentNetworkData : IComponent` with `TypeHash`, `AnchorLocationId`, and `Ordinal`; zero anchor means the network is unanchored.
- [x] Add `AgentNetworkMembership : ILinkRelation` with `Network` as the relation key plus `RoleHash` and `Supervisor` fields.
- [x] Add `AgentNetworkService` as the sole mutation boundary for creating and deleting networks and for adding, changing, querying, supervising, and removing memberships.
- [x] Enforce live agent/network entities, network targets with `AgentNetworkData`, roles belonging to the selected network type, type-level membership cardinality, and one membership per agent/network pair.
- [x] Enforce hierarchy invariants: flat networks have no supervisors; supervisors are not self, belong to the same network, and cannot introduce cycles; each non-root company member has exactly one supervisor.
- [x] Define explicit manager removal, root succession, agent deletion cleanup, and network deletion behavior. Do not rely on Friflo target-link cleanup for the non-key `Supervisor` field.
- [x] Keep networks immutable after initial generation in the first delivery while retaining the service as the invariant-preserving path for future mutations.
- [x] Test outgoing and incoming membership views, duplicate prevention, role and cardinality validation, flat/hierarchical rules, cycle rejection, and lifecycle cleanup.

### Slice 3 — Deterministic generation

- [x] Split the injected seed into independent deterministic streams for population assignments, operative selection, network generation, and the social graph so a network-data change cannot reshuffle social relationships.
- [x] Run network generation after all agent entities and home/work assignments exist, but before generating interpersonal social edges.
- [x] Add an `AgentNetworkBuilder` with a shared partitioning stage: select the configured location key, bucket agents, deterministically shuffle each bucket, sample bounded sizes, partition the bucket, create network entities, and add memberships.
- [x] Define deterministic remainder handling so every eligible agent is consumed exactly once for types whose `MaxNetworksPerAgent` is one.
- [x] Generate synthetic flat families within each home-location bucket, assign the family-member role, leave supervisors null, and avoid inventing genealogical parent, child, spouse, age, or surname semantics.
- [x] Generate bounded companies within each work-location bucket and construct an acyclic breadth-first hierarchy with configured target/maximum span of control and maximum depth.
- [x] Assign exactly one company head, manager roles only to non-root members with reports, and employee roles to leaves; explicitly support a one-person company as a supervisor-less head.
- [x] Add deterministic generation tests covering repeatable seeds, full membership coverage, location anchoring, bounds, hierarchy balance, acyclicity, role assignment, and random-stream isolation.

### Slice 4 — Inspection and documentation

- [x] Add immutable `DebugNetworkSnapshot` and `DebugNetworkMembershipSnapshot` projections that resolve static type, role, and display names without exposing live Ground Truth entities to ordinary UI.
- [x] Extend only the ground-truth debug inspector to show an agent's networks, roles, and supervisor and to summarize each network's anchor and member count.
- [x] Keep persistent network storage linear in population: approximately one family and one company membership per agent, with no persistent lists, dictionaries, strings, member arrays, descendant closures, or redundant hierarchy indexes in ECS data.
- [x] Process packed membership relation pairs once for network-wide systems.
- [x] Update `README.md`, `docs/coreecs.md`, and `docs/datastructs.md` alongside implementation, documenting Friflo query paths and expected costs: agent networks `O(d)`, network members `O(k)`, one membership `O(d)`, direct reports `O(k)`, all memberships `O(M)`, and management-chain walks `O(depth)`.

### Slice 5 — Performance verification

- [ ] Add a large-population generation benchmark covering at least 1,000, 10,000, and 100,000 agents, recording generation time and allocations.
- [ ] Confirm from benchmarked membership and network-instance counts that persistent relation storage grows linearly with agent population.
- [ ] Confirm incoming-link member enumeration visits only the selected network's members and does not scan or materialize the full agent population.
- [ ] Measure bounded direct-report scans and use the results to decide whether a manager-keyed reverse relation is justified; do not add the relation without benchmark evidence.

### Delivery criteria

- [ ] `ContentCatalog` exposes validated, cached network definitions without runtime string searches.
- [ ] Every generated agent belongs to the configured family and company network counts, and no agent can be inserted twice into the same network.
- [ ] Generated company hierarchies are deterministic, bounded, connected, and acyclic, with exactly one root.
- [ ] Network generation does not alter population assignments, operative selection, or social edges for the same root seed.
- [ ] Debug inspection uses copied snapshots, while player-facing intelligence remains isolated and unchanged.

## Milestones 6–15: Data-Defined Intent Roadmap

The detailed architecture, sequencing, constraints, and acceptance criteria for
Milestones 6–15 are maintained in [`docs/datadrivenintents.md`](datadrivenintents.md).
The linked GitHub milestones and issues below are the canonical delivery records;
each issue also records its dependencies and verification expectations.

## [Milestone 6: Baseline and Behavioural Lock](https://github.com/wnj00524/proxystate/milestone/7)

- [x] [M6.1 — Add deterministic decision fixtures](https://github.com/wnj00524/proxystate/issues/39)
- [x] [M6.2 — Add decision trace test helper](https://github.com/wnj00524/proxystate/issues/40)
- [x] [M6.3 — Record decision performance baseline](https://github.com/wnj00524/proxystate/issues/41)

## [Milestone 7: Fact and Numeric Expression IR](https://github.com/wnj00524/proxystate/milestone/8)

- [x] [M7.1 — Introduce `FactId` and fact registry](https://github.com/wnj00524/proxystate/issues/42)
- [x] [M7.2 — Introduce numeric expression model](https://github.com/wnj00524/proxystate/issues/43)
- [x] [M7.3 — Add compiled numeric evaluator](https://github.com/wnj00524/proxystate/issues/44)
- [x] [M7.4 — Migrate `actions.json`](https://github.com/wnj00524/proxystate/issues/45)

## [Milestone 8: Predicate IR](https://github.com/wnj00524/proxystate/milestone/9)

- [x] [M8.1 — Define predicate authoring schema](https://github.com/wnj00524/proxystate/issues/46)
- [x] [M8.2 — Add compiled predicate evaluator](https://github.com/wnj00524/proxystate/issues/47)
- [x] [M8.3 — Remove `ActionEligibilityDefinition.Gate`](https://github.com/wnj00524/proxystate/issues/48)
- [x] [M8.4 — Migrate existing action eligibility](https://github.com/wnj00524/proxystate/issues/49)

## [Milestone 9: Data-Defined Targeting](https://github.com/wnj00524/proxystate/milestone/10)

- [x] [M9.1 — Introduce target definition model](https://github.com/wnj00524/proxystate/issues/50)
- [x] [M9.2 — Build generic social-relation target query](https://github.com/wnj00524/proxystate/issues/51)
- [x] [M9.3 — Move destination resolution into intent data](https://github.com/wnj00524/proxystate/issues/52)
- [x] [M9.4 — Generalise decision result structure](https://github.com/wnj00524/proxystate/issues/53)

## [Milestone 10: Generic Execution](https://github.com/wnj00524/proxystate/milestone/11)

- [x] [M10.1 — Introduce executor definition](https://github.com/wnj00524/proxystate/issues/54)
- [x] [M10.2 — Extract generic movement execution](https://github.com/wnj00524/proxystate/issues/55)
- [x] [M10.3 — Implement `performAtLocation`](https://github.com/wnj00524/proxystate/issues/56)
- [x] [M10.4 — Implement `performWithEntity`](https://github.com/wnj00524/proxystate/issues/57)
- [x] [M10.5 — Replace or rename `CommutingSystem`](https://github.com/wnj00524/proxystate/issues/58)

## [Milestone 11: Data-Defined Activity Identity](https://github.com/wnj00524/proxystate/milestone/12)

- [x] [M11.1 — Introduce `ActivityPhase`](https://github.com/wnj00524/proxystate/issues/59)
- [x] [M11.2 — Add data-defined activity identity](https://github.com/wnj00524/proxystate/issues/60)
- [x] [M11.3 — Migrate existing consumers](https://github.com/wnj00524/proxystate/issues/61)

## [Milestone 12: Intent Compiler and Structural Validation](https://github.com/wnj00524/proxystate/milestone/13)

- [x] [M12.1 — Introduce `IntentCompiler`](https://github.com/wnj00524/proxystate/issues/62)
- [x] [M12.2 — Add dense runtime indexes](https://github.com/wnj00524/proxystate/issues/63)
- [x] [M12.3 — Replace semantic validation](https://github.com/wnj00524/proxystate/issues/64)
- [x] [M12.4 — Introduce fallback intent](https://github.com/wnj00524/proxystate/issues/65)

## [Milestone 13: Dependency-Driven Reevaluation](https://github.com/wnj00524/proxystate/milestone/14)

- [x] [M13.1 — Introduce `FactDependencyMask`](https://github.com/wnj00524/proxystate/issues/66)
- [x] [M13.2 — Track changed fact categories](https://github.com/wnj00524/proxystate/issues/67)
- [x] [M13.3 — Selective intent reevaluation](https://github.com/wnj00524/proxystate/issues/68)
- [x] [M13.4 — Benchmark dependency-driven decisions](https://github.com/wnj00524/proxystate/issues/69)

## [Milestone 14: Candidate Indexing](https://github.com/wnj00524/proxystate/milestone/15)

- [x] [M14.1 — Add intent candidate bitsets](https://github.com/wnj00524/proxystate/issues/70)
- [x] [M14.2 — Build static intent indexes](https://github.com/wnj00524/proxystate/issues/71)
- [x] [M14.3 — Add scaling benchmark](https://github.com/wnj00524/proxystate/issues/72)

## [Milestone 15: Tooling, Diagnostics, and Content Safety](https://github.com/wnj00524/proxystate/milestone/16)

- [x] [M15.1 — Add decision inspector](https://github.com/wnj00524/proxystate/issues/73)
- [x] [M15.2 — Add content validation command/test](https://github.com/wnj00524/proxystate/issues/74)
- [x] [M15.3 — Add intent authoring documentation](https://github.com/wnj00524/proxystate/issues/75)
- [x] [M15.4 — Add data-only extensibility test](https://github.com/wnj00524/proxystate/issues/76)

## Milestones 17–20: Agent LOD and 100,000-Agent Roadmap

The decision-complete implementation sequence, architectural invariants, test
matrix, and agent handoff rules are maintained in
[`docs/agent-lod-roadmap.md`](agent-lod-roadmap.md). The work is tracked by
[Epic #106](https://github.com/wnj00524/proxystate/issues/106) and the four
sequential milestones below.

## [Milestone 17: Scalable Agent Lookup Foundation](https://github.com/wnj00524/proxystate/milestone/18)

- [ ] [M17.1 — Record LOD and large-population baselines](https://github.com/wnj00524/proxystate/issues/107)
- [ ] [M17.2 — Build compact agent and social relationship indexes](https://github.com/wnj00524/proxystate/issues/108)
- [ ] [M17.3 — Refactor decision targeting to indexed direct lookup](https://github.com/wnj00524/proxystate/issues/109)
- [ ] [M17.4 — Remove population scans from execution and interaction](https://github.com/wnj00524/proxystate/issues/110)

## [Milestone 18: Dynamic Agent LOD Lifecycle](https://github.com/wnj00524/proxystate/milestone/19)

- [ ] [M18.1 — Add LOD configuration and runtime contracts](https://github.com/wnj00524/proxystate/issues/111)
- [ ] [M18.2 — Implement POI classification and investigation service](https://github.com/wnj00524/proxystate/issues/112)
- [ ] [M18.3 — Implement Tier 2 cadence and critical wake-ups](https://github.com/wnj00524/proxystate/issues/113)
- [ ] [M18.4 — Add demotion grace, interaction pins, and network notifications](https://github.com/wnj00524/proxystate/issues/114)

## [Milestone 19: Data-Driven Coarse Agent Routines](https://github.com/wnj00524/proxystate/milestone/20)

- [x] [M19.1 — Compile and validate Tier 3 routine definitions](https://github.com/wnj00524/proxystate/issues/115)
- [x] [M19.2 — Build shared deterministic weekly routine profiles](https://github.com/wnj00524/proxystate/issues/116)
- [x] [M19.3 — Implement sharded Tier 3 effect integration](https://github.com/wnj00524/proxystate/issues/117)
- [x] [M19.4 — Materialize promotion and demotion state](https://github.com/wnj00524/proxystate/issues/118)
- [x] [M19.5 — Enable Tier 3 and verify cross-tier coordination](https://github.com/wnj00524/proxystate/issues/119)

## [Milestone 20: Interactive 100,000-Agent Delivery](https://github.com/wnj00524/proxystate/milestone/21)

- [x] [M20.1 — Add incremental player-intelligence projection and investigation commands](https://github.com/wnj00524/proxystate/issues/120)
- [x] [M20.2 — Virtualize dossier and debug inspection](https://github.com/wnj00524/proxystate/issues/121)
- [x] [M20.3 — Add population CLI and complete 100k verification](https://github.com/wnj00524/proxystate/issues/122)
