# Proxy State

Proxy State is a code-first .NET 8 simulation built around Friflo.Engine.ECS,
Raylib-cs, and rlImGui-cs. Milestone 3 provides the core ECS components, JSON
content catalogs, schema-driven agent generation, binary trait masks, a world
clock, networked locations, jobs, commuting, a fatigue/stress simulation loop,
and a randomized bidirectional social graph with scheduled bitwise discovery.

## Run

```text
dotnet run --project ProxyState.csproj
```

To enable the development-only agent inspector, pass `-debug`:

```text
dotnet run --project ProxyState.csproj -- -debug
```

The application loads numeric agent attributes from `data/agent-schema.json`,
traits from `data/traits.json`, jobs from `data/jobs.json`, and the location
network from `data/world.json`. It then opens the Raylib canvas and an ImGui
placeholder terminal. One in-world day advances in about ten real minutes, and
agents commute along shortest-time routes between assigned homes and workplaces.
With debug mode enabled, the `Debug` window lists all agents and shows the full
copied simulation state for the selected agent.

Every mode also includes a bottom status bar showing the in-game day, weekday,
and time of day from the simulation clock. Agents have five unique social peers
represented by reciprocal directed edge entities. Every 60 simulation ticks,
each edge can discover one present target trait through an opposed Perception
versus Willpower d100 contest; Paranoid targets receive a 20-point Willpower
bonus.

## Test

```text
dotnet test ProxyState.sln
```
