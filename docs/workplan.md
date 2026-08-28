# Development Milestones for Coding Agent

## Milestone 1: The Core Framework & Dummy Simulation
- [x] Set up a .NET 8 Console Application project.
- [x] Install NuGet packages: Friflo.Engine.ECS, Raylib-cs, rlImGui-cs.
- [x] Implement the Program.cs bootstrapper as defined in #3. Application Bootstrapper (Raylib + ImGui) of agents.md.
- [x] Implement the struct definitions from Section 2.
- [x] Write a spawner function to instantiate 1,000 dummy entities with randomized stats and traits.
- [x] Write a basic Friflo System that slowly increases Fatigue and Stress on all entities and resets them when they hit 100.

## Milestone 2: Social Graph & Bitwise Discovery
- [ ] Implement the EdgeData relationship entities.
- [ ] Assign 5 random relationships (Edge Entities) to each Agent upon generation.
- [ ] Implement an InteractionSystem that runs every X ticks, forcing an agent to roll Perception to reveal a bit of their target's Psychology.TraitMask.
- [ ] Update the EdgeData.KnownTraitMask accordingly.

## Milestone 3: The ImGui Intelligence Terminal
- [ ] Create an ImGui window titled "Surveillance Terminal".
- [ ] Draw a list of all Agents. When the user clicks an Agent, open their "Dossier".
- [ ] Crucial Security Check: The Dossier UI must ONLY display traits that are unlocked in the Player's Knowledge Mask for that Agent. Use bitwise AND (&) logic. If the mask bit is 0, render "Trait: ???". If 1, render the trait name.
