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
- [ ] Create an ImGui window titled "Surveillance Terminal".
- [ ] Draw a list of all Agents. When the user clicks an Agent, open their "Dossier".
- [ ] Crucial Security Check: The Dossier UI must ONLY display traits that are unlocked in the Player's Knowledge Mask for that Agent. Use bitwise AND (&) logic. If the mask bit is 0, render "Trait: ???". If 1, render the trait name.
