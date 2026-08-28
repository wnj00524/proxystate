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
    public int OccupationId;
}

public struct PoliticalAlignment : IComponent
{
    public byte FactionId;
    public float Preference;
    public float Salience;
}

public struct BaseStats : IComponent
{
    public byte Intelligence;
    public byte Charisma;
    public byte Perception;
    public byte Willpower;
}

public struct Psychology : IComponent
{
    public long TraitMask;
}

public struct AgentState : IComponent
{
    public float Fatigue;
    public float Stress;
    public float Wealth;
    public int CurrentActionHash;
}

public static class SimulationDefaults
{
    public const int AgentCount = 1_000;
    public const float FatigueStressIncreasePerTick = 0.1f;
    public const float MaximumFatigueStress = 100f;
    public const float MaximumWealth = 10_000f;
}
