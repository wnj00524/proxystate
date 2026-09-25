## 2. Core ECS Data Structures

The simulation uses pure value-type components and tags implementing the native
`Friflo.Engine.ECS` interfaces. Components contain only simulation state; systems
contain behavior. The namespace for Milestone 1 types is `ProxyState.Simulation`.

### 2.1 Agent Components (Ground Truth)

```csharp
using Friflo.Engine.ECS;

public struct Tier1LodTag : ITag { } // Full-detail decisions.
public struct Tier2LodTag : ITag { } // Reduced decision cadence.
public struct Tier3LodTag : ITag { } // Coarse routine simulation.
public struct DetailedSimulationTag : ITag { } // Present on Tier 1 and Tier 2.
public struct OperativeTag : ITag { } // Player-controlled intelligence source.

public enum AgentLodTier : byte { Tier1 = 1, Tier2 = 2, Tier3 = 3 }

[Flags]
public enum AgentInterestReason : byte {
    None = 0,
    Operative = 1,
    Investigation = 2,
    RelatedPointOfInterest = 4,
    ActiveInteraction = 8
}

public struct AgentLodState : IComponent {
    public AgentLodTier DesiredTier;
    public int DirectPoiReferenceCount;
    public AgentInterestReason InterestReasons;
    public long ScheduledDemotionMinute;
    public int CoarseProfileId;
    public ulong CoarseProfileFingerprint;
    public long LastCoarseSimulatedMinute;
}

public enum IntelligenceRole : byte {
    None,       // Zero/default value for unassigned agents.
    Officer,
    Agent,
    Informant
}

public struct Identity : IComponent {
    public int NameId;       // Hash mapped to localization.
    public int OccupationId; // Hash mapped to job data.
    public IntelligenceRole IntelligenceRole;
}

public struct PoliticalAlignment : IComponent {
    public byte FactionId;   // JSON faction ID.
}

public struct PoliticalParticipation : IComponent {
    public int CandidateJobHash;
    public int CandidateElectionId;
    public int VotedElectionId;
    public int VotingDecisionElectionId;
    public int PreviousOccupationId;
    public int TripOriginLocationId;
    public int TripElectionId;
    public int TripOfficeHash;
    public long TripDepartureMinute;
    public long TripArrivalMinute;
    public long TripReturnMinute;
    public PoliticalTripKind TripKind;
    public bool TripInProgress;
    public bool TripArrived;
}

public enum PoliticalTripKind : byte { None, Nomination, Polling }

public struct AgentAttributes : IComponent {
    public float[] Values; // Values are ordered by data/agent-schema.json.
}

public struct Psychology : IComponent {
    public long TraitMask; // Bits are supplied by data/traits.json.
}

public struct AgentState : IComponent {
    public int SecretStateHash;    // Hash supplied by data/secret-states.json; 0 is None.
}

public struct IntentionState : IComponent {
    public int ActionHash;          // Goal selected by utility deliberation.
    public int TargetEntityId;     // Social or network target, or 0.
    public int TargetLocationId;   // Destination for location-bound goals.
    public long SelectedAtMinute;
    public float Utility;
}

public struct ActivityState : IComponent {
    public int ActionHash;          // Content action responsible for the activity.
    public int ActivityTypeHash;    // Data-defined identity from the selected action.
    public ActivityPhase Phase;     // Idle, Moving, Waiting, Performing, or Blocked mechanics.
    public long StartedAtMinute;
}

public enum CoordinationRole : byte { None, Initiator, Participant }
public enum CoordinationStatus : byte { None, Reserved, Travelling, Waiting, Performing }

public struct CoordinationState : IComponent {
    public int PartnerEntityId;
    public int ActionHash;
    public CoordinationRole Role;
    public CoordinationStatus Status;
    public long AcceptedAtMinute;
    public long StartedAtMinute;
    public int MinimumDurationMinutes;
    public int MaximumDurationMinutes;
    public float Utility;          // This agent's utility for the coordinated action.
    public bool ReleaseRequested;
}

public struct DecisionState : IComponent {
    public long LastConsideredMinute;
    public bool Dirty;
    public FactDependencyMask ChangedFacts;
    public DecisionWakeReason ImmediateWakeReasons;
    public int[] CooldownActionHashes;
    public long[] CooldownUntilMinutes;
    public float[][] CachedUtilityContributions; // Allocated in debug mode only.
    public float[][] CachedTraitContributions;   // Allocated in debug mode only.
    public string[] CachedRejectedPredicates;    // Allocated in debug mode only.
}

public struct WorldTime : IComponent {
    public double ElapsedSimulationSeconds;
    public double DeltaSimulationSeconds;
}

public struct AgentLocation : IComponent {
    public int HomeLocationId;
    public int WorkLocationId;
    public int CurrentLocationId;
}

public enum AgentTravelMode : byte { Stationary, Travelling }

public struct AgentTravel : IComponent {
    public int[] RouteLocationIds;
    public int TotalTravelMinutes;
    public int RoutePosition;
    public float RemainingTravelMinutes;
    public int DestinationLocationId;
    public AgentTravelMode Mode;
}
```

