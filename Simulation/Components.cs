using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

// LOD tags are intentionally empty. They let systems select update frequency
// without adding per-entity storage to the component data.
public struct Tier1LodTag : ITag { }
public struct Tier2LodTag : ITag { }
public struct Tier3LodTag : ITag { }

public struct Identity : IComponent
{
    public int NameId;
    // This is the stable hash of the assigned JobDefinition.
    public int OccupationId;
}

public struct PoliticalAlignment : IComponent
{
    public byte FactionId;
}

public struct Psychology : IComponent
{
    public long TraitMask;
}

public struct AgentState : IComponent
{
    public int CurrentActionHash;
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

public enum AgentTravelMode : byte
{
    AtHome,
    TravellingToWork,
    AtWork,
    TravellingHome
}

public struct AgentTravel : IComponent
{
    // The route is stored as stable location hashes. It is static for the
    // lifetime of the assignment and is traversed forward or in reverse.
    public int[] RouteLocationIds;
    public int TotalTravelMinutes;
    public int RoutePosition;
    public float RemainingTravelMinutes;
    public AgentTravelMode Mode;
}

// Numeric agent attributes are kept in schema order. The shared schema supplies
// the meaning of each index, avoiding a per-agent dictionary and fixed fields.
public struct AgentAttributes : IComponent
{
    public float[] Values;
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
    public const int DaysPerWeek = 7;
    public const string ResidentialLocationType = "residential";
}
