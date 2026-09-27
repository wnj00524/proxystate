using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class AgentAffinityTests
{
    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void SharedFactorsAndFamilyMembershipSeedBothDirectedEdges()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = CreateAgent(store, catalog, ageBand: 2, occupation: 42, home: 3001, wealth: 4000);
        var second = CreateAgent(store, catalog, ageBand: 2, occupation: 42, home: 3001, wealth: 4000);
        var family = store.CreateEntity(new AgentNetworkData
        {
            TypeHash = catalog.Affinity.FamilyNetworkTypeHash
        });
        first.AddRelation(new AgentNetworkMembership { Network = family });
        second.AddRelation(new AgentNetworkMembership { Network = family });

        new SocialGraphBuilder(catalog.Networks, catalog.Affinity, relationshipsPerAgent: 0)
            .Populate(store, [first, second], new Random(1));

        var edges = store.Query<EdgeData>().Entities.Select(edge => edge.GetComponent<EdgeData>()).ToArray();
        Assert.Equal(2, edges.Length);
        Assert.All(edges, edge =>
        {
            Assert.True(edge.IsFamily);
            Assert.Equal(71f, edge.Affinity); // 8 + 8 + 8 + 12 + 35.
        });
    }

    [Fact]
    public void EachSimilarityFactorAndWealthCurveContributeConfiguredValues()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = CreateAgent(store, catalog, ageBand: 1, occupation: 10, home: 1, wealth: 0);

        Assert.Equal(8f, Bonus(catalog, first,
            CreateAgent(store, catalog, ageBand: 1, occupation: 20, home: 2, wealth: 10_000)));
        Assert.Equal(8f, Bonus(catalog, first,
            CreateAgent(store, catalog, ageBand: 0, occupation: 10, home: 2, wealth: 10_000)));
        Assert.Equal(8f, Bonus(catalog, first,
            CreateAgent(store, catalog, ageBand: 0, occupation: 20, home: 1, wealth: 10_000)));
        Assert.Equal(0f, Bonus(catalog, first,
            CreateAgent(store, catalog, ageBand: 0, occupation: 20, home: 2, wealth: 10_000)));
        Assert.Equal(4f, Bonus(catalog, first,
            CreateAgent(store, catalog, ageBand: 0, occupation: 20, home: 2, wealth: 2500)));
        Assert.Equal(12f, Bonus(catalog, first,
            CreateAgent(store, catalog, ageBand: 0, occupation: 20, home: 2, wealth: 0)));
    }

    [Fact]
    public void InteractionCadenceRefreshesWealthBonusAndInvalidatesDecisions()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var source = CreateAgent(store, catalog, ageBand: 0, occupation: 1, home: 1, wealth: 0);
        source.AddComponent(new DecisionState { Dirty = false });
        var target = CreateAgent(store, catalog, ageBand: 1, occupation: 2, home: 2, wealth: 10_000);
        var edgeEntity = store.CreateEntity(new EdgeData { Source = source, Target = target });
        ref var sourceAttributes = ref source.GetComponent<AgentAttributes>();
        sourceAttributes.Values[catalog.Affinity.WealthAttributeIndex] = 9000;

        var system = new InteractionSystem(store, catalog, new Random(17), intervalTicks: 2);
        var root = new SystemRoot(store) { system };
        root.Update(default);
        Assert.Equal(0f, edgeEntity.GetComponent<EdgeData>().Affinity);
        root.Update(default);

        Assert.Equal(9f, edgeEntity.GetComponent<EdgeData>().Affinity);
        Assert.True(source.GetComponent<DecisionState>().Dirty);
    }

    [Fact]
    public void AffinityContentRejectsInvalidWealthCurves()
    {
        using var content = TemporaryContent.Create();
        var affinityPath = Path.Combine(content.Directory, "affinity.json");
        var json = File.ReadAllText(affinityPath).Replace(
            "{ \"x\": 0.1, \"y\": 9 },\n    { \"x\": 0.25, \"y\": 4 }",
            "{ \"x\": 0.25, \"y\": 9 },\n    { \"x\": 0.1, \"y\": 4 }");
        File.WriteAllText(affinityPath, json);

        var error = Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(content.Directory));
        Assert.Contains("affinity.json:wealthSimilarity", error.Message);
    }

    [Fact]
    public void TraitAffinityCombinesWithSharedBonusesAndClampsAtOneHundred()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var source = CreateAgent(store, catalog, ageBand: 2, occupation: 7, home: 30, wealth: 4000);
        var target = CreateAgent(store, catalog, ageBand: 2, occupation: 7, home: 30, wealth: 4000,
            traits: catalog.AllTraitBits);

        var score = AgentAffinityCalculator.Calculate(source, target, isFamily: true,
            knownTraitMask: catalog.AllTraitBits, catalog.AllTraitBits, catalog.Traits.Count, catalog.Affinity);

        Assert.Equal(100f, score);
    }

    private static float Bonus(ContentCatalog catalog, Entity first, Entity second) =>
        AgentAffinityCalculator.CalculateSharedBonus(first, second, isFamily: false, catalog.Affinity);

    private static Entity CreateAgent(EntityStore store, ContentCatalog catalog, byte ageBand,
        int occupation, int home, float wealth, long traits = 0)
    {
        var values = catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray();
        values[catalog.Affinity.WealthAttributeIndex] = wealth;
        return store.CreateEntity(
            new Identity { OccupationId = occupation },
            new AgentAttributes { Values = values },
            new AgentLocation { HomeLocationId = home, CurrentLocationId = home },
            new SurveyDemographicProfile { AgeBand = ageBand },
            new Psychology { TraitMask = traits },
            Tags.Get<Tier1LodTag>());
    }

    private sealed class TemporaryContent : IDisposable
    {
        private TemporaryContent(string directory) => Directory = directory;
        public string Directory { get; }

        public static TemporaryContent Create()
        {
            var directory = System.IO.Directory.CreateTempSubdirectory("proxystate-affinity-").FullName;
            foreach (var file in System.IO.Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "data"), "*.json"))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            return new TemporaryContent(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