Every agent owns `AgentLodState` and exactly one tier tag. `AgentLodService` is
the exclusive mutation boundary for that state and for `DetailedSimulationTag`.
Tier 1 and Tier 2 are detailed; Tier 3 is not. During the Milestone 18 rollout,
a desired Tier 3 assignment is materialized as Tier 2 because Tier 3 remains
disabled. The coarse-profile fields are reserved contracts for Milestone 19.

`AgentLodService` owns a stable-ID POI set and a cached, sorted direct-neighbour
list per POI. `DirectPoiReferenceCount` is incremented once for each operative
or investigated direct neighbour, preventing an agent from leaving Tier 2 while
another POI still references it. Its drained `InvestigationChangedEvent` values
contain only `AgentId` and `Enabled`; the copied records can cross into a later
player-intelligence projection without leaking ECS `Entity` handles or LOD state.
`ScheduledDemotionMinute` is `-1` when no reduction is pending and otherwise
stores the earliest next-day boundary computed from elapsed simulation minutes.
`ActiveInteraction` is backed by service-owned reference counts so overlapping
owners cannot clear one another's pins; the flag is removed only on final release.

`AgentAttributeSchema` loads the ordered numeric definitions from
`data/agent-schema.json` and resolves IDs to indexes. Each generated agent stores
one floating-point value per definition, so adding an attribute requires only a
data-file change. Values are sampled from a bounded normal distribution centered
on the configured average and constrained to the configured range.

Intention, activity, effects, and covert state are distinct. `IntentionState`
stores what was selected, `ActivityState` stores what is happening now, and
JSON effect definitions describe attribute changes. Each action declares an
activity ID, display name, and stable hash in `data/actions.json`; execution copies
that identity into `ActivityState` while `ActivityPhase` contains only generic
engine states. Debug snapshots resolve the display name through `ContentCatalog`.
`AgentState.SecretStateHash`
identifies a separate secret activity such as `Surveillance`. Secret states are loaded from
`data/secret-states.json`; the required `none` definition uses hash `0`, making a
default-initialized `AgentState` safe. Agents are spawned with `None`, and a
covert system may change the secret hash without changing intention or activity.

`data/actions.json` owns each candidate's eligibility predicate, base utility,
weighted numeric expressions, piecewise-linear response curves, trait modifiers,
minimum commitment, switching margin, cooldown, urgent-preemption threshold,
per-minute effects, activity identity, target definition, and optional mutual
participation. `TargetDefinition` selects `none`, a
direct agent `location`, or an `entity` query. Entity queries contain a relation,
compiled predicate requirements, ordered compiled numeric rankings, and an
optional positive candidate limit; the runtime result carries both entity and
location IDs alongside eligibility and score. Runtime cooldowns and candidate
caches use parallel fixed-size arrays indexed by the compiled catalogue and do
not need per-agent dictionaries.

`ParticipationDefinition` owns the mutual minimum and maximum duration,
rejection cooldown, and a separately compiled participant acceptance predicate,
utility inputs, and trait modifiers. `CoordinationState` is pure ECS ground
truth describing the accepted pair and lifecycle; it stores hashes, IDs,
scalars, and enums rather than behavior-specific roles or strings. Effect
definitions compile an optional subject enum; omission means the initiator and
participant effects are accepted only on mutual entity actions.

