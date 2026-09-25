# Proxy State

Proxy State is a code-first .NET 8 simulation built around Friflo.Engine.ECS,
Raylib-cs, and rlImGui-cs. The simulation provides core ECS components, JSON
content catalogs, schema-driven agent generation, binary trait masks, a world
clock, networked locations, jobs, commuting, a fatigue/stress simulation loop,
and relationship-driven family, friendship, and employment activities.

## Run

```text
dotnet run --project ProxyState.csproj
```

The ordinary launch creates 1,000 agents. Use the validated population option
to run any population from 1 through the supported 100,000-agent maximum:

```text
dotnet run --project ProxyState.csproj -- --agents 100000
```

Missing, nonnumeric, nonpositive, duplicate, and excessive values fail before
content is loaded or a Raylib window is opened. `--agents` can be combined with
`-debug`; debug and dossier lists remain clipped to visible rows.

To enable the development-only agent inspector, pass `-debug`:

```text
dotnet run --project ProxyState.csproj -- -debug
```

The application loads numeric agent attributes from `data/agent-schema.json`,
traits from `data/traits.json`, secret states from `data/secret-states.json`,
jobs from `data/jobs.json`, agent-network definitions from `data/networks.json`,
and the location network from `data/world.json`. It
then opens the Raylib canvas and the ImGui
Applications program manager. The world starts at 09:00 on Monday. One in-world day advances in about ten real minutes by default; use the clock-bar speed buttons to select five minutes, one minute, or thirty seconds per day, and
agents commute along shortest-time routes between assigned homes and workplaces.
The `Applications` window acts as the program manager: double-click `Dossiers`
to open the `Surveillance Terminal`, or, in debug mode, double-click the
`Debug Window` icon to open the development inspector. The debug window lists
all agents and shows the full copied simulation state for the selected agent.
Its ground-truth-only network section shows copied family/friend/company memberships,
resolved roles and supervisors, plus a network summary with anchor and member
count. Player-facing dossiers receive none of this network ground truth.

## Editing simulation data

The files in `data` can be read and changed with a plain-text editor; changing
them does not require changing the C# program. Start with the plain-English
[guide to reading and editing data files](docs/editing-data.md). It explains
the JSON punctuation, what every file and field means, how entries refer to one
another, which identifying numbers must remain unique, and how to check a
change without opening the game. The separate
[intent authoring guide](docs/intent-authoring.md) is an advanced reference for
the expression rules in `actions.json`.

Every generated agent belongs to one synthetic family anchored at home, one
town-wide friend group of three to six people, and one company anchored at
work. Families and friend groups are flat; companies use a bounded,
single-supervisor hierarchy. Runtime network entities and membership relations
store only compact hashes, entity links, and scalar metadata—display strings and
member collections exist only in static content or transient debug snapshots.

The simulation randomly selects five agents as Operatives (or all agents when
the population is smaller). Selected Operatives have the `Officer`
IntelligenceRole; all other agents default to `None`. The Surveillance Terminal
lists every agent, shows any assigned intelligence role, and displays only
traits discovered by at least one Operative. Operative knowledge is combined at
the ECS/UI boundary; hidden traits are displayed as `Trait: ???`.

The JSON behavior catalog contains Meet Friends, Family Time, Support Family,
Report to Supervisor, Manage Report, and Collaborate. Mutual activities invite
one eligible relationship target, reserve a deterministic pair, coordinate
travel and simultaneous performance, enforce shared duration bounds, and apply
initiator/participant effects independently. Their network selectors, schedules,
utilities, roles, and consequences remain data-defined; debug snapshots expose
copied coordination details while player intelligence remains isolated.

Every mode also includes a bottom status bar showing the in-game day, weekday,
and time of day from the simulation clock. Agents retain at least five random
social peers, with reciprocal family and friend-group clique edges added and
de-duplicated. Every 60 simulation ticks,
each edge can discover one present target trait through an opposed Perception
versus Willpower d100 contest; Paranoid targets receive a 20-point Willpower
bonus.

Agents also have a covert state separate from their public action. New agents
default to `None`; a future simulation system can set a state such as
`Surveillance` while the public action remains `Work`. Secret state is shown only
in the optional ground-truth debug inspector and is not exposed to the player
intelligence dossier.

## Test

```text
dotnet test ProxyState.sln
```

Milestone 6 also provides deterministic decision-behaviour fixtures and a
repeatable 1,000-agent performance test. Its recorded Release baseline and
measurement procedure are documented in `docs/decisionbaseline.md`.

The final LOD acceptance fixture repeats generation, classification, projection,
a simulated week, promotion/demotion, and UI work-count checks at 1,000, 10,000,
and 100,000 agents. The two larger cases are opt-in:

```text
PROXYSTATE_RUN_LARGE_BENCHMARKS=1 dotnet test ProxyState.sln -c Release \
  --filter FullyQualifiedName~Milestone20AcceptanceTests --logger "console;verbosity=detailed"
```

Milestone 7 replaces named utility sources with data-defined numeric
expressions. Content loading validates fact references and compiles each bounded
expression to postfix opcodes with typed fact handles; decision ticks evaluate
those handles directly without parsing strings. Existing work, rest, and
socialize utility formulas—including schedule pressure, low wealth, night time,
and peer affinity—are now composed in `data/actions.json`.

Milestone 8 similarly replaces named eligibility gates with data-defined
predicates. Boolean facts, boolean combinators, and numeric comparisons are
validated and compiled at content load; decision ticks evaluate pre-resolved
instructions without gate-name parsing or per-agent predicate allocations.

Milestone 11 moves public activity identity into action content. Runtime
`ActivityState` stores stable action and activity hashes plus a domain-neutral
execution phase, while debug presentation resolves activity names through the
content catalog and effects require a matching action/activity pair.

Milestone 13 derives compact dependency masks from compiled fact reads and
tracks attribute, location, travel, and social-target mutations. Same-minute
updates rescore only affected intents while the minute boundary remains a full
deterministic safety pass; benchmark results live in `docs/decisionbaseline.md`.

Milestone 14 compiles the dense intent indexes into packed candidate bitsets.
Decision ticks intersect those static indexes with job, home, workplace, and
social- and network-relation availability, then visit only the resulting runtime indexes.
The fallback remains outside the candidate set and is selected safely when the
intersection produces no eligible intent.

Milestone 16 adds compiled network relationship selectors, target attributes,
and a generic mutual-coordination lifecycle. All six relationship behaviors and
their participant scoring remain authored in JSON; runtime systems never branch
on behavior, network, or role IDs.
