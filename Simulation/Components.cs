using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

// LOD tags are intentionally empty. They let systems select update frequency
// without adding per-entity storage to the component data.
public struct Tier1LodTag : ITag { }
public struct Tier2LodTag : ITag { }
public struct Tier3LodTag : ITag { }
// Tier 1 and Tier 2 both retain the detailed component set. Systems introduced
// during the staged rollout can select that shared capability explicitly.
public struct DetailedSimulationTag : ITag { }

public enum AgentLodTier : byte
{
    Tier1 = 1,
    Tier2 = 2,
    Tier3 = 3
}

[Flags]
public enum AgentInterestReason : byte
{
    None = 0,
    Operative = 1 << 0,
    Investigation = 1 << 1,
    RelatedPointOfInterest = 1 << 2,
    ActiveInteraction = 1 << 3
}

// This compact state stays on every LOD tier. Profile fields are reserved for
// Milestone 19; zero/-1 values mean that no coarse routine has been assigned.
public struct AgentLodState : IComponent
{
    public AgentLodTier DesiredTier;
    public int DirectPoiReferenceCount;
    public AgentInterestReason InterestReasons;
    public long ScheduledDemotionMinute;
    public int CoarseProfileId;
    public ulong CoarseProfileFingerprint;
    public long LastCoarseSimulatedMinute;
}

// None is the zero value so manually created agents do not accidentally gain
// an intelligence assignment before a simulation or content system sets one.
public enum IntelligenceRole : byte
{
    None,
    Officer,
    Agent,
    Informant
}

// Operatives are the player-controlled intelligence sources. Keeping team
// membership as an ECS tag lets simulation systems and the UI boundary query
// the same authoritative assignment without exposing entities to ImGui.
public struct OperativeTag : ITag { }

public struct Identity : IComponent
{
    public int NameId;
    // This is the stable hash of the assigned JobDefinition.
    public int OccupationId;
    public IntelligenceRole IntelligenceRole;
}

public struct PoliticalAlignment : IComponent
{
    public byte FactionId;
}

public struct Psychology : IComponent
{
    public long TraitMask;
}

// Social relationships are first-class entities so each direction can carry
// its own intelligence state about the other agent.
public struct EdgeData : IComponent
{
    public Entity Source;
    public Entity Target;
    public float Affinity;
    public long KnownTraitMask;
    public byte KnownStatsMask;
    public byte KnownPoliticalMask;
}

public struct AgentState : IComponent
{
    // A secret state is independent from the public action so an agent can
    // appear to be working while covertly performing another activity.
    // Hash zero is reserved for the content-defined None state.
    public int SecretStateHash;
}

// An intention is the outcome of deliberation. It is deliberately separate
// from both the activity currently being performed and its state effects.
public struct IntentionState : IComponent
{
    public int ActionHash;
    public int TargetEntityId;
    public int TargetLocationId;
    public long SelectedAtMinute;
    public float Utility;
}

// Phases describe execution mechanics only. The activity's domain meaning is
// supplied by ActivityTypeHash from the content catalog.
public enum ActivityPhase : byte
{
    Idle,
    Waiting,
    Moving,
    Performing,
    Blocked
}

public enum CoordinationRole : byte { None, Initiator, Participant }
public enum CoordinationStatus : byte { None, Reserved, Travelling, Waiting, Performing }

// Mutual activity state is deliberately mechanical: action data supplies the
// social meaning, while this component only owns pairing and timing.
public struct CoordinationState : IComponent
{
    public int PartnerEntityId;
    public int ActionHash;
    public CoordinationRole Role;
    public CoordinationStatus Status;
    public long AcceptedAtMinute;
    public long StartedAtMinute;
    public int MinimumDurationMinutes;
    public int MaximumDurationMinutes;
    public float Utility;
    public bool ReleaseRequested;

    public readonly bool Active => PartnerEntityId != 0 && Role != CoordinationRole.None;
}

public struct ActivityState : IComponent
{
    // This public action moved out of AgentState. SecretStateHash therefore
    // remains an independent covert/public boundary.
    public int ActionHash;
    public int ActivityTypeHash;
    public ActivityPhase Phase;
    public long StartedAtMinute;
}

[System.Runtime.CompilerServices.InlineArray(32)]
public struct DecisionFloatArray { 
    private float _e0; 
    public int Length => 32;
    public static implicit operator DecisionFloatArray(float[] values) {
        var a = new DecisionFloatArray();
        if (values != null) for (int i=0; i<values.Length && i<32; i++) a[i] = values[i];
        return a;
    }
}

[System.Runtime.CompilerServices.InlineArray(32)]
public struct DecisionBoolArray { 
    private bool _e0; 
    public int Length => 32;
    public static implicit operator DecisionBoolArray(bool[] values) {
        var a = new DecisionBoolArray();
        if (values != null) for (int i=0; i<values.Length && i<32; i++) a[i] = values[i];
        return a;
    }
}

[System.Runtime.CompilerServices.InlineArray(32)]
public struct DecisionIntArray { 
    private int _e0; 
    public int Length => 32;
    public static implicit operator DecisionIntArray(int[] values) {
        var a = new DecisionIntArray();
        if (values != null) for (int i=0; i<values.Length && i<32; i++) a[i] = values[i];
        return a;
    }
}