Every action also declares an `ExecutorDefinition`. Loading compiles its
`executor` string to `ExecutorKind` (`performHere`, `performAtLocation`,
`performWithEntity`, or `wait`) and validates the required target type.
Target-bound executors use `destination: "intent.target"`. Generic travel stores
the active destination and shortest route in `AgentTravel`, so execution does
not branch on work, rest, or socialize identity.

Numeric facts use stable `FactId` values composed of a `FactKind` and an optional
schema index. `FactRegistry` resolves authoring references such as
`agent.attribute.fatigue`, `time.minuteOfDay`, `job.workStartMinute`, and
`target.affinity` and `target.attribute.<id>` during catalog loading. Network
targets without a directional social edge receive normalized affinity `0.5`.
`NumericExpressionDefinition` is the
JSON authoring tree; the loader compiles it to a bounded postfix instruction
array containing opcodes, fact handles, and numeric operands. The runtime stack
evaluator uses `stackalloc`, performs no string lookup, and supports `fact`,
`constant`, `normalize`, `normalizeRange`, arithmetic, `min`, `max`, `clamp`,
`oneMinus`, and `abs`. Trees are limited to 16 levels and 64 instructions.

`PredicateDefinition` is the eligibility authoring tree. It composes typed
boolean facts with `and`, `or`, and `not`, or compares numeric expressions with
`equal`, `notEqual`, `less`, `lessOrEqual`, `greater`, and `greaterOrEqual`.
`CompiledPredicate` stores bounded postfix boolean instructions and
precompiled numeric operands. Boolean/numeric type mismatches and malformed
arity fail during catalog loading; runtime evaluation uses a stack-allocated
boolean span and performs no string parsing or per-agent allocation.

Binary attributes are traits defined in `data/traits.json`. Their unique positive
single-bit values are combined in `Psychology.TraitMask`; `prevalence` controls the
independent probability that a generated agent has each trait. The `long` mask
currently supports up to 63 positive single-bit traits.

`Identity.OccupationId` stores the stable hash of the agent's assigned job. Jobs
are loaded from `data/jobs.json`; each job defines an integer start and end
minute, a set of workdays from 1 through 7, the required workplace type, a
public or private sector, weekly pay in simulation credits, prestige from 1 to
100, and an optional selection method. Appointed jobs also identify the elected
job allowed to appoint them. The schedule is used by activity decisions and
coarse routine profile generation. `PoliticalParticipation` keeps candidate,
ballot, office history, and polling trip state on every LOD tier.

World locations are loaded from `data/world.json` as typed nodes connected by
bidirectional edges. Each location has a stable integer hash, and each edge
has a positive travel duration in in-world minutes. `WorldTopology` validates
the graph and calculates deterministic shortest-time routes. Spawned agents
store their home, workplace, current location, and route in the location and
travel components above.

### 2.2 The Social Graph (Edge Entities)

To model the social network and intelligence discovery, relationships are created as distinct Entities containing the `EdgeData` component, linking two agents.

```csharp
public struct EdgeData : IComponent {
    public Entity Source;
    public Entity Target;
    public float Affinity;       // -100 to 100

    // KNOWLEDGE MASKS (Parallel Bitmasks)
    // 1 = Source knows this data about Target; 0 = Hidden
    public long KnownTraitMask;      
    public byte KnownStatsMask;      
    public byte KnownPoliticalMask;  
}

```

`SocialGraphBuilder` creates five unique peers for each agent using the
injected random source. Each peer relationship is stored as two directed edge
entities, one in each direction, with independent knowledge masks. Populations
smaller than six receive the largest valid graph degree for their size. Self-links
and duplicate peers are not created.

`InteractionSystem` processes the packed outgoing edges of eligible detailed
sources on the configured interval (60 ECS ticks by default), rather than
scanning the entire edge population. A source's d100 plus `perception` competes with the target's
d100 plus `willpower`; a target with the `paranoid` trait receives a 20-point
willpower bonus. A successful contest reveals one present, previously unknown
trait by OR-ing its bit into `KnownTraitMask`. The mask records confirmed
present traits only, so confirmed absence is not represented. Affinity is the
normalized percentage of configured traits shared by the target and the
source's known mask.

