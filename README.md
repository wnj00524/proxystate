# Proxy State

Proxy State is a code-first .NET 8 simulation built around Friflo.Engine.ECS,
Raylib-cs, and rlImGui-cs. Milestone 1 provides the core ECS components, JSON
content catalogs, dummy-agent generation, and fatigue/stress simulation loop.

## Run

```text
dotnet run --project ProxyState.csproj
```

The application opens the Raylib canvas and an ImGui placeholder terminal.

## Test

```text
dotnet test ProxyState.sln
```
