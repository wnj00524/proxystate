using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

/// <summary>
/// A copied discovery notification. Only discoveries made by an Operative are
/// published, so consumers never need an ECS entity or the target's Psychology.
/// </summary>
public readonly record struct OperativeTraitDiscoveryEvent(int TargetAgentId, long KnownTraitMask);

/// <summary>
/// Creates a randomized simple undirected graph and stores each pair as two
/// directed edge entities. A shuffled circulant graph gives every agent the
/// requested degree without retry-based random matching getting stuck.
/// </summary>
public sealed class SocialGraphBuilder
{
    private readonly int _relationshipsPerAgent;
    private readonly HashSet<int> _socialNetworkTypes;
    private readonly AgentAffinitySettings? _affinity;

    public SocialGraphBuilder(int relationshipsPerAgent = SimulationDefaults.SocialRelationshipsPerAgent)
        : this(null, null, relationshipsPerAgent)
    {
    }

    public SocialGraphBuilder(AgentNetworkCatalog? networks,
        int relationshipsPerAgent = SimulationDefaults.SocialRelationshipsPerAgent)
        : this(networks, null, relationshipsPerAgent)
    {
    }

    public SocialGraphBuilder(AgentNetworkCatalog? networks, AgentAffinitySettings? affinity,
        int relationshipsPerAgent = SimulationDefaults.SocialRelationshipsPerAgent)
    {
        if (relationshipsPerAgent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(relationshipsPerAgent));
        }

        _relationshipsPerAgent = relationshipsPerAgent;
        _affinity = affinity;
        _socialNetworkTypes = networks?.Types.Where(type => type.SeedsSocialGraph)
            .Select(type => type.Hash).ToHashSet() ?? new HashSet<int>();
    }

    public void Populate(EntityStore store, IReadOnlyList<Entity> agents, Random random)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(random);

        var count = agents.Count;
        if (count < 2)
        {
            return;
        }

        var pairs = new HashSet<(int First, int Second)>();
        var familyPairs = BuildFamilyPairs(store);

        // An undirected regular graph requires an even degree*vertex count.
        // This also gives sensible behavior for small test populations.
        var degree = Math.Min(_relationshipsPerAgent, count - 1);
        if ((degree * count) % 2 != 0)
        {
            degree--;
        }

        var shuffled = agents.ToArray();
        Shuffle(shuffled, random);

        var halfDegree = degree / 2;
        for (var offset = 1; offset <= halfDegree; offset++)
        {
            for (var index = 0; index < count; index++)
            {
                CreatePair(store, shuffled[index], shuffled[(index + offset) % count], pairs, familyPairs);
            }
        }

        if (degree % 2 == 1)
        {
            var opposite = count / 2;
            for (var index = 0; index < opposite; index++)
            {
                CreatePair(store, shuffled[index], shuffled[index + opposite], pairs, familyPairs);
            }
        }

        // Content can mark bounded networks as interpersonal groups. Their
        // cliques are unioned with the baseline graph, preserving minimum
        // random degree while avoiding duplicate directional edge entities.
        foreach (var network in store.Query<AgentNetworkData>().Entities.OrderBy(entity => entity.Id))
        {
            var data = network.GetComponent<AgentNetworkData>();
            if (!_socialNetworkTypes.Contains(data.TypeHash)) continue;
            var members = network.GetIncomingLinks<AgentNetworkMembership>()
                .Select(link => link.Entity).OrderBy(entity => entity.Id).ToArray();
            for (var first = 0; first < members.Length; first++)
                for (var second = first + 1; second < members.Length; second++)
                    CreatePair(store, members[first], members[second], pairs, familyPairs);
        }
    }

    private HashSet<(int First, int Second)> BuildFamilyPairs(EntityStore store)
    {
        var pairs = new HashSet<(int First, int Second)>();
        if (_affinity is null) return pairs;
        foreach (var network in store.Query<AgentNetworkData>().Entities)
        {
            if (network.GetComponent<AgentNetworkData>().TypeHash != _affinity.FamilyNetworkTypeHash) continue;
            var members = network.GetIncomingLinks<AgentNetworkMembership>()
                .Select(link => link.Entity.Id).OrderBy(id => id).ToArray();
            for (var first = 0; first < members.Length; first++)
                for (var second = first + 1; second < members.Length; second++)
                    pairs.Add((members[first], members[second]));
        }
        return pairs;
    }

    private void CreatePair(EntityStore store, Entity first, Entity second,
        HashSet<(int First, int Second)> pairs, HashSet<(int First, int Second)> familyPairs)
    {
        var key = first.Id < second.Id ? (first.Id, second.Id) : (second.Id, first.Id);
        if (!pairs.Add(key)) return;
        var isFamily = familyPairs.Contains(key);
        store.CreateEntity(new EdgeData
        {
            Source = first,
            Target = second,
            IsFamily = isFamily,
            Affinity = InitialAffinity(first, second, isFamily)
        });
        store.CreateEntity(new EdgeData
        {
            Source = second,
            Target = first,
            IsFamily = isFamily,
            Affinity = InitialAffinity(second, first, isFamily)
        });
    }

    private float InitialAffinity(Entity source, Entity target, bool isFamily) =>
        _affinity is null ? 0f : AgentAffinityCalculator.Calculate(source, target, isFamily,
            knownTraitMask: 0, allTraitBits: 0, traitCount: 0, _affinity);

    private static void Shuffle(Entity[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }
    }
}