`AgentSocialIndexes` is the persistent, non-ECS lookup snapshot created after
agent, network, and social-edge generation. Its direct agent directory is
indexed by integer entity ID. Outgoing relationships are stored in one packed
array of `SocialEdgeIndexEntry(TargetAgentId, EdgeEntityId)` values, ordered by
source agent ID, target agent ID, and edge entity ID; a compact source-range
table provides a `ReadOnlySpan<SocialEdgeIndexEntry>` for one agent without a
dictionary or allocation. Construction uses fixed-pass integer radix sorting,
so build work and retained storage are linear in generated entities and directed
edges.

Milestone 17.3 adds a persistent edge-entity directory beside the packed
adjacency data. Decision targeting uses the packed edge ID to retrieve current
affinity without rebuilding an affinity map, while target location and
attribute arrays are read from the agent directory's resolved ECS entity.
Network target enumeration deliberately remains in Friflo's native
`AgentNetworkMembership` incoming-link and relation storage rather than adding
a duplicate network-member snapshot.

Milestone 17.4 also uses the direct agent directory to resolve a moving
intention target's current `AgentLocation`; execution no longer builds a
full-population location dictionary on every tick. Deleted or component-missing
targets follow the ordinary target-availability invalidation path.

The snapshot is immutable between rebuilds in Milestone 17.2. Code that changes
the population must call `NotifyPopulationChanged`; code that adds, removes, or
retargets `EdgeData` must call `NotifySocialGraphChanged`. Lookups then fail
explicitly until `Rebuild(EntityStore)` is called, preventing stale entity IDs
from being consumed. Dynamic social mutation itself remains out of scope.

### 2.3 Debug Inspection Snapshots

Debug inspection uses immutable copies rather than exposing `Entity` instances to ImGui. `DebugAgentSnapshot` contains the scalar identity, occupation, faction, public action, secret-state, and trait-mask values plus read-only collections for schema-defined attributes, every configured trait's present/absent state, named locations, travel state, resolved network memberships, and an optional copied coordination snapshot. `DebugCoordinationSnapshot` contains partner, role, status, acceptance/performance times, duration bounds, current utilities, and release request. `DebugNetworkMembershipSnapshot` copies a network ID/display name/type, role hash/name, and optional supervisor ID/display name. `DebugNetworkSnapshot` copies a network's identity, resolved type, optional named anchor, and member count. `DebugInspectionSnapshot` groups the agent and network collections passed to the debug UI. `DebugSnapshotBuilder` is the ECS boundary that creates these snapshots; `DebugWindow` renders only the copied values. None of the network or coordination projections enter `PlayerIntelligenceDB`.

`DebugDecisionCandidateSnapshot` copies candidate eligibility, rejection path,
target IDs, utility and trait contributions, cooldown, commitment state, final
score, and winner status. These ground-truth diagnostics exist only in debug
inspection snapshots and are not fields of `PlayerIntelligenceDB`.

### 2.4 World-Time Presentation Snapshot

`WorldTimeSnapshot` is an immutable UI-facing copy of the `WorldTime` calendar fields:

```csharp
public readonly record struct WorldTimeSnapshot(
    int DayNumber,
    int DayOfWeek,
    int MinuteOfDay);
```

The application creates this snapshot after the clock and simulation systems update. `WorldTimeBar` renders it without retaining or querying the ECS clock entity, preserving the intelligence isolation boundary for the ImGui layer.

### 2.5 Application Launcher State

`ApplicationId` identifies the presentation applications exposed by the launcher:

```csharp
public enum ApplicationId
{
    Dossiers,
    DebugWindow
}
```

`ApplicationIcon` pairs an application identifier with its launcher label and compact icon glyph. `ApplicationShell` keeps the selected icon and open/closed presentation state for the `Surveillance Terminal` and `Debug Window`; it contains no ECS entity references or simulation data.

### 2.6 Operative Intelligence Snapshots

