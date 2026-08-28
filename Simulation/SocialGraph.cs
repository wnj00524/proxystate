using System.Numerics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

/// <summary>
/// Creates a randomized simple undirected graph and stores each pair as two
/// directed edge entities. A shuffled circulant graph gives every agent the
/// requested degree without retry-based random matching getting stuck.
/// </summary>
public sealed class SocialGraphBuilder
{
    private readonly int _relationshipsPerAgent;

    public SocialGraphBuilder(int relationshipsPerAgent = SimulationDefaults.SocialRelationshipsPerAgent)
    {
        if (relationshipsPerAgent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(relationshipsPerAgent));
        }

        _relationshipsPerAgent = relationshipsPerAgent;
    }

    public void Populate(EntityStore store, IReadOnlyList<Entity> agents, Random random)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(random);

        var count = agents.Count;
        if (count < 2 || _relationshipsPerAgent == 0)
        {
            return;
        }

        // An undirected regular graph requires an even degree*vertex count.
        // This also gives sensible behavior for small test populations.
        var degree = Math.Min(_relationshipsPerAgent, count - 1);
        if ((degree * count) % 2 != 0)
        {
            degree--;
        }

        if (degree <= 0)
        {
            return;
        }

        var shuffled = agents.ToArray();
        Shuffle(shuffled, random);

        var halfDegree = degree / 2;
        for (var offset = 1; offset <= halfDegree; offset++)
        {
            for (var index = 0; index < count; index++)
            {
                CreatePair(store, shuffled[index], shuffled[(index + offset) % count]);
            }
        }

        if (degree % 2 == 1)
        {
            var opposite = count / 2;
            for (var index = 0; index < opposite; index++)
            {
                CreatePair(store, shuffled[index], shuffled[index + opposite]);
            }
        }
    }

    private static void CreatePair(EntityStore store, Entity first, Entity second)
    {
        store.CreateEntity(new EdgeData
        {
            Source = first,
            Target = second
        });
        store.CreateEntity(new EdgeData
        {
            Source = second,
            Target = first
        });
    }

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
public sealed class InteractionSystem : QuerySystem<EdgeData>
{
    private readonly Random _random;
    private readonly int _intervalTicks;
    private readonly int _perceptionIndex;
    private readonly int _willpowerIndex;
    private readonly long _allTraitBits;
    private readonly long _paranoidBit;
    private readonly IReadOnlyList<TraitDefinition> _traits;
    private int _ticks;

    public InteractionSystem(
        ContentCatalog catalog,
        Random random,
        int intervalTicks = SimulationDefaults.InteractionIntervalTicks)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(random);
        if (intervalTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalTicks));
        }

        _random = random;
        _intervalTicks = intervalTicks;
        _perceptionIndex = catalog.AgentAttributes.GetIndex("perception");
        _willpowerIndex = catalog.AgentAttributes.GetIndex("willpower");
        _allTraitBits = catalog.AllTraitBits;
        _traits = catalog.Traits;
        _paranoidBit = _traits
            .FirstOrDefault(trait => string.Equals(trait.Id, "paranoid", StringComparison.OrdinalIgnoreCase))
            ?.Bit ?? 0L;
    }

    protected override void OnUpdate()
    {
        _ticks++;
        if (_ticks % _intervalTicks != 0)
        {
            return;
        }

        Query.ForEachEntity((ref EdgeData edge, Entity _) => Interact(ref edge));
    }

    private void Interact(ref EdgeData edge)
    {
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

        edge.Affinity = CalculateAffinity(
            targetPsychology.TraitMask,
            edge.KnownTraitMask,
            _allTraitBits,
            _traits.Count);
    }

    private static float CalculateAffinity(long targetTraitMask, long knownTraitMask, long allTraitBits, int traitCount)
    {
        if (traitCount == 0)
        {
            return 0f;
        }

        var sharedMask = targetTraitMask & knownTraitMask & allTraitBits;
        return BitOperations.PopCount((ulong)sharedMask) * 100f / traitCount;
    }
}
