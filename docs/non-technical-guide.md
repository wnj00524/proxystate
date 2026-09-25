# Proxy State — Non-Technical Guide

This document explains Proxy State for readers who do not need to understand the source code. It covers what the program is trying to simulate, what a user sees, and how the major pieces fit together.

## What Proxy State is

Proxy State is an agent-based political and intelligence simulation of a synthetic town. Hundreds or thousands of artificial residents live ordinary lives, work, travel, form relationships, participate in organisations and politics, and gradually reveal information to a player-controlled intelligence team.

The central design rule is that **the simulation knows more than the player does**. The internal simulation contains the authoritative "ground truth", while the normal player interface receives only information that has legitimately been learned or published. Dossiers therefore do not automatically reveal a person's hidden traits, and polling applications receive aggregate survey results rather than direct access to everyone's political state.

The current application is best understood as a working simulation platform with a desktop-style intelligence interface rather than a finished consumer game.

## Starting a simulation

A normal interactive run generates a fresh artificial population. The default population is 1,000 residents and the program supports populations up to 100,000.

Each resident receives generated characteristics including personal attributes, traits, political alignment, demographic characteristics, a job, a home, a workplace, social relationships and organisational memberships.

Normally five residents are selected as the player's Operatives. If the population contains fewer than five residents, all residents become Operatives.

The world begins on Monday at 09:00 and advances continuously. The player can select four simulation speeds.

## The town

The default town contains North Homes, South Homes, Town Square, Civic Offices, Market Street, Town Hall, Town School, Town Clinic, Police Station, Town Factory, Building Yard, Market Restaurant and Bus Depot.

Locations are connected by routes with authored travel times. Residents therefore move through a physical network rather than teleporting between activities. Travel time affects ordinary work and social behaviour as well as political participation such as nomination and voting.

## Residents

Each resident has numerical characteristics. The current model includes Intelligence, Charisma, Perception, Willpower, Preference, Salience, Fatigue, Stress, Wealth, Political Engagement and Motivation.

Residents may also have discrete personality traits. The current traits are Brave, Chaste, Greedy and Paranoid. Traits can influence behaviour and intelligence discovery, but the normal player interface does not automatically reveal them.

## Jobs and working life

The simulation contains ordinary occupations such as Office Worker, Shopkeeper, Civic Clerk, Teacher, Doctor, Nurse, Police Officer, Factory Worker, Construction Worker, Restaurant Server, Bus Driver and Cleaner.

Jobs define working days, start and finish times, workplace type, sector, weekly pay and prestige. Political positions such as Mayor, Town Councillor and Public Works Commissioner are also represented as jobs, but are obtained through election or appointment rather than ordinary employment.

Because occupation affects schedules and locations, a change of political office also changes the person's ordinary simulated life.

## Families, friends and companies

Residents belong to social and organisational networks.

Families are synthetic household-style groups, generally containing two to six people. The current system deliberately does not invent genealogy such as parent, child or sibling relationships.

Friend groups are town-wide social groups, generally containing three to six people.

Companies are workplace-based organisations that can contain a head, managers and employees. Management relationships are hierarchical: non-root workers can have supervisors, and managers can have direct reports.

The simulation also maintains interpersonal social relationships with affinity values. These relationships affect social activities, political social influence, intelligence opportunities and coordinated behaviour.

## How residents decide what to do

Residents do not merely follow one fixed timetable. Possible behaviours compete according to their current circumstances.

For example, Work considers matters such as whether it is a workday, proximity to the resident's shift, wealth, stress, fatigue and relevant personality traits. Rest becomes more attractive as fatigue or stress rises. Social activities depend on suitable people being available.

Actions have commitment periods, switching thresholds and cooldowns so that residents do not unrealistically change their minds every moment.

Some activities require two people. The coordination system handles invitations, acceptance, travel, waiting, simultaneous participation and completion. Examples include meeting friends, family activities, collaboration, reporting to a supervisor and management interactions.

