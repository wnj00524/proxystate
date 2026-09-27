using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
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
        Assert.False(system.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 10));
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
        var rota = store.GetEntityById(operativeId).GetComponent<OperativeRota>();
        Assert.Equal((byte)0b0101010, rota.WorkDaysMask);
        Assert.Equal(600, rota.WorkStartMinute);
        Assert.Equal(900, rota.WorkEndMinute);
        Assert.Contains(system.Capture(0).Operatives, agent =>
            agent.AgentId == operativeId && agent.WorkDaysMask == 0b0101010);
    }

    [Fact]
    public void BuildRapportTaskRecordsAffinityIncreaseAndUpdatesSanitizedProjection()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var intelligence = PlayerIntelligenceDB.Create(store, catalog);
        var settings = new IntelligenceTaskSettings
        {
            RapportDurationMinutes = 5,
            RapportBaseSuccessChance = 100,
            RapportMinimumSuccessChance = 100,
            RapportMaximumSuccessChance = 100,
            RapportDecreaseChance = 0
        };
        var system = new OperativeManagementSystem(store, catalog, lod, settings, new Random(10));

        Assert.True(system.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 3));
        system.Update(8);
        var projection = system.Capture(8);
        intelligence.Apply(projection);
        var report = Assert.Single(projection.Reports);
        Assert.Contains("increased", report.Summary);
        var evidence = Assert.Single(report.Evidence);
        Assert.Equal("rapport", evidence.Kind);
        Assert.Contains("increased", evidence.Detail);
        var operativeAffinity = intelligence.Agents.Single(agent => agent.EntityId == targetId)
            .RapportAffinities.Single(item => item.OperativeId == operativeId);
        Assert.Equal(operativeId, operativeAffinity.OperativeId);
        Assert.Equal(evidence.NumericValue, (float?)operativeAffinity.Affinity);
        var relationship = store.Query<EdgeData>().Entities.Select(entity => entity.GetComponent<EdgeData>())
            .Single(edge => edge.Source.Id == targetId && edge.Target.Id == operativeId);
        Assert.Equal(5f, relationship.RapportDelta);
        Assert.Equal(OperativeTaskKind.None,
            store.GetEntityById(operativeId).GetComponent<OperativeAssignment>().Kind);
    }

    [Theory]
    [InlineData(0, 100, -3f, "decreased")]
    [InlineData(0, 0, 0f, "unchanged")]
    public void BuildRapportCanDecreaseOrLeaveAffinityUnchanged(int successChance, int decreaseChance,
        float expectedDelta, string outcome)
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var system = new OperativeManagementSystem(store, catalog, lod, new IntelligenceTaskSettings
        {
            RapportDurationMinutes = 1,
            RapportBaseSuccessChance = successChance,
            RapportMinimumSuccessChance = successChance,
            RapportMaximumSuccessChance = successChance,
            RapportDecreaseChance = decreaseChance
        }, new Random(10));

        Assert.True(system.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 3));
        system.Update(4);

        var report = Assert.Single(system.Capture(4).Reports);
        Assert.Contains(outcome, report.Summary);
        var relationship = store.Query<EdgeData>().Entities.Select(entity => entity.GetComponent<EdgeData>())
            .Single(edge => edge.Source.Id == targetId && edge.Target.Id == operativeId);
        Assert.Equal(expectedDelta, relationship.RapportDelta);
    }

    [Fact]
    public void RapportContributionSurvivesPeriodicAffinityRefreshAndRespectsBounds()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var settings = new IntelligenceTaskSettings
        {
            RapportDurationMinutes = 1,
            RapportBaseSuccessChance = 100,
            RapportMinimumSuccessChance = 100,
            RapportMaximumSuccessChance = 100,
            RapportDecreaseChance = 0,
            RapportIncreaseDelta = 100
        };
        var management = new OperativeManagementSystem(store, catalog, lod, settings, new Random(1));
        Assert.True(management.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 0));
        management.Update(1);
        var edge = store.Query<EdgeData>().Entities.Single(entity =>
        {
            var data = entity.GetComponent<EdgeData>();
            return data.Source.Id == targetId && data.Target.Id == operativeId;
        });
        var beforeRefresh = edge.GetComponent<EdgeData>().Affinity;
        Assert.InRange(beforeRefresh, catalog.Affinity.Minimum, catalog.Affinity.Maximum);

        var interaction = new InteractionSystem(store, catalog, new Random(2), intervalTicks: 1);
        new SystemRoot(store) { interaction }.Update(default);

        var refreshed = edge.GetComponent<EdgeData>();
        Assert.InRange(refreshed.RapportDelta, -100f, 100f);
        Assert.Equal(AgentAffinityCalculator.Calculate(refreshed.Source, refreshed.Target,
            refreshed.IsFamily, refreshed.KnownTraitMask, catalog.AllTraitBits,
            catalog.Traits.Count, catalog.Affinity, refreshed.RapportDelta), refreshed.Affinity);
        Assert.InRange(refreshed.Affinity, catalog.Affinity.Minimum, catalog.Affinity.Maximum);
    }

    [Fact]
    public void RapportAssignmentCreatesMissingDirectionalRelationshipAndRefreshesIndexes()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var existing = store.Query<EdgeData>().Entities.ToArray().FirstOrDefault(entity =>
        {
            var edge = entity.GetComponent<EdgeData>();
            return edge.Source.Id == targetId && edge.Target.Id == operativeId;
        });
        if (!existing.IsNull) existing.DeleteEntity();
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var initialCount = store.Query<EdgeData>().Count;
        var system = new OperativeManagementSystem(store, catalog, lod,
            socialIndexes: indexes);

        Assert.True(system.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 0));

        Assert.Equal(initialCount + 1, store.Query<EdgeData>().Count);
        Assert.True(indexes.TryGetDirectedEdge(targetId, operativeId, out var indexedEdge));
        Assert.True(indexes.TryGetEdge(indexedEdge.EdgeEntityId, out var relationship));
        Assert.Equal(targetId, relationship.GetComponent<EdgeData>().Source.Id);
        Assert.Equal(operativeId, relationship.GetComponent<EdgeData>().Target.Id);
    }

    [Fact]
    public void RapportContestUsesOperativeStatesTargetStatsAndConfiguredTraits()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var operative = store.GetEntityById(operativeId);
        var target = store.GetEntityById(targetId);
        var definitions = catalog.AgentAttributes.Definitions;
        var charisma = catalog.AgentAttributes.GetIndex("charisma");
        var motivation = catalog.AgentAttributes.GetIndex("motivation");
        var stress = catalog.AgentAttributes.GetIndex("stress");
        var fatigue = catalog.AgentAttributes.GetIndex("fatigue");
        var willpower = catalog.AgentAttributes.GetIndex("willpower");
        operative.GetComponent<AgentAttributes>().Values[charisma] = definitions[charisma].Max;
        operative.GetComponent<AgentAttributes>().Values[motivation] = definitions[motivation].Max;
        operative.GetComponent<AgentAttributes>().Values[stress] = definitions[stress].Min;
        operative.GetComponent<AgentAttributes>().Values[fatigue] = definitions[fatigue].Min;
        target.GetComponent<AgentAttributes>().Values[willpower] = definitions[willpower].Min;
        target.GetComponent<Psychology>().TraitMask = 0;
        var relationshipEntity = store.Query<EdgeData>().Entities.FirstOrDefault(entity =>
        {
            var data = entity.GetComponent<EdgeData>();
            return data.Source.Id == targetId && data.Target.Id == operativeId;
        });
        if (!relationshipEntity.IsNull)
        {
            ref var relationship = ref relationshipEntity.GetComponent<EdgeData>();
            relationship.KnownTraitMask = 0;
            relationship.Affinity = AgentAffinityCalculator.Calculate(relationship.Source,
                relationship.Target, relationship.IsFamily, 0, 0, 0, catalog.Affinity);
        }
        var system = new OperativeManagementSystem(store, catalog, lod, new IntelligenceTaskSettings
        {
            RapportDurationMinutes = 1,
            RapportBaseSuccessChance = 50,
            RapportMinimumSuccessChance = 0,
            RapportMaximumSuccessChance = 100,
            RapportDecreaseChance = 0,
            RapportOperativeAttributeWeights = new(StringComparer.OrdinalIgnoreCase) { ["charisma"] = 1 },
            RapportTargetAttributeWeights = new(StringComparer.OrdinalIgnoreCase) { ["willpower"] = -1 }
        }, new Random(0));
        Assert.True(system.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 0));
        system.Update(1);
        Assert.Contains("increased", Assert.Single(system.Capture(1).Reports).Summary);

        var edge = store.Query<EdgeData>().Entities.Single(entity =>
        {
            var data = entity.GetComponent<EdgeData>();
            return data.Source.Id == targetId && data.Target.Id == operativeId;
        }).GetComponent<EdgeData>();
        Assert.Equal(5f, edge.RapportDelta);
    }

    [Fact]
    public void RapportSettingsRejectUnknownAttributesAndTraits()
    {
        var (store, catalog, lod, _, _) = CreateWorld();
        Assert.Throws<InvalidDataException>(() => new OperativeManagementSystem(store, catalog, lod,
            new IntelligenceTaskSettings
            {
                RapportOperativeAttributeWeights = new(StringComparer.OrdinalIgnoreCase) { ["unknown"] = 1 }
            }));
        Assert.Throws<InvalidDataException>(() => new OperativeManagementSystem(store, catalog, lod,
            new IntelligenceTaskSettings
            {
                RapportTargetTraitModifiers = new(StringComparer.OrdinalIgnoreCase) { ["unknown"] = 1 }
            }));
    }

    [Fact]
    public void ParanoidTargetTraitCanTurnAWinningRapportRollIntoAnAffinityLoss()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        ref var psychology = ref store.GetEntityById(targetId).GetComponent<Psychology>();
        psychology.TraitMask = catalog.Traits.Single(trait => trait.Id == "paranoid").Bit;
        var system = new OperativeManagementSystem(store, catalog, lod, new IntelligenceTaskSettings
        {
            RapportDurationMinutes = 1,
            RapportBaseSuccessChance = 50,
            RapportMinimumSuccessChance = 0,
            RapportMaximumSuccessChance = 80,
            RapportDecreaseChance = 20,
            RapportTargetTraitModifiers = new(StringComparer.OrdinalIgnoreCase) { ["paranoid"] = -100 }
        }, new FixedRandom(0));

        Assert.True(system.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 0));
        system.Update(1);

        var report = Assert.Single(system.Capture(1).Reports);
        Assert.Contains("decreased", report.Summary);
    }

    [Fact]
    public void RapportSettingsLoadConfiguredContent()
    {
        var settings = IntelligenceTaskSettings.Load(Path.Combine(AppContext.BaseDirectory, "data"));
        Assert.Equal(120, settings.RapportDurationMinutes);
        Assert.Equal(5f, settings.RapportIncreaseDelta);
        Assert.Equal(3f, settings.RapportDecreaseDelta);
        Assert.Equal(-15f, settings.RapportTargetTraitModifiers["paranoid"]);
    }

    [Fact]
    public void RapportSettingsRequireSpaceForNeutralOutcome()
    {
        var (store, catalog, lod, _, _) = CreateWorld();
        Assert.Throws<InvalidDataException>(() => new OperativeManagementSystem(store, catalog, lod,
            new IntelligenceTaskSettings
        {
            RapportMaximumSuccessChance = 95,
            RapportDecreaseChance = 20
        }));
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
        Assert.DoesNotContain(typeof(OperativeAffinitySnapshot).GetProperties(),
            property => property.PropertyType == typeof(Entity));
        Assert.DoesNotContain(typeof(OperativeAffinitySnapshot).GetFields(),
            field => field.FieldType == typeof(Entity));
    }

    [Fact]
    public void InvalidRapportAssignmentDoesNotPromoteOrCreateRelationship()
    {
        var (store, catalog, lod, operativeId, targetId) = CreateWorld();
        var target = store.GetEntityById(targetId);
        var originalTier = target.GetComponent<AgentLodState>().DesiredTier;
        target.GetComponent<AgentLocation>().CurrentLocationId = int.MaxValue;
        var initialEdgeCount = store.Query<EdgeData>().Count;
        var system = new OperativeManagementSystem(store, catalog, lod);

        Assert.False(system.Assign(new(operativeId, OperativeTaskKind.BuildRapport, targetId), 0));

        Assert.Equal(initialEdgeCount, store.Query<EdgeData>().Count);
        Assert.Equal(originalTier, target.GetComponent<AgentLodState>().DesiredTier);
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

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => Math.Clamp(value, 0, maxValue - 1);
    }
}
