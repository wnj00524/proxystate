using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

public sealed class DummyAgentSpawner
{
    private readonly ContentCatalog _catalog;

    public DummyAgentSpawner(ContentCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public int Spawn(EntityStore store, int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (var index = 0; index < count; index++)
        {
            var entity = store.CreateEntity(
                new Identity
                {
                    NameId = random.Next(),
                    OccupationId = random.Next()
                },
                new PoliticalAlignment
                {
                    FactionId = _catalog.Factions[random.Next(_catalog.Factions.Count)].FactionId,
                    Preference = NextUnitFloat(random),
                    Salience = NextUnitFloat(random)
                },
                new BaseStats
                {
                    Intelligence = NextByteStat(random),
                    Charisma = NextByteStat(random),
                    Perception = NextByteStat(random),
                    Willpower = NextByteStat(random)
                },
                new Psychology
                {
                    TraitMask = CreateTraitMask(random)
                },
                new AgentState
                {
                    Fatigue = NextThresholdFloat(random),
                    Stress = NextThresholdFloat(random),
                    Wealth = (float)(random.NextDouble() * SimulationDefaults.MaximumWealth),
                    CurrentActionHash = _catalog.Actions[random.Next(_catalog.Actions.Count)].Hash
                },
                Tags.Get<Tier1LodTag>());

            // Keep the local variable assignment explicit: it documents that
            // entity creation is the only structural operation in this loop.
            _ = entity;
        }

        return count;
    }

    private long CreateTraitMask(Random random)
    {
        var mask = 0L;
        foreach (var trait in _catalog.Traits)
        {
            if (random.Next(2) == 1)
            {
                mask |= trait.Bit;
            }
        }

        return mask;
    }

    private static byte NextByteStat(Random random) => (byte)random.Next(1, 101);

    private static float NextThresholdFloat(Random random) => (float)(random.NextDouble() * SimulationDefaults.MaximumFatigueStress);

    private static float NextUnitFloat(Random random) => (float)random.NextDouble();
}