Activities can change resident state over time. Working increases wealth but can increase fatigue and stress; resting reduces fatigue and stress. The consequences of today's activities therefore influence later decisions.

## Visible and hidden activity

Proxy State separates a resident's visible activity from a possible covert state. A person can appear to be carrying out an ordinary activity while separately having a hidden state such as Surveillance.

The normal player interface is not entitled to see hidden state merely because the simulation contains it. Developer/debug inspection can expose it.

## The intelligence model

The intelligence game is built around two separate views of the world.

**Ground truth** is the authoritative internal simulation: real traits, relationships, behaviour, hidden states and other internal facts.

**Player intelligence** is the restricted information that the player's organisation has acquired.

The normal interface works from a copied player-intelligence database rather than reading the complete simulation directly. A dossier can therefore display an unknown trait even though the engine knows exactly which trait the person has.

## Operatives

Operatives are the player's intelligence personnel. Their working rota can specify days and start/end times.

The current special assignments are **Follow** and **Talk**. Only one special assignment can be active for an Operative at a time, and assignments interrupt the Operative's normal routine until they finish or are recalled.

A Follow assignment directs an Operative to observe another resident for a period. It can produce sightings, observations, interaction evidence or unsuccessful observations. Travel and circumstances matter.

A Talk assignment directs an Operative to attempt an interview or conversation. Success is not guaranteed and can produce intelligence or an unsuccessful report.

## Intelligence reports

Completed assignments generate reports. Reports deliberately distinguish **evidence** from **assessment**.

Evidence records what the Operative claims to have observed, including source and simulation time. Assessment is the Operative's interpretation of that evidence and carries a confidence value.

The distinction prevents an intelligence assessment from silently becoming objective ground truth.

## Dossiers and investigations

The Dossiers application opens the Surveillance Terminal. It is the main player-facing database of residents.

A dossier contains intelligence, not omniscient simulation data. Undiscovered traits remain hidden.

The player can also mark a resident as under investigation. Investigation is more than a bookmark: it increases the simulation detail devoted to that resident and relevant associates so that intelligence work occurs against a sufficiently detailed simulated subject.

## Simulation detail and large populations

Proxy State is designed to support populations up to 100,000, so it does not spend maximum processing effort on every resident at every moment.

It uses three broad levels of detail:

- **Tier 1:** full-detail simulation for important people such as Operatives and direct points of interest.
- **Tier 2:** detailed state with a reduced decision cadence, useful for people connected to important subjects.
- **Tier 3:** coarse routine simulation for people who are currently less important to the player's immediate concerns.

Residents do not cease to exist when simulated coarsely. Important persistent state, including political participation, remains authoritative.

## Politics

Every generated resident has a political alignment. The supplied simulation currently contains two fictional factions, Blue and Red.

Alignment is not the same as formal membership. A resident can be politically aligned with a faction without joining it.

Formal faction members can be Volunteers, Activists or Leaders. Recruitment is voluntary within the simulation and residents can decline even when their political alignment matches the recruiting faction.

Factions pursue data-defined strategies. Their progress includes membership, organisation, public support and institutional control. Goals can require prerequisites, so organisational development can precede campaigning or office-seeking.

## Elections

The default election cycle is 28 simulated days, with a nomination period before polling day.

Potential candidates decide whether to stand according to factors including political engagement and motivation, plus deterministic variation. Nomination is represented as a physical trip in the simulated town.

Residents also decide individually whether to vote. Turnout considers political engagement, motivation, social pressure, travel time and work conflicts. A resident who decides to vote must actually be able to travel to Town Hall during polling hours.

Political offices include elected and appointed positions. Election outcomes therefore feed back into employment, schedules and the ordinary life simulation.

## Political research

Proxy State contains two fictional polling organisations: Northstar Opinion and Townline Research.

