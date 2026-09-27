using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class PlayerIntelligenceProjectionTests
{
    [Fact]
    public void ProjectionUnionsBootstrapMasksAndAppliesDiscoveriesIncrementally()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = store.CreateEntity(new Identity { NameId = 1 }, Tags.Get<OperativeTag>());
        var second = store.CreateEntity(new Identity { NameId = 2 }, Tags.Get<OperativeTag>());
        var target = store.CreateEntity(new Identity { NameId = 3 });
        store.CreateEntity(new EdgeData { Source = first, Target = target, KnownTraitMask = 1 });
        store.CreateEntity(new EdgeData { Source = second, Target = target, KnownTraitMask = 4 });

        var projection = PlayerIntelligenceDB.Create(store, catalog);

        Assert.True(projection.TryGetAgent(target.Id, out var initial));
        Assert.Equal(5, initial!.KnownTraitMask);
        Assert.True(projection.Apply(new OperativeTraitDiscoveryEvent(target.Id, 8)));
        Assert.False(projection.Apply(new OperativeTraitDiscoveryEvent(target.Id, 8)));
        Assert.True(projection.TryGetAgent(target.Id, out var updated));
        Assert.Equal(13, updated!.KnownTraitMask);
        Assert.Equal(1, projection.Diagnostics.IncrementalUpdates);
    }

    [Fact]
    public void InteractionPublishesOnlyOperativeDiscoveries()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var operative = CreateDetailedAgent(store, catalog, 100, 0, isOperative: true);
        var ordinary = CreateDetailedAgent(store, catalog, 100, 0);
        var target = CreateDetailedAgent(store, catalog, 1, 1);
        store.CreateEntity(new EdgeData { Source = operative, Target = target });
        store.CreateEntity(new EdgeData { Source = ordinary, Target = target });
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var system = new InteractionSystem(store, catalog, new FixedRandom(), 1, indexes);
        var root = new SystemRoot(store) { system };

        root.Update(default);

        var discovery = Assert.Single(system.DrainOperativeDiscoveries());
        Assert.Equal(target.Id, discovery.TargetAgentId);
        Assert.Equal(1, discovery.KnownTraitMask);
        Assert.Empty(system.DrainOperativeDiscoveries());
    }

    [Fact]
    public void AffinityRefreshPublishesSanitizedTargetToOperativeValues()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var operative = CreateDetailedAgent(store, catalog, 100, 0, isOperative: true);
        var target = CreateDetailedAgent(store, catalog, 1, 0);
        var relationship = store.CreateEntity(new EdgeData
        {
            Source = target,
            Target = operative,
            Affinity = -1
        });
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var system = new InteractionSystem(store, catalog, new FixedRandom(), 1, indexes);
        var root = new SystemRoot(store) { system };

        root.Update(default);

        var change = Assert.Single(system.DrainOperativeAffinityChanges());
        Assert.Equal(target.Id, change.TargetAgentId);
        Assert.Equal(operative.Id, change.OperativeId);
        Assert.Equal(relationship.GetComponent<EdgeData>().Affinity, change.Affinity);
    }

    [Fact]
    public void DossierAppliesSanitizedAffinityChangesIncrementally()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var operative = store.CreateEntity(new Identity(), Tags.Get<OperativeTag>());
        var target = store.CreateEntity(new Identity());
        var intelligence = PlayerIntelligenceDB.Create(store, catalog);
        var change = new OperativeAffinityChangedEvent(target.Id, operative.Id, 42.5f);

        Assert.True(intelligence.Apply(change));
        Assert.False(intelligence.Apply(change));

        Assert.True(intelligence.TryGetAgent(target.Id, out var snapshot));
        var affinity = Assert.Single(snapshot!.RapportAffinities);
        Assert.Equal(operative.Id, affinity.OperativeId);
        Assert.Equal(42.5f, affinity.Affinity);
    }

    [Fact]
    public void InvestigationCommandsUpdateProjectionAndRejectMissingAgents()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, 10, 42);
        var lod = spawner.LodService!;
        var projection = PlayerIntelligenceDB.Create(store, catalog);
        var target = projection.Agents.First(agent => !agent.IsOperative);
        var commands = new InvestigationCommandQueue();
        commands.Enqueue(new InvestigationCommand(target.EntityId, true));
        commands.Enqueue(new InvestigationCommand(int.MaxValue, true));

        var result = commands.Process(lod, projection);

        Assert.Equal(new InvestigationCommandResult(1, 1), result);
        Assert.Equal(0, commands.PendingCount);
        Assert.True(projection.TryGetAgent(target.EntityId, out var updated));
        Assert.True(updated!.IsUnderInvestigation);
        Assert.Equal(1, projection.Diagnostics.IncrementalUpdates);
    }

    [Fact]
    public void StableProjectionDoesNotRescanPopulationWhenRead()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, 20, 42);
        var projection = PlayerIntelligenceDB.Create(store, catalog);
        var agentVisits = projection.Diagnostics.InitializationAgentVisits;
        var edgeVisits = projection.Diagnostics.InitializationEdgeVisits;

        foreach (var agent in projection.Agents)
            Assert.True(projection.TryGetAgent(agent.EntityId, out _));

        Assert.Equal(20, agentVisits);
        Assert.Equal(agentVisits, projection.Diagnostics.InitializationAgentVisits);
        Assert.Equal(edgeVisits, projection.Diagnostics.InitializationEdgeVisits);
        Assert.Equal(0, projection.Diagnostics.IncrementalUpdates);
    }

    [Fact]
    public void PlayerProjectionContractsContainNoGroundTruthOrEntityReferences()
    {
        var forbiddenTypes = new[] { typeof(Entity), typeof(Psychology), typeof(AgentLodState) };
        var contractTypes = new[]
        {
            typeof(PlayerIntelligenceAgentSnapshot),
            typeof(OperativeTraitDiscoveryEvent),
            typeof(OperativeAffinityChangedEvent),
            typeof(OperativeAffinitySnapshot),
            typeof(InvestigationChangedEvent),
            typeof(InvestigationCommand)
        };

        Assert.All(contractTypes, contractType => Assert.DoesNotContain(
            contractType.GetProperties(),
            property => forbiddenTypes.Contains(property.PropertyType)));
        Assert.DoesNotContain(typeof(PlayerIntelligenceAgentSnapshot).GetProperties(),
            property => property.Name.Contains("Lod", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Reason", StringComparison.OrdinalIgnoreCase));
    }

    private static Entity CreateDetailedAgent(EntityStore store, ContentCatalog catalog,
        float perception, long traits, bool isOperative = false)
    {
        var values = catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray();
        values[catalog.AgentAttributes.GetIndex("perception")] = perception;
        values[catalog.AgentAttributes.GetIndex("willpower")] = 1;
        var entity = isOperative
            ? store.CreateEntity(new Identity(), new AgentAttributes { Values = values },
                new Psychology { TraitMask = traits }, Tags.Get<OperativeTag>())
            : store.CreateEntity(new Identity(), new AgentAttributes { Values = values },
                new Psychology { TraitMask = traits });
        entity.AddTag<Tier1LodTag>();
        return entity;
    }

    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    private sealed class FixedRandom : Random
    {
        public override int Next(int minValue, int maxValue) => maxValue - 1;
        public override int Next(int maxValue) => 0;
    }
}
