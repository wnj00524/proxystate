using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class SimulationTests
{
    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void CoreDataTypesUseFrifloComponentAndTagInterfaces()
    {
        var components = new[]
        {
            typeof(Identity),
            typeof(PoliticalAlignment),
            typeof(BaseStats),
            typeof(Psychology),
            typeof(AgentState)
        };
        var tags = new[] { typeof(Tier1LodTag), typeof(Tier2LodTag), typeof(Tier3LodTag) };

        Assert.All(components, type => Assert.True(typeof(IComponent).IsAssignableFrom(type), type.Name));
        Assert.All(tags, type => Assert.True(typeof(ITag).IsAssignableFrom(type), type.Name));
    }

    [Fact]
    public void SpawnerCreatesTheRequestedPopulationWithRequiredComponentsAndTierOneTag()
    {
        var store = new EntityStore();
        var spawned = new DummyAgentSpawner(LoadCatalog()).Spawn(store, SimulationDefaults.AgentCount, new Random(1234));

        Assert.Equal(SimulationDefaults.AgentCount, spawned);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<Identity>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<PoliticalAlignment>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<BaseStats>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<Psychology>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<AgentState>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<AgentState>().AllTags(Tags.Get<Tier1LodTag>()).Count);
    }

    [Fact]
    public void SpawnerKeepsGeneratedValuesWithinTheDocumentedRanges()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        new DummyAgentSpawner(catalog).Spawn(store, SimulationDefaults.AgentCount, new Random(5678));
        var validFactionIds = catalog.Factions.Select(faction => faction.FactionId).ToHashSet();
        var validActionHashes = catalog.Actions.Select(action => action.Hash).ToHashSet();

        foreach (var entity in store.Query<Identity>().Entities)
        {
            var alignment = entity.GetComponent<PoliticalAlignment>();
            var stats = entity.GetComponent<BaseStats>();
            var psychology = entity.GetComponent<Psychology>();
            var state = entity.GetComponent<AgentState>();

            Assert.Contains(alignment.FactionId, validFactionIds);
            Assert.InRange(alignment.Preference, 0f, 1f);
            Assert.InRange(alignment.Salience, 0f, 1f);
            Assert.InRange(stats.Intelligence, (byte)1, (byte)100);
            Assert.InRange(stats.Charisma, (byte)1, (byte)100);
            Assert.InRange(stats.Perception, (byte)1, (byte)100);
            Assert.InRange(stats.Willpower, (byte)1, (byte)100);
            Assert.Equal(0L, psychology.TraitMask & ~catalog.AllTraitBits);
            Assert.InRange(state.Fatigue, 0f, SimulationDefaults.MaximumFatigueStress);
            Assert.InRange(state.Stress, 0f, SimulationDefaults.MaximumFatigueStress);
            Assert.InRange(state.Wealth, 0f, SimulationDefaults.MaximumWealth);
            Assert.Contains(state.CurrentActionHash, validActionHashes);
        }
    }

    [Fact]
    public void SeededRandomnessProducesTheSameFixture()
    {
        var catalog = LoadCatalog();
        var firstStore = new EntityStore();
        var secondStore = new EntityStore();
        new DummyAgentSpawner(catalog).Spawn(firstStore, 1, new Random(42));
        new DummyAgentSpawner(catalog).Spawn(secondStore, 1, new Random(42));

        var first = firstStore.Query<Identity>().Entities.First();
        var second = secondStore.Query<Identity>().Entities.First();

        Assert.Equal(first.GetComponent<Identity>(), second.GetComponent<Identity>());
        Assert.Equal(first.GetComponent<PoliticalAlignment>(), second.GetComponent<PoliticalAlignment>());
        Assert.Equal(first.GetComponent<BaseStats>(), second.GetComponent<BaseStats>());
        Assert.Equal(first.GetComponent<Psychology>(), second.GetComponent<Psychology>());
        Assert.Equal(first.GetComponent<AgentState>(), second.GetComponent<AgentState>());
    }

    [Fact]
    public void FatigueStressSystemIncreasesBothValuesForTierOneAgents()
    {
        var store = new EntityStore();
        var entity = store.CreateEntity(
            new AgentState { Fatigue = 10f, Stress = 20f },
            Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { new FatigueStressSystem(0.5f) };

        root.Update(default);

        var state = entity.GetComponent<AgentState>();
        Assert.Equal(10.5f, state.Fatigue);
        Assert.Equal(20.5f, state.Stress);
    }

    [Fact]
    public void FatigueAndStressResetIndependentlyAtTheThreshold()
    {
        var store = new EntityStore();
        var entity = store.CreateEntity(
            new AgentState { Fatigue = 99.95f, Stress = 10f },
            Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { new FatigueStressSystem(0.1f) };

        root.Update(default);

        var state = entity.GetComponent<AgentState>();
        Assert.Equal(0f, state.Fatigue);
        Assert.Equal(10.1f, state.Stress);
    }

    [Fact]
    public void NonTierOneAgentsAreNotUpdated()
    {
        var store = new EntityStore();
        var entity = store.CreateEntity(new AgentState { Fatigue = 10f, Stress = 20f });
        var root = new SystemRoot(store) { new FatigueStressSystem(1f) };

        root.Update(default);

        var state = entity.GetComponent<AgentState>();
        Assert.Equal(10f, state.Fatigue);
        Assert.Equal(20f, state.Stress);
    }
}
