# Proxy State

Proxy State is a code-first .NET 8 simulation built around Friflo.Engine.ECS,
Raylib-cs, and rlImGui-cs. Milestone 2 provides the core ECS components, JSON
content catalogs, schema-driven agent generation, binary trait masks, a world
clock, networked locations, jobs, commuting, and a fatigue/stress simulation loop.

## Run

```text
dotnet run --project ProxyState.csproj
```

The application loads numeric agent attributes from `data/agent-schema.json`,
traits from `data/traits.json`, jobs from `data/jobs.json`, and the location
network from `data/world.json`. It then opens the Raylib canvas and an ImGui
placeholder terminal. One in-world day advances in about ten real minutes, and
agents commute along shortest-time routes between assigned homes and workplaces.

## Test

```text
dotnet test ProxyState.sln
```