`OperativeTag` identifies the five randomly selected agents controlled by the
player's team. `SimulationDefaults.OperativeCount` is `5`; smaller populations
select all available agents. Selection uses the spawner's injected `Random`, so
seeded runs reproduce the same team membership.

`PlayerIntelligenceAgentSnapshot` contains an agent ID, name ID, Operative
marker, intelligence role, team-known trait mask, and sanitized investigation
flag. It intentionally omits the ground-truth secret state and all LOD details.
`PlayerIntelligenceDB` owns a sorted snapshot array exposed through a read-only
view plus the selected Operative IDs. Its one-time creation boundary scans
outgoing edges whose source has `OperativeTag` and combines their known trait
masks per target with bitwise `OR`; subsequent stable-ID lookups use binary
search. It does not copy `Psychology` or retain ECS entities. Operative discovery
and investigation events replace only affected copied entries.

`OperativeTraitDiscoveryEvent` contains a target stable ID and sanitized known
mask. `InvestigationChangedEvent` contains a stable ID and enabled flag.
`InvestigationCommand` carries the same presentation request into the
simulation-owned `InvestigationCommandQueue`; its result reports accepted and
rejected command counts without exposing `AgentLodService` to ImGui.
`PlayerIntelligenceProjectionDiagnostics` records one-time agent/edge visits and
incremental replacements so scaling tests can prove that reads do not rescan ECS.
`AgentIdentitySearchIndex` caches the indexes of identity rows matching agent ID
or display name and invalidates only for new search text or a changed stable
identity version. `VisibleRowRange` is the pure counterpart of the ImGui list
clipper and makes visible-row work counts testable at full population scale.
Operatives are assigned the `Officer` role
when spawned; other agents default to `None`. The dossier displays non-empty
roles alongside each agent and applies each configured trait bit with
`knownMask & trait.Bit` before choosing a visible name or `Trait: ???`.

`DebugAgentIdentitySnapshot` is the lightweight row contract for debug search.
`DebugInspectionView` pairs those immutable rows and copied network summaries
with zero or one `DebugAgentSnapshot`. `DebugInspectionProjection` alone retains
the simulation dependencies needed to copy that selected detail on demand; the
view passed to ImGui retains neither `EntityStore` nor `Entity`. Debug agent
detail additionally contains materialized/desired LOD tier, nullable pending
demotion minute, and coarse-profile ID/fingerprint. Those fields deliberately
do not appear in `PlayerIntelligenceAgentSnapshot`.

### 2.7 Agent Network Catalog

`data/networks.json` is the static source of network types, roles, and generation
policies. `AgentNetworkCatalog` validates that document at startup and exposes
immutable `NetworkTypeDefinition`, `NetworkRoleDefinition`, and
`NetworkGeneratorDefinition` records. Identifiers and references are converted
to stable FNV-1a integer hashes during loading; lookups by either ID or hash are
cached dictionaries and do not perform runtime string searches.

Hierarchy is deliberately limited to `Flat` and `SingleSupervisor`. Partitioning
is also a registered set (`HomeLocation`, `WorkLocation`, and `Global`), rather than a
content-supplied query language. A generator contains bounded weighted sizes,
an explicit remainder policy, data-driven role hashes, and (only for a
single-supervisor hierarchy) span-of-control and depth limits. Runtime ECS
network instances use `AgentNetworkData` ECS entities. `TypeHash` selects their
validated type, `AnchorLocationId` is the partition location (or zero for an
unanchored network), and `Ordinal` provides deterministic identity within a
generated series. Global partitions use anchor `0`. `SeedsSocialGraph` allows a
type to request reciprocal clique edges after generation; it is enabled for
families and friend groups and disabled for companies.

Agent membership is an `AgentNetworkMembership : ILinkRelation` stored on the
agent and keyed by its `Network` entity. It carries the data-driven `RoleHash`
and an optional `Supervisor` entity. The relation key guarantees at most one
membership per agent/network pair while still allowing unrelated family,
friend-group, and company memberships. The supervisor is intentionally not another relation key,
so deletion cleanup must update it explicitly.

