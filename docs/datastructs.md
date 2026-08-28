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
}

public struct AgentAttributes : IComponent {
    public float[] Values; // Values are ordered by data/agent-schema.json.
}

public struct Psychology : IComponent {
    public long TraitMask; // Bits are supplied by data/traits.json.
}

public struct AgentState : IComponent {
    public int CurrentActionHash;  // Hash supplied by data/actions.json.
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

public enum AgentTravelMode : byte {
    AtHome, TravellingToWork, AtWork, TravellingHome
}

public struct AgentTravel : IComponent {
    public int[] RouteLocationIds;
    public int TotalTravelMinutes;
    public int RoutePosition;
    public float RemainingTravelMinutes;
    public AgentTravelMode Mode;
}
```

`AgentAttributeSchema` loads the ordered numeric definitions from
`data/agent-schema.json` and resolves IDs to indexes. Each generated agent stores
one floating-point value per definition, so adding an attribute requires only a
data-file change. Values are sampled from a bounded normal distribution centered
on the configured average and constrained to the configured range.

Binary attributes are traits defined in `data/traits.json`. Their unique positive
single-bit values are combined in `Psychology.TraitMask`; `prevalence` controls the
independent probability that a generated agent has each trait. The `long` mask
currently supports up to 63 positive single-bit traits.

`Identity.OccupationId` stores the stable hash of the agent's assigned job. Jobs
are loaded from `data/jobs.json`; each job defines an integer start and end
minute, a set of workdays from 1 through 7, and the required workplace type.

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

### 2.3 Debug Inspection Snapshots

Debug inspection uses immutable copies rather than exposing `Entity` instances to ImGui. `DebugAgentSnapshot` contains the scalar identity, occupation, faction, action, and trait-mask values plus read-only collections for schema-defined attributes, every configured trait's present/absent state, named locations, and the travel route/state. `DebugSnapshotBuilder` is the ECS boundary that creates these snapshots; `DebugWindow` renders only the copied values.
