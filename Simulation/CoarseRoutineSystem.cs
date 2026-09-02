using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

/// <summary>Owns deterministic Tier 3 shard membership and coarse effect catch-up.</summary>
public sealed class CoarseRoutineSystem
{
    private readonly ContentCatalog _catalog;
    private readonly CoarseRoutineProfileCache _profiles;
    private readonly List<int>[] _shards;
    private readonly Dictionary<int, Entity> _agents = [];

    public CoarseRoutineSystem(ContentCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _profiles = new CoarseRoutineProfileCache(catalog);
        _shards = Enumerable.Range(0, catalog.Lod.Tier3ShardCount).Select(_ => new List<int>()).ToArray();
    }

    public int ProfileCount => _profiles.Count;
    /// <summary>Deterministic count of coarse agents actually visited by shard updates and catch-up.</summary>
    public long AgentVisits { get; private set; }

    public void Add(Entity agent, long currentMinute)
    {
        if (!agent.HasComponent<Identity>() || !agent.HasComponent<Psychology>() || !agent.HasComponent<AgentCommute>() || !agent.HasComponent<AgentLodState>())
            throw new InvalidOperationException("A coarse agent must own common identity, psychology, commute, and LOD state.");
        if (_agents.ContainsKey(agent.Id)) return;
        ref var identity = ref agent.GetComponent<Identity>();
        ref var psychology = ref agent.GetComponent<Psychology>();
        ref var commute = ref agent.GetComponent<AgentCommute>();
        var profile = _profiles.GetOrCreate(identity.OccupationId, psychology.TraitMask, commute.TravelMinutes);
        ref var state = ref agent.GetComponent<AgentLodState>();
        state.CoarseProfileId = profile.Id;
        state.CoarseProfileFingerprint = profile.Fingerprint;
        state.LastCoarseSimulatedMinute = currentMinute;
        _agents.Add(agent.Id, agent);
        var shard = Shard(agent.Id);
        var insertion = _shards[shard].BinarySearch(agent.Id);
        _shards[shard].Insert(~insertion, agent.Id);
    }

    public void Remove(Entity agent)
    {
        if (!_agents.Remove(agent.Id)) return;
        _shards[Shard(agent.Id)].Remove(agent.Id);
    }

    public void UpdateHour(long currentMinute)
    {
        var hour = currentMinute / 60;
        var shard = (int)(hour % _shards.Length);
        if (shard < 0) shard += _shards.Length;
        // IDs are ordered at insertion, so this visits stable membership only.
        var members = _shards[shard];
        for (var index = 0; index < members.Count; index++)
            if (_agents.TryGetValue(members[index], out var agent)) CatchUp(agent, currentMinute);
    }

    public void CatchUp(Entity agent, long currentMinute)
    {
        if (!_agents.ContainsKey(agent.Id)) return;
        AgentVisits++;
        ref var state = ref agent.GetComponent<AgentLodState>();
        if (currentMinute <= state.LastCoarseSimulatedMinute) return;
        if (!_profiles.TryGet(state.CoarseProfileId, out var profile))
            throw new InvalidOperationException("A Tier 3 agent references a missing shared coarse profile.");
        var resolvedProfile = profile ?? throw new InvalidOperationException("A coarse profile lookup returned no profile.");
        var values = agent.GetComponent<AgentAttributes>().Values;
        resolvedProfile.ForEachOverlap(state.LastCoarseSimulatedMinute, currentMinute, (segment, minutes) => ApplyEffects(ref values, segment, minutes));
        agent.GetComponent<AgentAttributes>().Values = values;
        var current = resolvedProfile.GetSegment(currentMinute - 1);
        ref var location = ref agent.GetComponent<AgentLocation>();
        location.CurrentLocationId = current.Location == CoarseRoutineLocation.Home ? location.HomeLocationId : location.WorkLocationId;
        state.LastCoarseSimulatedMinute = currentMinute;
    }

    private void ApplyEffects(ref AgentAttributeValues values, CoarseRoutineInterval segment, int elapsedMinutes)
    {
        var intent = _catalog.Intents[segment.IntentIndex];
        foreach (var effect in intent.Effects)
        {
            if (effect.Subject != segment.EffectRole) continue;
            var definition = _catalog.AgentAttributes.Definitions[effect.AttributeIndex];
            values[effect.AttributeIndex] = Math.Clamp(values[effect.AttributeIndex] + effect.PerMinute * elapsedMinutes,
                definition.Min, definition.Max);
        }
    }

    private int Shard(int id) => (int)((uint)id % (uint)_shards.Length);
}