They do not read exact population preferences. They conduct simulated survey research involving sample selection, telephone calls, nonresponse, demographic sampling, weighting, likely-voter filtering, effective sample size and statistical uncertainty.

Northstar uses a stratified probability-style sample and a likely-voter screen. Townline uses a demographic quota model and reports all respondents. Their methodologies can therefore produce different estimates from the same underlying town.

Polling waves currently begin weekly, with fieldwork spread across several days. Published projections include calls attempted, interviews, nonresponse, effective sample size, raw and weighted percentages, 95% intervals and trends.

## The desktop applications

The normal application launcher contains:

- **Dossiers** — opens the Surveillance Terminal.
- **Northstar Opinion** — displays Northstar polling.
- **Townline Research** — displays Townline polling.

The interface also contains:

- **Agents** — manages the Operative team, rotas, targets and assignments.
- **Reports** — displays completed intelligence reports and their evidence/assessments.
- **World Time bar** — shows day, weekday and time and controls simulation speed.

When debug mode is enabled, a **Debug Window** becomes available. It can expose ground-truth information that the ordinary player should not know, including real traits, internal activities, hidden state, network membership, supervisors, reports and simulation-detail diagnostics.

## Headless mode

Proxy State can run without opening its graphical interface. Headless mode runs a deterministic simulation for a specified number of days and produces Markdown and JSON political diagnostic reports.

Reports include population and seed information, daily faction progress, election results, officeholders and a chronological political event log.

A fixed random seed makes diagnostic runs reproducible, which is useful for testing and balancing.

## Editable data

Much of the simulated world lives in JSON files under `data/`, so content can be changed without rewriting C#.

The data describes actions, resident attributes, traits, jobs, locations, travel routes, networks, factions, elections, polling, intelligence tasks, covert states and level-of-detail rules.

The repository includes `docs/editing-data.md`, which explains how a non-programmer can edit these files safely. The program also has a validation mode that checks authored content before starting the graphical application.

## What is currently simulated

The repository presently contains substantive systems for population generation, personal attributes, traits, employment, schedules, fatigue, stress, wealth, travel, social relationships, families, friend groups, company hierarchy, autonomous decisions, joint activities, intelligence discovery, Operatives, following, interviews, intelligence reports, investigations, level of detail, political alignment, faction recruitment and organisation, political offices, candidacy, turnout, elections, appointments, polling and headless diagnostics.

## Current maturity

Proxy State has a substantial simulation core, but it should not yet be read as a conventional finished game. Residents are still primarily presented as numbered Agents, the graphical world presentation is limited, the developer/debug interface remains important, and some systems are foundations intended for later expansion.

A useful summary is:

> **Proxy State is an agent-based political society simulator wrapped in an intelligence-management interface, in which the simulation knows the truth but the player must discover it.**

---

# Program map — what each part does

This section maps the repository to plain-English responsibilities.

## Top-level application files

| File | What it does |
| --- | --- |
| `Program.cs` | The main coordinator. Starts validation, interactive or headless mode; creates the population and simulation systems; advances the simulation; transfers safe data to the UI; and draws the application windows. |
| `ApplicationOptions.cs` | Reads and validates command-line options such as population size, debug mode, headless duration, seed and report filename. |
| `ApplicationShell.cs` | Implements the desktop-style application launcher and tracks which application windows are open. It deliberately handles presentation rather than ground-truth simulation data. |
| `ContentValidation.cs` | Provides the `--validate-content` command. Loads authored data and reports configuration errors without starting the graphical application. |
| `IntelligenceDossiers.cs` | Implements the player's restricted intelligence database and Surveillance Terminal. It is a key security/design boundary preventing hidden simulation facts from leaking into ordinary dossiers. |
| `OperativeWindows.cs` | Implements the Agents and Reports user interfaces: Operative rotas, Follow/Talk assignments, recall controls and completed intelligence reporting. |
| `PoliticalResearchWindow.cs` | Displays aggregate polling results for Northstar and Townline, including methodology, fieldwork, estimates, uncertainty and trends. |
| `DebugInspection.cs` | Builds and displays a separate developer-facing ground-truth inspection view. It contains information deliberately excluded from the normal player interface. |
| `WorldTimePresentation.cs` | Converts simulation time into safe UI data, formats day/time text and draws the persistent clock/speed bar. |
| `ProxyState.csproj` | Defines how the application is built: target framework, external packages and data files copied into the output. |
| `nb.ps1` | Convenience PowerShell script used by the repository's development workflow. |

