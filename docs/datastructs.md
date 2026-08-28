## 2. Core ECS Data Structures

The simulation uses pure value-type components and tags implementing the native
`Friflo.Engine.ECS` interfaces. Components contain only simulation state; systems
contain behavior. The namespace for Milestone 1 types is `ProxyState.Simulation`.

### 2.1 Agent Components (Ground Truth)

```csharp
using Friflo.Engine.ECS;

public struct Tier1LodTag : ITag { } // Updated every simulation tick.
public struct Tier2LodTag : ITag { } // Reserved for hourly updates.
public struct Tier3LodTag : ITag { } // Reserved for daily updates.

public struct Identity : IComponent {
    public int NameId;       // Hash mapped to localization.
    public int OccupationId; // Hash mapped to job data.
}

public struct PoliticalAlignment : IComponent {
    public byte FactionId;   // JSON faction ID.
    public float Preference; // 0.0 to 1.0.
    public float Salience;   // 0.0 to 1.0.
}

public struct BaseStats : IComponent {
    public byte Intelligence; // 1 to 100.
    public byte Charisma;     // 1 to 100.
    public byte Perception;   // 1 to 100.
    public byte Willpower;    // 1 to 100.
}

public struct Psychology : IComponent {
    public long TraitMask; // Bits are supplied by data/traits.json.
}

public struct AgentState : IComponent {
    public float Fatigue;          // [0, 100); reset at 100.
    public float Stress;           // [0, 100); reset at 100.
    public float Wealth;           // [0, 10,000].
    public int CurrentActionHash;  // Hash supplied by data/actions.json.
}
```

Milestone 1 creates 1,000 entities with all five components and `Tier1LodTag`.
Dummy values are generated from an injected `Random`; the application supplies a
fresh random generator on launch, while tests can use a fixed seed.

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

---
a