`AgentNetworkService` is the only supported mutation boundary. It validates
live agent/network entities, catalog roles, per-type cardinality, flat versus
single-supervisor rules, and acyclic supervision. A removed manager's direct
reports move to that manager's supervisor. A live root can be removed only with
an explicit direct-report successor; when an agent is deleted externally, the
lowest-entity-ID direct report succeeds a deleted root deterministically.
Deleting a network removes every incoming membership before deleting its ECS
entity. Its type, anchor, and ordinal are immutable after creation.

Persistent network storage remains linear in population. ECS data contains no
display strings, member arrays, dictionaries, descendant closures, or redundant
supervisor indexes: each generated agent contributes one family, one friend-group,
and one company relation. Resolved names and summary collections are transient
debug projections and are discarded after presentation.

### 2.8 Compiled Intent Catalog

`ActionDefinition` remains the JSON authoring model. `IntentCompiler` resolves
its fact, trait, attribute, target, and executor strings once at catalog load.
The resulting `CompiledIntent` stores compiled predicate/numeric programs,
trait bits, attribute indexes, compact target/executor enums, stable content
hashes, and a dense `ushort RuntimeIndex`. `CompiledIntentCatalog` owns the
index-ordered array and a hash-to-index map; simulation systems consume this
catalog rather than mutable authoring trees.

`IntentBitSet` maps the same dense runtime index to one bit in a packed
`ulong[]`. `IntentCandidateIndex`, built by `CompiledIntentCatalog`, owns the
global non-fallback set and the sets that remain available without a job, home,
workplace, social relation, or network relation. Public immutable intersections support tooling
and tests. The decision hot path instead intersects words through a struct
enumerator, visiting set bits without allocating or scanning the compiled
intent array. Target/executor contracts are the source of these conservative
prerequisites; content authors do not maintain a second set of flags.

Exactly one authoring definition must set `fallback: true`. Compilation requires
that fallback to use target kind `none` and executor `wait`, producing a safe
no-op decision when ordinary candidates are unavailable. Compiler failures use
`actions.json:actions[index].field` paths so content authors can locate invalid
references and incompatible structures directly.

### 2.9 Decision Dependencies and Cache

`FactDependencyMask` combines a flagged `FactDependencyCategory` with a 64-bit
schema-attribute bitset. `CompiledNumericExpression` and `CompiledPredicate`
derive masks from their pre-resolved instructions; `CompiledIntent` adds trait
and target-query dependencies. Categories include social targets, network
targets, target attributes, target location/affinity, and coordination. No
dependency list is authored in JSON.

`DecisionState.ChangedFacts` accumulates mutation signals until consideration.
`Dirty` records that ordinary dependencies changed, while the flagged
`DecisionWakeReason` distinguishes target loss, coordination lifecycle,
investigation, and promotion events that cannot wait for a Tier 2 cadence
boundary. Both are cleared only after a decision pass; ordinary Tier 2 changes
therefore remain accumulated for the next data-defined interval.
Its parallel score, eligibility, and target arrays are indexed by the compiled
intent's dense runtime index, avoiding dictionaries per agent. `EvaluationCount`
is diagnostic Ground Truth state and is not copied into player intelligence.

### 2.10 Simulation Work Diagnostics

`SimulationWorkDiagnostics` is a mutable, system-owned measurement sink with
64-bit counters for decision passes, candidate evaluations, target population
visits, edge visits, and transient allocation-sensitive operations.
`SimulationWorkSnapshot` is its immutable value projection for tests and
benchmark output. Neither type is an ECS component, and neither is exposed to
the UI or `PlayerIntelligenceDB`; instrumentation is opt-in at system
construction and does not affect decision state.

### 2.11 Tier 3 Routine Contracts

`data/lod.json` owns the coarse weekly routine templates. A workday and a
non-workday each contain exactly one `fillRemaining` fixed segment. Workdays
also contain exactly one `jobWork`, `commuteToWork`, and `commuteHome` segment.
Every segment identifies an existing intent, its symbolic home/work location,
and the authored effect role. `ContentCatalog` compiles these values into
`CompiledCoarseRoutineSegment` records carrying an intent hash, dense runtime
intent index, compact segment/location enums, and `EffectSubject`; no Tier 3
runtime system parses a routine or branches on named intent IDs. Trait duration
rules are compiled from trait IDs and segment IDs into a trait bit and segment
index. The catalog rejects unknown references, duplicate IDs, invalid roles,
negative or missing fixed durations, bad fill counts, invalid routine kinds,
and infeasible job commute windows with `lod.json` paths for content authors.