## Simulation engine

| File | What it does |
| --- | --- |
| `Simulation/Components.cs` | Defines the core pieces of state stored on residents and relationships: identity, attributes, traits, location, travel, decisions, activities, political state, intelligence roles and related records. Think of it as the vocabulary of the simulated world. |
| `Simulation/AgentSpawner.cs` | Creates the initial population and assigns generated characteristics, jobs, homes, workplaces, Operatives, political/demographic state, networks and social relationships. |
| `Simulation/SimulationRandomStreams.cs` | Separates random-number streams so changes in one part of generation do not unnecessarily change unrelated outcomes. |
| `Simulation/AgentSocialIndexes.cs` | Maintains efficient lookup structures for social relationships so systems do not repeatedly scan the whole population. |
| `Simulation/SocialGraph.cs` | Creates and manages interpersonal social connections and the relationship data used by social/intelligence behaviour. |
| `Simulation/DecisionSystems.cs` | The main autonomous decision engine. Scores available actions for residents, reacts to changed circumstances and selects what each detailed resident intends to do. |
| `Simulation/IntentCompiler.cs` | Converts human-authored action definitions into efficient runtime instructions before the simulation starts. |
| `Simulation/IntentCandidateIndex.cs` | Narrows down possible targets/actions efficiently instead of testing every resident against every possibility. |
| `Simulation/NumericExpressions.cs` | Evaluates the mathematical expressions used in data-authored decision rules. |
| `Simulation/PredicateExpressions.cs` | Evaluates yes/no eligibility rules used by data-authored actions and participation rules. |
| `Simulation/CoordinationSystem.cs` | Coordinates activities requiring two residents, including proposals, acceptance, travel/waiting, participation and release. |
| `Simulation/WorldSystems.cs` | Runs the world clock, resident travel, execution of chosen activities and activity effects. |
| `Simulation/FatigueStressSystem.cs` | Contains fatigue/stress-related simulation behaviour and supporting logic. |
| `Simulation/WorldTopology.cs` | Represents the town as locations and connections and validates the authored world network. |
| `Simulation/WorldPathfinder.cs` | Finds routes through the town and calculates travel paths/times. |

## Networks and organisations

| File | What it does |
| --- | --- |
| `Simulation/AgentNetworkCatalog.cs` | Loads/represents the definitions for network types and roles such as families, friend groups and companies. |
| `Simulation/AgentNetworkBuilder.cs` | Generates the initial family, friend-group and company memberships from the authored rules. |
| `Simulation/AgentNetworkService.cs` | The controlled runtime boundary for changing networks. It enforces rules such as valid roles, one supervisor where required and no management cycles. |

## Large-population simulation

| File | What it does |
| --- | --- |
| `Simulation/AgentLodService.cs` | Decides which residents need full, reduced or coarse simulation detail. Investigations, Operatives, relationships and active interactions can raise a resident's detail level. |
| `Simulation/CoarseRoutineProfiles.cs` | Defines compact routine profiles used when residents are simulated economically rather than at full behavioural detail. |
| `Simulation/CoarseRoutineSystem.cs` | Advances coarse residents in batches and catches them up when they return to detailed simulation. |
| `Simulation/SimulationWorkDiagnostics.cs` | Records work/performance diagnostics used to check how much simulation processing different systems perform. |

## Intelligence operations

