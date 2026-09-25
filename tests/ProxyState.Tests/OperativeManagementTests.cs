using Friflo.Engine.ECS;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class OperativeManagementTests
{
    [Fact]
    public void FollowTaskProducesSourcedSightingsAndBlocksSecondAssignment()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var system = new OperativeManagementSystem(store, catalog, lod);

        Assert.True(system.Assign(new(operativeId, OperativeTaskKind.Follow, targetId, 121), 10));
        Assert.False(system.Assign(new(operativeId, OperativeTaskKind.Talk, targetId), 10));
        system.Update(10);
        system.Update(70);
        system.Update(130);
        system.Update(131);

        var report = Assert.Single(system.Capture(131).Reports);
        Assert.Equal(operativeId, report.SourceOperativeId);
        Assert.Equal(targetId, report.SubjectAgentId);
        Assert.InRange(report.Confidence, 0f, 1f);
        Assert.True(report.Evidence.Count >= 3);
        Assert.All(report.Evidence, item => Assert.Equal(operativeId, item.SourceOperativeId));
        Assert.Equal(OperativeTaskKind.None,
            store.GetEntityById(operativeId).GetComponent<OperativeAssignment>().Kind);
    }

    [Fact]
    public void RotaEditsAreValidatedAndCopiedToProjection()
    {
        var (store, catalog, lod, operativeId, _) = CreateWorld();
        var system = new OperativeManagementSystem(store, catalog, lod);

        Assert.False(system.SetRota(new(operativeId, 0, 600, 900), 0));
        Assert.True(system.SetRota(new(operativeId, 0b0101010, 600, 900), 0));
        var rota = store.GetEntityById(operativeId).GetComponent<OperativeWorkSchedule>();
        Assert.Equal((byte)0b0101010, rota.WorkDaysMask);
        Assert.Equal(600, rota.WorkStartMinute);
        Assert.Equal(900, rota.WorkEndMinute);
        Assert.Contains(system.Capture(0).Operatives, agent =>
            agent.AgentId == operativeId && agent.WorkDaysMask == 0b0101010);
    }

    [Fact]
    public void TalkTaskAlwaysProducesASeparateAssessmentAndEvidenceOrFailure()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var system = new OperativeManagementSystem(store, catalog, lod,
            new IntelligenceTaskSettings(5, 100, 12, 60, 0.3f, 0.01f, 0.7f, 0.15f));

        Assert.True(system.Assign(new(operativeId, OperativeTaskKind.Talk, targetId), 3));
        system.Update(8);
        var report = Assert.Single(system.Capture(8).Reports);
        Assert.NotEmpty(report.Summary);
        Assert.InRange(report.Confidence, 0f, 1f);
        Assert.All(report.Evidence, item => Assert.Equal("interview", item.Kind));
        Assert.Equal(OperativeTaskKind.None,
            store.GetEntityById(operativeId).GetComponent<OperativeAssignment>().Kind);
    }

    [Fact]
    public void ManagementProjectionContainsOnlyOperativesAndIsAttachedToPlayerIntelligence()
    {
        var (store, catalog, lod, _, _) = CreateWorld();
        var intelligence = PlayerIntelligenceDB.Create(store, catalog);
        var system = new OperativeManagementSystem(store, catalog, lod);
        var projection = system.Capture(0);

        intelligence.Apply(projection);

        Assert.Equal(SimulationDefaults.OperativeCount, projection.Operatives.Count);
        Assert.Same(projection, intelligence.OperativeManagement);
        Assert.DoesNotContain(ApplicationCatalog.GetAvailable(false), item =>
            item.Label is "Agents" or "Reports");
        foreach (var type in new[] { typeof(OperativeSnapshot), typeof(IntelligenceEvidence),
                     typeof(IntelligenceAssessment), typeof(OperativeManagementProjection) })
        {
            Assert.DoesNotContain(type.GetProperties(), property => property.PropertyType == typeof(Entity));
            Assert.DoesNotContain(type.GetFields(), field => field.FieldType == typeof(Entity));
        }
    }

    private static (EntityStore Store, ContentCatalog Catalog, AgentLodService Lod, int OperativeId, int TargetId) CreateWorld()
    {
        var catalog = ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, 20, 7123);
        var intelligence = PlayerIntelligenceDB.Create(store, catalog);
        var operativeId = intelligence.OperativeEntityIds[0];
        var targetId = intelligence.Agents.First(agent => !agent.IsOperative).EntityId;
        var operative = store.GetEntityById(operativeId);
        var target = store.GetEntityById(targetId);
        ref var targetLocation = ref target.GetComponent<AgentLocation>();
        targetLocation.CurrentLocationId = operative.GetComponent<AgentLocation>().CurrentLocationId;
        return (store, catalog, spawner.LodService!, operativeId, targetId);
    }
}