### 2.12 Launch Options and Coarse Work Count

`ApplicationOptions` is an immutable startup value containing `AgentCount` and
`DebugMode`. Its pure parser preserves the 1,000-agent default and accepts a
single invariant-culture positive integer after `--agents`, bounded by
`MaximumAgentCount` (100,000). It is not an ECS component or content structure.

`CoarseRoutineSystem.AgentVisits` is a monotonic 64-bit diagnostic counter. It
increments only when a registered coarse agent enters `CatchUp`, including
scheduled shard work and promotion catch-up. `AgentLodService.CoarseAgentVisits`
exposes the value read-only for scale verification; presentation projections do
not contain it.

### 2.13 Political Faction State and Strategy

`FactionParticipation` is an ECS component on every spawned agent. It stores
the one current faction membership, the role (`Volunteer`, `Activist`,
`Leader`, or `None`), the former occupation used when faction staff leave, and
the last day on which the agent considered recruitment. Its `AppliedForStaff`
flag records a volunteer's application for activist work. Political alignment
remains a separate preference and does not itself create membership.

`factions.json` defines a faction's elected leader job, leader-selected
activist job, meta goal, and subgoal definitions. Each subgoal names its
action, progress metric, target, daily progress, priority, and prerequisite
IDs. `ContentCatalog` validates faction job ownership, action/metric names,
and prerequisite references. `FactionSnapshot` is an immutable projection of
current membership and influence measures; mutable progress counters live in
`PoliticalFactionSystem`, not on agents or in UI state.

### 2.14 Headless Political Diagnostics

`ApplicationOptions` includes the headless mode flag, optional day count,
deterministic seed (default 17001), and report prefix. Headless runs require
`--days` and support 1–3650 days and 1–100,000 agents. They write a Markdown
and a JSON file by appending `.md` and `.json` to the prefix.

`PoliticalHeadlessReport` is the versioned report root (`SchemaVersion` is
currently 1). It contains seed/population/run metadata, one `DailyFactionDiagnostic`
per faction per day, the final elected/appointed `PoliticalOfficeholder` list,
one `ElectionDiagnosticResult` per resolved cycle, and a chronological sequence
of `PoliticalDiagnosticEvent` records. Office tallies are stored per office and
cycle so future elections do not overwrite prior results. Event agent IDs are
stable simulation entity IDs; faction and office identifiers use content IDs.
The event stream includes political choices and travel, not ordinary movement
or day-to-day work activity. Console progress is emitted through an optional
runner callback and reports startup parameters, day boundaries, elapsed time,
and a remaining-time estimate calculated from completed days.

### 2.15 Political Research Providers

`SurveyDemographicProfile` stores a stable, content-sampled age band, gender,
and education category on each agent. The dedicated survey random stream keeps
these values repeatable for a run seed without perturbing job assignment or
traits. Research sampling uses those profiles, home district, and job sector;
it never uses present or future home status to choose a sample.

`data/research.json` defines weekly cadence, a three-day telephone fieldwork
window, call hours, response and undecided rates, demographic population
targets, and provider methods. Northstar Opinion uses a stratified probability
sample and likely-voter reporting; Townline Research uses demographic quotas
and all-respondent reporting. Both attempt 200 unique agents per wave. At call
time, an interview can complete only if the selected agent is home and opts to
respond. Providers receive either a response or one combined nonresponse
outcome; away status and refusals are never exposed separately.

`OpinionPollSnapshot` is an aggregate containing call and response totals,
effective sample size, raw and calibrated party/candidate percentages, and 95%
Wilson intervals. Candidate estimates are present only when mayoral nominees
exist. `ResearchProviderProjection` copies the latest snapshot and history for
presentation. `PoliticalResearchWindow` accepts only this aggregate and has no
ECS access; each provider application opens and closes independently.