| File | What it does |
| --- | --- |
| `Simulation/OperativeManagement.cs` | Owns Operative schedules and special assignments, processes Follow/Talk/Recall commands and produces sanitized reports for the player-facing intelligence projection. |

The intelligence discovery produced by ordinary interpersonal interaction is integrated with the social/interaction systems and then copied into `PlayerIntelligenceDB` in `IntelligenceDossiers.cs`.

## Politics

| File | What it does |
| --- | --- |
| `Simulation/PoliticalSystem.cs` | Runs candidacy, nomination trips, turnout decisions, physical polling trips, ballots, election resolution and political-office changes/appointments. |
| `Simulation/PoliticalFactionSystem.cs` | Runs faction membership, recruitment, volunteers, activists, leaders, organisational progress, campaigning and data-authored faction goals. |
| `Simulation/PoliticalReportModels.cs` | Defines the stable data structures used to describe political events and election/report results. |

## Political research

| File | What it does |
| --- | --- |
| `Simulation/PoliticalResearchSystem.cs` | Conducts simulated opinion polling: sample selection, call scheduling, response/nonresponse, weighting, estimates, effective sample size and confidence intervals. |
| `Simulation/PoliticalResearchModels.cs` | Defines polling/research data structures passed between the simulation, reports and presentation layer. |

## Content and diagnostics

| File | What it does |
| --- | --- |
| `Simulation/ContentCatalog.cs` | Loads the authored JSON content, resolves references and validates that the different data files form a coherent simulation configuration. |
| `Simulation/HeadlessSimulationRunner.cs` | Runs the normal simulation systems without graphics for a specified duration and writes political diagnostics in Markdown and JSON. |

## Data files

| File | What it controls |
| --- | --- |
| `data/agent-schema.json` | Numerical resident attributes and their ranges/averages. |
| `data/traits.json` | Discrete personality traits and how common they are. |
| `data/actions.json` | Resident behaviours: eligibility, desirability, trait effects, commitment/cooldowns, targets, execution and ongoing effects. |
| `data/jobs.json` | Occupations, working hours, pay, prestige, workplace types and political selection/appointment rules. |
| `data/world.json` | Town locations and travel connections. |
| `data/networks.json` | Family, friend-group and company definitions and generation rules. |
| `data/factions.json` | Political factions, leadership/activist jobs, strategic goals and prerequisites. |
| `data/politics.json` | Election cycle, nomination period, polling hours and factors affecting candidacy and turnout. |
| `data/research.json` | Polling companies, fieldwork schedule, demographics, sampling methods, response rules and weighting limits. |
| `data/intelligence-tasks.json` | Durations and success/confidence parameters for Follow and Talk intelligence work. |
| `data/secret-states.json` | Hidden/covert resident states. |
| `data/lod.json` | Rules for reduced-detail and coarse simulation, including decision cadence and coarse daily routines. |

## Documentation

| File | What it is for |
| --- | --- |
| `README.md` | Entry point for building, running and understanding the repository at a high level. |
| `docs/editing-data.md` | Non-programmer guide to safely changing JSON simulation content. |
| `docs/coreecs.md` | Technical architecture and milestone documentation for the ECS simulation. |
| `docs/datastructs.md` | Technical reference for the simulation's core data structures. |
| `docs/non-technical-guide.md` | This document: product-level explanation and plain-English map of the program. |
| `politics-report.md` / `politics-report.json` | Example/generated outputs from a headless political simulation, not authoritative design specifications. |

## Tests

The `tests/ProxyState.Tests/` project is the automated safety net for the program. Its tests cover content loading, population generation, decisions, social/network behaviour, intelligence isolation, Operative assignments, politics, polling, headless reporting, level-of-detail behaviour and performance/large-population scenarios.

When changing the simulation, these tests are intended to catch regressions such as a player-facing window gaining access to hidden ground truth, invalid network structures, non-deterministic diagnostic runs or behaviour that no longer matches the authored data.