[System.Runtime.CompilerServices.InlineArray(32)]
public struct DecisionLongArray { 
    private long _e0; 
    public int Length => 32;
    public static implicit operator DecisionLongArray(long[] values) {
        var a = new DecisionLongArray();
        if (values != null) for (int i=0; i<values.Length && i<32; i++) a[i] = values[i];
        return a;
    }
}

public struct DecisionState : IComponent
{
    public long LastConsideredMinute;
    public bool Dirty;
    public FactDependencyMask ChangedFacts;
    // Ordinary dependency changes may wait for a Tier 2 cadence boundary.
    // Wake reasons identify lifecycle changes that require an immediate pass.
    public DecisionWakeReason ImmediateWakeReasons;
    public long EvaluationCount;
    public DecisionFloatArray CachedScores;
    public DecisionBoolArray CachedEligibility;
    public DecisionIntArray CachedTargetEntityIds;
    public DecisionIntArray CachedTargetLocationIds;
    // Debug diagnostics are allocated only when the decision system is created
    // with diagnostics enabled. They never cross into player intelligence.
    public float[][] CachedUtilityContributions;
    public float[][] CachedTraitContributions;
    public string[] CachedRejectedPredicates;
    // Parallel arrays avoid a dictionary allocation per agent. There are only
    // three candidates in this first slice.
    public DecisionIntArray CooldownActionHashes;
    public DecisionLongArray CooldownUntilMinutes;
}

[Flags]
public enum DecisionWakeReason : byte
{
    None = 0,
    TargetLoss = 1 << 0,
    CoordinationLifecycle = 1 << 1,
    Investigation = 1 << 2,
    Promotion = 1 << 3
}

public struct WorldTime : IComponent
{
    // Simulation time is stored as seconds so the clock can advance smoothly
    // even though job schedules are compared at whole in-world minutes.
    public double ElapsedSimulationSeconds;
    public double DeltaSimulationSeconds;

    public int DayIndex => (int)Math.Floor(ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerDay);
    public int DayOfWeek => (DayIndex % SimulationDefaults.DaysPerWeek) + 1;
    public int MinuteOfDay => (int)(Math.Floor(ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute) % SimulationDefaults.SimulationMinutesPerDay);
}

public struct AgentLocation : IComponent
{
    public int HomeLocationId;
    public int WorkLocationId;
    public int CurrentLocationId;
}

// Tier 3 keeps only the scalar route cost needed for its shared itinerary.
// Detailed route arrays remain exclusively in AgentTravel after promotion.
public struct AgentCommute : IComponent
{
    public int TravelMinutes;
}

// A network instance is an ECS entity. Type and role identifiers are stable
// catalog hashes; an anchor of zero deliberately represents no location.
public struct AgentNetworkData : IComponent
{
    public int TypeHash;
    public int AnchorLocationId;
    public int Ordinal;
}

// Membership is keyed by its network, allowing one agent to participate in
// several unrelated networks without allocating intermediary edge entities.
public struct AgentNetworkMembership : ILinkRelation
{
    public Entity Network;
    public int RoleHash;
    public Entity Supervisor;

    public readonly Entity GetRelationKey() => Network;
}

public enum AgentTravelMode : byte
{
    Stationary,
    Travelling
}

public struct AgentTravel : IComponent
{
    // The route is stored as stable location hashes. It is static for the
    // lifetime of the assignment and is traversed forward or in reverse.
    public int[] RouteLocationIds;
    public int TotalTravelMinutes;
    public int RoutePosition;
    public float RemainingTravelMinutes;
    public int DestinationLocationId;
    public AgentTravelMode Mode;
}

[System.Runtime.CompilerServices.InlineArray(16)]
public struct AgentAttributeValues
{
    private float _e0;
    public int Length => 16;
    public static implicit operator AgentAttributeValues(float[] values) {
        var a = new AgentAttributeValues();
        if (values != null) for (int i=0; i<values.Length && i<16; i++) a[i] = values[i];
        return a;
    }
    public float[] ToArray() {
        var arr = new float[16];
        for (int i=0; i<16; i++) arr[i] = this[i];
        return arr;
    }
}

// Numeric agent attributes are kept in schema order. The shared schema supplies
// the meaning of each index, avoiding a per-agent dictionary and fixed fields.
public struct AgentAttributes : IComponent
{
    public AgentAttributeValues Values;
}

public static class SimulationDefaults
{
    public const int AgentCount = 1_000;
    public const float FatigueStressIncreasePerTick = 0.1f;
    public const float MaximumFatigueStress = 100f;
    public const float MaximumWealth = 10_000f;
    public const double RealSecondsPerSimulationDay = 600d;
    public const double SimulationSecondsPerDay = 86_400d;
    public const double SimulationSecondsPerMinute = 60d;
    public const int SimulationMinutesPerDay = 1_440;
    public const int SimulationMinutesPerWeek = SimulationMinutesPerDay * DaysPerWeek;
    public const int DaysPerWeek = 7;
    public const string ResidentialLocationType = "residential";
    public const int SocialRelationshipsPerAgent = 5;
    public const int OperativeCount = 5;
    public const int InteractionIntervalTicks = 60;
    public const int InteractionD100Sides = 100;
    public const int ParanoidWillpowerBonus = 20;
}