/// <summary>
/// Periodically lets every directed relationship attempt to discover one
/// currently hidden, present trait on its target.
/// </summary>
public sealed class InteractionSystem : QuerySystem<Identity>
{
    private readonly AgentSocialIndexes _socialIndexes;
    private readonly SimulationWorkDiagnostics? _workDiagnostics;
    private readonly Random _random;
    private readonly int _intervalTicks;
    private readonly int _perceptionIndex;
    private readonly int _willpowerIndex;
    private readonly long _allTraitBits;
    private readonly long _paranoidBit;
    private readonly AgentAffinitySettings _affinity;
    private readonly IReadOnlyList<TraitDefinition> _traits;
    private readonly List<OperativeTraitDiscoveryEvent> _operativeDiscoveries = [];
    private int _ticks;

    public InteractionSystem(
        EntityStore store,
        ContentCatalog catalog,
        Random random,
        int intervalTicks = SimulationDefaults.InteractionIntervalTicks,
        AgentSocialIndexes? socialIndexes = null,
        SimulationWorkDiagnostics? workDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(random);
        if (intervalTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalTicks));
        }

        _random = random;
        _socialIndexes = socialIndexes ?? BuildIndexes(store);
        _workDiagnostics = workDiagnostics;
        _intervalTicks = intervalTicks;
        _perceptionIndex = catalog.AgentAttributes.GetIndex("perception");
        _willpowerIndex = catalog.AgentAttributes.GetIndex("willpower");
        _allTraitBits = catalog.AllTraitBits;
        _affinity = catalog.Affinity;
        _traits = catalog.Traits;
        _paranoidBit = _traits
            .FirstOrDefault(trait => string.Equals(trait.Id, "paranoid", StringComparison.OrdinalIgnoreCase))
            ?.Bit ?? 0L;
        Filter.AllTags(Tags.Get<Tier1LodTag>());
    }

    /// <summary>Returns copied Operative discoveries and clears the pending buffer.</summary>
    public OperativeTraitDiscoveryEvent[] DrainOperativeDiscoveries()
    {
        var result = _operativeDiscoveries.ToArray();
        _operativeDiscoveries.Clear();
        return result;
    }

    protected override void OnUpdate()
    {
        _ticks++;
        if (_ticks % _intervalTicks != 0)
        {
            return;
        }

        // Iterate detailed sources, then only their packed adjacency ranges.
        // Unrelated edges therefore never enter the interval's hot path.
        Query.ForEachEntity((ref Identity identity, Entity source) =>
        {
            foreach (var indexedEdge in _socialIndexes.GetOutgoingEdges(source.Id))
            {
                _workDiagnostics?.RecordEdgeVisit();
                if (_socialIndexes.TryGetEdge(indexedEdge.EdgeEntityId, out var edgeEntity) &&
                    !edgeEntity.IsNull && edgeEntity.TryGetComponent<EdgeData>(out _))
                {
                    ref var edge = ref edgeEntity.GetComponent<EdgeData>();
                    Interact(ref edge);
                }
            }
        });
    }

    private static AgentSocialIndexes BuildIndexes(EntityStore store)
    {
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        return indexes;
    }

    private void Interact(ref EdgeData edge)
    {
        var previousKnownTraitMask = edge.KnownTraitMask;
        var sourceAttributes = edge.Source.GetComponent<AgentAttributes>();
        var targetAttributes = edge.Target.GetComponent<AgentAttributes>();
        var targetPsychology = edge.Target.GetComponent<Psychology>();

        var sourceRoll = _random.Next(1, SimulationDefaults.InteractionD100Sides + 1) +
            sourceAttributes.Values[_perceptionIndex];
        var targetWillpower = targetAttributes.Values[_willpowerIndex];
        if ((targetPsychology.TraitMask & _paranoidBit) != 0)
        {
            targetWillpower += SimulationDefaults.ParanoidWillpowerBonus;
        }

        var targetRoll = _random.Next(1, SimulationDefaults.InteractionD100Sides + 1) + targetWillpower;
        if (sourceRoll > targetRoll)
        {
            var availableTraits = new List<TraitDefinition>();
            foreach (var trait in _traits)
            {
                if ((targetPsychology.TraitMask & trait.Bit) != 0 &&
                    (edge.KnownTraitMask & trait.Bit) == 0)
                {
                    availableTraits.Add(trait);
                }
            }

            if (availableTraits.Count > 0)
            {
                var discovered = availableTraits[_random.Next(availableTraits.Count)];
                edge.KnownTraitMask |= discovered.Bit;
            }
        }


        if (edge.KnownTraitMask != previousKnownTraitMask && edge.Source.Tags.Has<OperativeTag>())
        {
            _operativeDiscoveries.Add(new OperativeTraitDiscoveryEvent(
                edge.Target.Id,
                edge.KnownTraitMask & _allTraitBits));
        }

        var previousAffinity = edge.Affinity;
        edge.Affinity = AgentAffinityCalculator.Calculate(
            edge.Source,
            edge.Target,
            edge.IsFamily,
            edge.KnownTraitMask,
            _allTraitBits,
            _traits.Count,
            _affinity);
        if (edge.Affinity != previousAffinity && edge.Source.HasComponent<DecisionState>())
        {
            ref var decision = ref edge.Source.GetComponent<DecisionState>();
            DecisionInvalidation.SignalTargetAvailability(ref decision);
        }
    }

}
