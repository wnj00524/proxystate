using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState;
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
            typeof(AgentAttributes),
            typeof(Psychology),
            typeof(EdgeData),
            typeof(AgentState),
            typeof(WorldTime),
            typeof(AgentLocation),
            typeof(AgentTravel)
        };
        var tags = new[]
        {
            typeof(Tier1LodTag),
            typeof(Tier2LodTag),
            typeof(Tier3LodTag),
            typeof(OperativeTag)
        };

        Assert.All(components, type => Assert.True(typeof(IComponent).IsAssignableFrom(type), type.Name));
        Assert.All(tags, type => Assert.True(typeof(ITag).IsAssignableFrom(type), type.Name));
    }

    [Fact]
    public void IntelligenceRoleUsesNoneAsTheDefaultAndSupportsAllDefinedValues()
    {
        Assert.Equal(IntelligenceRole.None, new Identity().IntelligenceRole);
        Assert.Equal(new[]
        {
            IntelligenceRole.None,
            IntelligenceRole.Officer,
            IntelligenceRole.Agent,
            IntelligenceRole.Informant
        }, Enum.GetValues<IntelligenceRole>());
    }

    [Fact]
    public void SecretStatesLoadWithNoneAsTheDefaultAndSurveillanceAvailable()
    {
        var catalog = LoadCatalog();

        Assert.Equal(0, new AgentState().SecretStateHash);
        Assert.Equal("None", catalog.SecretStates.Single(secretState => secretState.Hash == 0).Name);
        Assert.Equal(0, catalog.SecretStates.Single(secretState =>
            string.Equals(secretState.Id, "none", StringComparison.OrdinalIgnoreCase)).Hash);
        Assert.Equal("Surveillance", catalog.SecretStates.Single(secretState =>
            string.Equals(secretState.Id, "surveillance", StringComparison.OrdinalIgnoreCase)).Name);
    }

    [Fact]
    public void DebugModeOnlyEnablesForTheDebugCommandLineArgument()
    {
        Assert.False(DebugMode.IsEnabled(Array.Empty<string>()));
        Assert.False(DebugMode.IsEnabled(new[] { "--debug" }));
        Assert.True(DebugMode.IsEnabled(new[] { "-debug" }));
        Assert.True(DebugMode.IsEnabled(new[] { "-DEBUG" }));
    }

    [Fact]
    public void ApplicationsLauncherOnlyExposesDebugWindowInDebugMode()
    {
        var normalApplications = ApplicationCatalog.GetAvailable(debugMode: false);
        var debugApplications = ApplicationCatalog.GetAvailable(debugMode: true);

        Assert.Equal(new[] { ApplicationId.Dossiers }, normalApplications.Select(application => application.Id));
        Assert.Equal(
            new[] { ApplicationId.Dossiers, ApplicationId.DebugWindow },
            debugApplications.Select(application => application.Id));
        Assert.Equal("Surveillance Terminal", ApplicationShell.DossiersWindowTitle);
        Assert.Equal("Debug Window", debugApplications[1].Label);
        Assert.Equal("Debug Window", ApplicationShell.DebugWindowTitle);
    }

    [Fact]
    public void SpawnerSelectsExactlyFiveDistinctOperatives()
    {
        var catalog = LoadCatalog();
        var firstStore = new EntityStore();
        var secondStore = new EntityStore();

        new AgentSpawner(catalog).Spawn(firstStore, 20, new Random(321));
        new AgentSpawner(catalog).Spawn(secondStore, 20, new Random(321));

        var firstOperatives = firstStore.Query<Identity>().Entities
            .Where(entity => entity.Tags.Has<OperativeTag>())
            .Select(entity => entity.Id)
            .ToArray();
        var secondOperatives = secondStore.Query<Identity>().Entities
            .Where(entity => entity.Tags.Has<OperativeTag>())
            .Select(entity => entity.Id)
            .ToArray();

        Assert.Equal(SimulationDefaults.OperativeCount, firstOperatives.Length);
        Assert.Equal(firstOperatives.Length, firstOperatives.Distinct().Count());
        Assert.Equal(firstOperatives, secondOperatives);
        Assert.All(firstStore.Query<Identity>().Entities, entity =>
        {
            var expectedRole = entity.Tags.Has<OperativeTag>()
                ? IntelligenceRole.Officer
                : IntelligenceRole.None;
            Assert.Equal(expectedRole, entity.GetComponent<Identity>().IntelligenceRole);
        });
    }

    [Fact]
    public void SpawnerSelectsAtMostThePopulationForSmallOperativeTeams()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();

        new AgentSpawner(catalog).Spawn(store, 3, new Random(99));

        Assert.Equal(3, store.Query<Identity>().Entities.Count(entity => entity.Tags.Has<OperativeTag>()));
        Assert.All(store.Query<Identity>().Entities,
            entity => Assert.Equal(IntelligenceRole.Officer, entity.GetComponent<Identity>().IntelligenceRole));
    }

    [Fact]
    public void SpawnerDefaultsEveryAgentToNoSecretState()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();

        new AgentSpawner(catalog).Spawn(store, 10, new Random(99));

        Assert.All(store.Query<AgentState>().Entities,
            entity => Assert.Equal(0, entity.GetComponent<AgentState>().SecretStateHash));
    }

    [Fact]
    public void PlayerIntelligenceUsesTheUnionOfOperativeOutgoingMasks()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var operativeOne = store.CreateEntity(new Identity { NameId = 11, IntelligenceRole = IntelligenceRole.Officer }, Tags.Get<OperativeTag>());
        var operativeTwo = store.CreateEntity(new Identity { NameId = 12, IntelligenceRole = IntelligenceRole.Officer }, Tags.Get<OperativeTag>());
        var nonOperative = store.CreateEntity(new Identity { NameId = 13, IntelligenceRole = IntelligenceRole.None });
        var target = store.CreateEntity(new Identity { NameId = 14, IntelligenceRole = IntelligenceRole.Informant });

        store.CreateEntity(new EdgeData
        {
            Source = operativeOne,
            Target = target,
            KnownTraitMask = 1L
        });
        store.CreateEntity(new EdgeData
        {
            Source = operativeTwo,
            Target = target,
            KnownTraitMask = 4L
        });
        store.CreateEntity(new EdgeData
        {
            Source = target,
            Target = operativeOne,
            KnownTraitMask = 2L
        });
        store.CreateEntity(new EdgeData
        {
            Source = nonOperative,
            Target = target,
            KnownTraitMask = 8L
        });

        var intelligence = PlayerIntelligenceDB.Create(store, catalog);

        Assert.Equal(new[] { operativeOne.Id, operativeTwo.Id }, intelligence.OperativeEntityIds);
        Assert.Equal(1L | 4L, intelligence.Agents.Single(agent => agent.EntityId == target.Id).KnownTraitMask);
        Assert.Equal(0L, intelligence.Agents.Single(agent => agent.EntityId == nonOperative.Id).KnownTraitMask);
        Assert.Equal(IntelligenceRole.Officer, intelligence.Agents.Single(agent => agent.EntityId == operativeOne.Id).IntelligenceRole);
        Assert.Equal(IntelligenceRole.Informant, intelligence.Agents.Single(agent => agent.EntityId == target.Id).IntelligenceRole);
        Assert.Equal(4, intelligence.Agents.Count);
    }

    [Fact]
    public void DebugSnapshotCopiesAndResolvesSecretStateWithoutAddingItToPlayerIntelligence()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var entity = store.CreateEntity(
            new Identity { NameId = 1 },
            new PoliticalAlignment { FactionId = catalog.Factions[0].FactionId },
            new AgentAttributes { Values = catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray() },
            new Psychology(),
            new AgentState { SecretStateHash = 1 },
            new ActivityState
            {
                ActionHash = catalog.Actions[0].Hash,
                ActivityTypeHash = catalog.Actions[0].Activity.Hash,
                Phase = ActivityPhase.Performing
            },
            new AgentLocation(),
            new AgentTravel { RouteLocationIds = Array.Empty<int>() });

        var snapshot = DebugSnapshotBuilder.Capture(store, catalog).Single();
        var intelligence = PlayerIntelligenceDB.Create(store, catalog);

        Assert.Equal(1, snapshot.SecretStateHash);
        Assert.Equal("Surveillance", snapshot.SecretStateName);
        Assert.Equal(catalog.Actions[0].Activity.Hash, snapshot.ActivityTypeHash);
        Assert.Equal(catalog.Actions[0].Activity.Name, snapshot.ActivityTypeName);
        Assert.Equal(ActivityPhase.Performing, snapshot.ActivityPhase);
        Assert.DoesNotContain("SecretState", typeof(PlayerIntelligenceAgentSnapshot)
            .GetProperties()
            .Select(property => property.Name));
        Assert.DoesNotContain("SecretStateHash", typeof(PlayerIntelligenceAgentSnapshot)
            .GetProperties()
            .Select(property => property.Name));
        Assert.Single(intelligence.Agents);
    }

    [Fact]
    public void DebugInspectionCopiesResolvedAgentNetworkAndSummaryValues()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 12, new Random(47));

        var inspection = DebugSnapshotBuilder.CaptureInspection(store, catalog);

        Assert.Equal(12, inspection.Agents.Count);
        Assert.All(inspection.Agents, agent => Assert.Equal(3, agent.Networks.Count));
        Assert.NotEmpty(inspection.Networks);
        Assert.Equal(36, inspection.Networks.Sum(network => network.MemberCount));
        Assert.All(inspection.Networks, network =>
        {
            Assert.NotEmpty(network.DisplayName);
            Assert.NotEmpty(network.TypeName);
            if (network.TypeName == "Friend Group") Assert.Null(network.Anchor);
            else Assert.NotNull(network.Anchor);
            Assert.True(network.MemberCount > 0);
        });
        Assert.All(inspection.Agents.SelectMany(agent => agent.Networks), membership =>
        {
            Assert.NotEmpty(membership.NetworkDisplayName);
            Assert.NotEmpty(membership.NetworkTypeName);
            Assert.NotEmpty(membership.RoleName);
            Assert.Equal(membership.SupervisorEntityId is null, membership.SupervisorDisplayName is null);
        });
        Assert.DoesNotContain(typeof(Entity), typeof(DebugNetworkSnapshot).GetProperties().Select(property => property.PropertyType));
        Assert.DoesNotContain(typeof(Entity), typeof(DebugNetworkMembershipSnapshot).GetProperties().Select(property => property.PropertyType));
    }

    [Fact]
    public void DossierTraitFormatterRevealsOnlyKnownBits()
    {
        var brave = new TraitDefinition("brave", "Brave", 1L, 0.5f);
        var greedy = new TraitDefinition("greedy", "Greedy", 4L, 0.5f);

        Assert.Equal("Trait: ???", DossierTraitFormatter.Format(brave, 0L));
        Assert.Equal("Brave", DossierTraitFormatter.Format(brave, 1L));
        Assert.Equal("Trait: ???", DossierTraitFormatter.Format(greedy, 1L));
    }

    [Fact]
    public void SpawnerCreatesFiveUniqueBidirectionalRelationshipsPerAgent()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 10, new Random(123));

        var agents = store.Query<Identity>().Entities.ToArray();
        var edges = store.Query<EdgeData>().Entities
            .Select(entity => entity.GetComponent<EdgeData>())
            .ToArray();

        Assert.True(edges.Length >= 10 * SimulationDefaults.SocialRelationshipsPerAgent);
        foreach (var agent in agents)
        {
            var outgoing = edges.Where(edge => edge.Source == agent).ToArray();
            Assert.True(outgoing.Length >= 5);
            Assert.Equal(outgoing.Length, outgoing.Select(edge => edge.Target).Distinct().Count());
            Assert.DoesNotContain(outgoing, edge => edge.Target == agent);

            foreach (var edge in outgoing)
            {
                Assert.Contains(edges, reciprocal =>
                    reciprocal.Source == edge.Target && reciprocal.Target == edge.Source);
            }
        }

        Assert.All(edges, edge =>
        {
            Assert.Equal(0f, edge.Affinity);
            Assert.Equal(0L, edge.KnownTraitMask);
            Assert.Equal(0, edge.KnownStatsMask);
            Assert.Equal(0, edge.KnownPoliticalMask);
        });
    }

    [Fact]
    public void SocialGraphHandlesPopulationsSmallerThanSix()
    {
        var catalog = LoadCatalog();
        var oneAgentStore = new EntityStore();
        var twoAgentStore = new EntityStore();

        new AgentSpawner(catalog).Spawn(oneAgentStore, 1, new Random(1));
        new AgentSpawner(catalog).Spawn(twoAgentStore, 2, new Random(1));

        Assert.Empty(oneAgentStore.Query<EdgeData>().Entities);
        Assert.Equal(2, twoAgentStore.Query<EdgeData>().Count);
    }

    [Fact]
    public void SeededSocialGraphGenerationIsDeterministic()
    {
        var catalog = LoadCatalog();
        var firstStore = new EntityStore();
        var secondStore = new EntityStore();
        new AgentSpawner(catalog).Spawn(firstStore, 10, new Random(42));
        new AgentSpawner(catalog).Spawn(secondStore, 10, new Random(42));

        var firstShape = RelationshipShape(firstStore);
        var secondShape = RelationshipShape(secondStore);

        Assert.Equal(firstShape, secondShape);
    }

    [Fact]
    public void InteractionSystemWaitsForItsConfiguredInterval()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var source = CreateAgent(store, catalog, perception: 100f, willpower: 1f, traitMask: 0L);
        var target = CreateAgent(store, catalog, perception: 1f, willpower: 1f, traitMask: 1L);
        var edgeEntity = store.CreateEntity(new EdgeData { Source = source, Target = target });
        var system = new InteractionSystem(store, catalog, new SequenceRandom(100, 1, 0), intervalTicks: 2);
        var root = new SystemRoot(store) { system };

        root.Update(default);
        Assert.Equal(0L, edgeEntity.GetComponent<EdgeData>().KnownTraitMask);

        root.Update(default);
        Assert.Equal(1L, edgeEntity.GetComponent<EdgeData>().KnownTraitMask);
    }

    [Fact]
    public void SuccessfulDiscoveryIsDirectionalAndUpdatesNormalizedAffinity()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var source = CreateAgent(store, catalog, perception: 100f, willpower: 1f, traitMask: 0L);
        var target = CreateAgent(store, catalog, perception: 1f, willpower: 1f, traitMask: 1L | 4L);
        var forward = store.CreateEntity(new EdgeData { Source = source, Target = target });
        var reverse = store.CreateEntity(new EdgeData { Source = target, Target = source });
        var root = new SystemRoot(store)
        {
            new InteractionSystem(store, catalog, new SequenceRandom(100, 1, 0), intervalTicks: 1)
        };

        root.Update(default);

        Assert.Equal(1L, forward.GetComponent<EdgeData>().KnownTraitMask);
        Assert.Equal(25f, forward.GetComponent<EdgeData>().Affinity);
        Assert.Equal(0L, reverse.GetComponent<EdgeData>().KnownTraitMask);
        Assert.Equal(0f, reverse.GetComponent<EdgeData>().Affinity);
    }

    [Fact]
    public void InteractionSystemDoesNotRevealAlreadyKnownOrUnknownTraits()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var source = CreateAgent(store, catalog, perception: 100f, willpower: 1f, traitMask: 0L);
        var target = CreateAgent(store, catalog, perception: 1f, willpower: 1f, traitMask: 1L);
        var edgeEntity = store.CreateEntity(new EdgeData
        {
            Source = source,
            Target = target,
            KnownTraitMask = 1L
        });
        var root = new SystemRoot(store)
        {
            new InteractionSystem(store, catalog, new SequenceRandom(100, 1), intervalTicks: 1)
        };

        root.Update(default);

        var edge = edgeEntity.GetComponent<EdgeData>();
        Assert.Equal(1L, edge.KnownTraitMask);
        Assert.Equal(25f, edge.Affinity);
        Assert.Equal(0L, edge.KnownTraitMask & ~catalog.AllTraitBits);
    }

    [Fact]
    public void ParanoidTargetsCanDefeatAnOtherwiseSuccessfulDiscoveryRoll()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var source = CreateAgent(store, catalog, perception: 1f, willpower: 1f, traitMask: 0L);
        var target = CreateAgent(store, catalog, perception: 1f, willpower: 1f, traitMask: 1L | 8L);
        var edgeEntity = store.CreateEntity(new EdgeData { Source = source, Target = target });
        var root = new SystemRoot(store)
        {
            new InteractionSystem(store, catalog, new SequenceRandom(2, 100), intervalTicks: 1)
        };

        root.Update(default);

        Assert.Equal(0L, edgeEntity.GetComponent<EdgeData>().KnownTraitMask);
    }

    [Fact]
    public void WorldTimeSnapshotFormatsTheInGameCalendarAndClock()
    {
        var time = new WorldTime
        {
            ElapsedSimulationSeconds = 2 * SimulationDefaults.SimulationSecondsPerDay +
                (13 * 60 + 7) * SimulationDefaults.SimulationSecondsPerMinute
        };

        var snapshot = WorldTimeSnapshot.From(time);

        Assert.Equal("Day 3 | Wednesday | 13:07", WorldTimeFormatter.Format(snapshot));
    }

    [Fact]
    public void DebugSnapshotContainsCopiedDetailsForEveryAgent()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 3, new Random(1234));

        var snapshots = DebugSnapshotBuilder.Capture(store, catalog);

        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(catalog.AgentAttributes.Count, snapshot.Attributes.Count);
            Assert.Equal(catalog.Traits.Count, snapshot.Traits.Count);
            var entity = store.Query<Identity>().Entities.Single(candidate => candidate.Id == snapshot.EntityId);
            Assert.Equal(
                entity.Tags.Has<OperativeTag>() ? IntelligenceRole.Officer : IntelligenceRole.None,
                snapshot.IntelligenceRole);
            Assert.Contains(snapshot.OccupationName, catalog.Jobs.Select(job => job.Name));
            Assert.Contains(snapshot.FactionName, catalog.Factions.Select(faction => faction.Name));
            Assert.Contains(snapshot.CurrentActionName, catalog.Actions.Select(action => action.Name));
            Assert.NotEmpty(snapshot.Travel.Route);
            Assert.NotEmpty(snapshot.CurrentLocation.Name);
        });

        var entity = store.Query<Identity>().Entities.First();
        var before = snapshots.Single(snapshot => snapshot.EntityId == entity.Id);
        var originalValue = before.Attributes[0].Value;
        var values = entity.GetComponent<AgentAttributes>().Values;
        values[0] = originalValue + 10f;

        var after = snapshots.Single(snapshot => snapshot.EntityId == entity.Id);
        Assert.Equal(originalValue, after.Attributes[0].Value);
    }

    [Fact]
    public void CatalogLoadsJobsAndNetworkedWorldDefinitions()
    {
        var catalog = LoadCatalog();

        Assert.NotEmpty(catalog.Jobs);
        Assert.Equal(5, catalog.World.Locations.Count);
        Assert.Equal("office-worker", catalog.Jobs.Single(job => job.Hash == 2001).Id);

        var route = catalog.World.FindShortestRoute(3001, 3004);
        Assert.NotNull(route);
        Assert.Equal(new[] { 3001, 3003, 3004 }, route.LocationIds);
        Assert.Equal(25, route.TravelMinutes);
    }

    [Fact]
    public void SpawnerCreatesTheRequestedPopulationWithGeneralizedAttributesAndLodState()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawned = new AgentSpawner(catalog).Spawn(store, SimulationDefaults.AgentCount, new Random(1234));

        Assert.Equal(SimulationDefaults.AgentCount, spawned);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<Identity>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<PoliticalAlignment>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<AgentAttributes>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<Psychology>().Count);
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<AgentState>().Count);
        Assert.Equal(SimulationDefaults.OperativeCount,
            store.Query<AgentAttributes>().AllTags(Tags.Get<Tier1LodTag>()).Count);
        Assert.InRange(store.Query<AgentAttributes>().AllTags(Tags.Get<DetailedSimulationTag>()).Count,
            SimulationDefaults.OperativeCount, SimulationDefaults.AgentCount - 1);
    }

    [Fact]
    public void SpawnerAssignsJobsHomesWorkplacesAndDeterministicRoutes()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 100, new Random(123));

        foreach (var entity in store.Query<AgentLocation>().Entities)
        {
            var identity = entity.GetComponent<Identity>();
            var job = Assert.Single(catalog.Jobs, candidate => candidate.Hash == identity.OccupationId);
            var location = entity.GetComponent<AgentLocation>();
            var commute = entity.GetComponent<AgentCommute>();

            Assert.Equal(location.HomeLocationId, location.CurrentLocationId);
            Assert.Equal(SimulationDefaults.ResidentialLocationType,
                catalog.World.GetLocation(location.HomeLocationId).Type);
            Assert.Equal(job.WorkplaceType,
                catalog.World.GetLocation(location.WorkLocationId).Type,
                StringComparer.OrdinalIgnoreCase);
            Assert.True(commute.TravelMinutes > 0);
            if (entity.HasComponent<AgentTravel>())
            {
                var travel = entity.GetComponent<AgentTravel>();
                Assert.Equal(location.HomeLocationId, travel.RouteLocationIds[0]);
                Assert.Equal(location.WorkLocationId, travel.RouteLocationIds[^1]);
                Assert.Equal(AgentTravelMode.Stationary, travel.Mode);
            }
        }
    }

    [Fact]
    public void WorldClockAdvancesOneInWorldDayPerTenRealMinutes()
    {
        var store = new EntityStore();
        var clock = new WorldClockSystem(store);
        var root = new SystemRoot(store) { clock };

        clock.Advance(SimulationDefaults.RealSecondsPerSimulationDay);
        root.Update(default);

        var time = clock.ClockEntity.GetComponent<WorldTime>();
        Assert.Equal(SimulationDefaults.SimulationSecondsPerDay, time.ElapsedSimulationSeconds);
        Assert.Equal(1, time.DayIndex);
        Assert.Equal(2, time.DayOfWeek);
        Assert.Equal(0, time.MinuteOfDay);
    }

    [Fact]
    public void IntentExecutionSystemMovesAnyLocationTargetAndBack()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = new WorldClockSystem(store);
        var entity = store.CreateEntity(
            new Identity { NameId = 1, OccupationId = 2001 },
            new AgentLocation { HomeLocationId = 3001, WorkLocationId = 3004, CurrentLocationId = 3001 },
            new AgentTravel
            {
                RouteLocationIds = new[] { 3001, 3003, 3004 },
                TotalTravelMinutes = 25,
                RoutePosition = 0,
                RemainingTravelMinutes = 0,
                Mode = AgentTravelMode.Stationary
            },
            new AgentState(),
            new IntentionState { ActionHash = 1001, TargetLocationId = 3004 },
            new ActivityState { ActionHash = 1002 },
            new DecisionState {   },
            Tags.Get<Tier1LodTag>());
        var commuting = new IntentExecutionSystem(store, catalog, clock.ClockEntity);
        var root = new SystemRoot(store) { clock, commuting };

        AdvanceMinutes(clock, root, 455);
        Assert.Equal(AgentTravelMode.Travelling, entity.GetComponent<AgentTravel>().Mode);

        AdvanceMinutes(clock, root, 25);
        Assert.Equal(AgentTravelMode.Stationary, entity.GetComponent<AgentTravel>().Mode);
        Assert.Equal(3004, entity.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(1001, entity.GetComponent<ActivityState>().ActionHash);

        entity.GetComponent<IntentionState>().ActionHash = 1002;
        entity.GetComponent<IntentionState>().TargetLocationId = 3001;
        AdvanceMinutes(clock, root, 480);
        Assert.Equal(AgentTravelMode.Travelling, entity.GetComponent<AgentTravel>().Mode);

        AdvanceMinutes(clock, root, 25);
        Assert.Equal(AgentTravelMode.Stationary, entity.GetComponent<AgentTravel>().Mode);
        Assert.Equal(3001, entity.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(1002, entity.GetComponent<ActivityState>().ActionHash);
    }

    [Fact]
    public void IntentExecutionSystemPerformsWhenAlreadyAtTheTarget()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = new WorldClockSystem(store);
        var entity = store.CreateEntity(
            new Identity { NameId = 1, OccupationId = 2001 },
            new AgentLocation { HomeLocationId = 3001, WorkLocationId = 3004, CurrentLocationId = 3001 },
            new AgentTravel
            {
                RouteLocationIds = new[] { 3001, 3003, 3004 },
                TotalTravelMinutes = 25,
                Mode = AgentTravelMode.Stationary
            },
            new AgentState(),
        new IntentionState { ActionHash = 1002, TargetLocationId = 3001 },
            new ActivityState { ActionHash = 1002 },
            new DecisionState {   },
            Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { clock, new IntentExecutionSystem(store, catalog, clock.ClockEntity) };

        AdvanceMinutes(clock, root, 6 * SimulationDefaults.SimulationMinutesPerDay + 600);

        Assert.Equal(AgentTravelMode.Stationary, entity.GetComponent<AgentTravel>().Mode);
        Assert.Equal(3001, entity.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(ActivityPhase.Performing, entity.GetComponent<ActivityState>().Phase);
    }

    [Fact]
    public void IntentExecutionSystemTravelsToAnEntityTargetAndInvalidatesTargetLoss()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = new WorldClockSystem(store);
        var target = store.CreateEntity(
            new Identity { NameId = 2 },
            new AgentLocation { CurrentLocationId = 3004 });
        var actor = store.CreateEntity(
            new AgentLocation { CurrentLocationId = 3001 },
            new AgentTravel { RouteLocationIds = Array.Empty<int>() },
            new IntentionState { ActionHash = 1003, TargetEntityId = target.Id },
            new ActivityState(),
            new DecisionState {   },
            Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { clock, new IntentExecutionSystem(store, catalog, clock.ClockEntity) };

        AdvanceMinutes(clock, root, 1);
        Assert.Equal(AgentTravelMode.Travelling, actor.GetComponent<AgentTravel>().Mode);
        AdvanceMinutes(clock, root, 25);
        Assert.Equal(3004, actor.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(ActivityPhase.Performing, actor.GetComponent<ActivityState>().Phase);

        target.DeleteEntity();
        actor.GetComponent<DecisionState>().Dirty = false;
        AdvanceMinutes(clock, root, 1);
        Assert.True(actor.GetComponent<DecisionState>().Dirty);
        Assert.Equal(ActivityPhase.Blocked, actor.GetComponent<ActivityState>().Phase);
    }

    [Fact]
    public void InteractionWorkOnlyVisitsEdgesOfDetailedSources()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var source = CreateAgent(store, catalog, perception: 100f, willpower: 1f, traitMask: 0L);
        var target = CreateAgent(store, catalog, perception: 1f, willpower: 1f, traitMask: 1L);
        store.CreateEntity(new EdgeData { Source = source, Target = target });
        for (var index = 0; index < 100; index++)
        {
            var unrelatedSource = store.CreateEntity(new Identity { NameId = 1000 + index });
            var unrelatedTarget = store.CreateEntity(new Identity { NameId = 2000 + index });
            store.CreateEntity(new EdgeData { Source = unrelatedSource, Target = unrelatedTarget });
        }
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var diagnostics = new SimulationWorkDiagnostics();
        var root = new SystemRoot(store)
        {
            new InteractionSystem(store, catalog, new SequenceRandom(100, 1, 0), intervalTicks: 1,
                socialIndexes: indexes, workDiagnostics: diagnostics)
        };

        root.Update(default);

        Assert.Equal(1, diagnostics.Snapshot().EdgeVisits);
    }

    [Fact]
    public void CatalogRejectsExecutorWhoseTargetTypeIsIncompatible()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        var path = Path.Combine(directory.RootPath, "actions.json");
        var json = File.ReadAllText(path).Replace(
            "\"executor\": \"performAtLocation\"",
            "\"executor\": \"performWithEntity\"", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var exception = Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
        Assert.Contains("actions.json:actions[0].execution.executor", exception.Message);
        Assert.Contains("incompatible with target kind", exception.Message);
    }

    [Fact]
    public void CatalogRejectsDuplicateDataDefinedActivityIdentity()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        var path = Path.Combine(directory.RootPath, "actions.json");
        var json = File.ReadAllText(path).Replace(
            "\"hash\": 4002",
            "\"hash\": 4001", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var exception = Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
        Assert.Contains("unique, non-zero activity ID and hash", exception.Message);
    }

    [Fact]
    public void IntentExecutionSystemPreservesSecretStateWhileUpdatingPublicAction()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = new WorldClockSystem(store);
        var entity = store.CreateEntity(
            new Identity { NameId = 1, OccupationId = 2001 },
            new AgentLocation { HomeLocationId = 3001, WorkLocationId = 3004, CurrentLocationId = 3001 },
            new AgentTravel
            {
                RouteLocationIds = new[] { 3001, 3003, 3004 },
                TotalTravelMinutes = 25,
                Mode = AgentTravelMode.Stationary
            },
            new AgentState { SecretStateHash = 1 },
            new IntentionState { ActionHash = 1001, TargetLocationId = 3004 },
            new ActivityState { ActionHash = 1002 },
            new DecisionState {   },
            Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { clock, new IntentExecutionSystem(store, catalog, clock.ClockEntity) };

        AdvanceMinutes(clock, root, 455);
        AdvanceMinutes(clock, root, 25);

        Assert.Equal(1001, entity.GetComponent<ActivityState>().ActionHash);
        Assert.Equal(1, entity.GetComponent<AgentState>().SecretStateHash);
    }

    [Fact]
    public void SpawnerRejectsAssignmentsWhenNoHomeToWorkRouteExists()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(Path.Combine(directory.RootPath, "world.json"),
            "{" +
            "\"locations\":[" +
            "{\"id\":\"home\",\"name\":\"Home\",\"hash\":1,\"type\":\"residential\"}," +
            "{\"id\":\"office\",\"name\":\"Office\",\"hash\":2,\"type\":\"office\"}," +
            "{\"id\":\"transit\",\"name\":\"Transit\",\"hash\":3,\"type\":\"transit\"}," +
            "{\"id\":\"retail\",\"name\":\"Retail\",\"hash\":4,\"type\":\"retail\"}]," +
            "\"connections\":[{\"from\":\"office\",\"to\":\"transit\",\"travelMinutes\":5}]}" );

        var catalog = ContentCatalog.Load(directory.RootPath);
        var store = new EntityStore();
        Assert.Throws<InvalidDataException>(() =>
            new AgentSpawner(catalog).Spawn(store, 1, new Random(1)));
        Assert.Equal(0, store.Query<Identity>().Count);
    }

    [Fact]
    public void SpawnerGeneratesEverySchemaAttributeWithinItsConfiguredRange()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, SimulationDefaults.AgentCount, new Random(5678));

        foreach (var entity in store.Query<AgentAttributes>().Entities)
        {
            var values = entity.GetComponent<AgentAttributes>().Values;
            Assert.Equal(catalog.AgentAttributes.Count, values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                var definition = catalog.AgentAttributes.Definitions[index];
                Assert.InRange(values[index], definition.Min, definition.Max);
            }

            var psychology = entity.GetComponent<Psychology>();
            Assert.Equal(0L, psychology.TraitMask & ~catalog.AllTraitBits);
            if (entity.HasComponent<ActivityState>())
                Assert.Contains(entity.GetComponent<ActivityState>().ActionHash,
                    catalog.Actions.Select(action => action.Hash));
        }
    }

    [Fact]
    public void SchemaSupportsAdditionalAttributesWithoutSpawnerChanges()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        var schemaPath = System.IO.Path.Combine(directory.RootPath, "agent-schema.json");
        var schemaJson = File.ReadAllText(schemaPath).Replace(
            "\n  ]", ",\n    { \"id\": \"luck\", \"min\": -5, \"max\": 5, \"average\": 1 }\n  ]");
        File.WriteAllText(schemaPath, schemaJson);

        var catalog = ContentCatalog.Load(directory.RootPath);
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 10, new Random(7));

        Assert.Equal(10, catalog.AgentAttributes.Count);
        Assert.Equal(9, catalog.AgentAttributes.GetIndex("luck"));
        Assert.All(store.Query<AgentAttributes>().Entities,
            entity => Assert.Equal(10, entity.GetComponent<AgentAttributes>().Values.Length));
    }

    [Fact]
    public void PopulationMeansApproximateConfiguredAverages()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        const int count = 10_000;
        new AgentSpawner(catalog).Spawn(store, count, new Random(991));
        var totals = new double[catalog.AgentAttributes.Count];

        foreach (var entity in store.Query<AgentAttributes>().Entities)
        {
            var values = entity.GetComponent<AgentAttributes>().Values;
            for (var index = 0; index < values.Length; index++)
            {
                totals[index] += values[index];
            }
        }

        for (var index = 0; index < totals.Length; index++)
        {
            var definition = catalog.AgentAttributes.Definitions[index];
            var tolerance = Math.Max((definition.Max - definition.Min) * 0.03f, 0.03f);
            Assert.InRange((float)(totals[index] / count), definition.Average - tolerance, definition.Average + tolerance);
        }
    }

    [Fact]
    public void EqualBoundsAlwaysGenerateTheConfiguredValue()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        var schemaPath = System.IO.Path.Combine(directory.RootPath, "agent-schema.json");
        var schemaJson = File.ReadAllText(schemaPath).Replace(
            "\"attributes\": [", "\"attributes\": [\n    { \"id\": \"fixed\", \"min\": 7, \"max\": 7, \"average\": 7 },");
        File.WriteAllText(schemaPath, schemaJson);

        var catalog = ContentCatalog.Load(directory.RootPath);
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 100, new Random(3));

        Assert.All(store.Query<AgentAttributes>().Entities,
            entity => Assert.Equal(7f, entity.GetComponent<AgentAttributes>().Values[0]));
    }

    [Fact]
    public void TraitPrevalenceIsReflectedInGeneratedMasks()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        const int count = 10_000;
        new AgentSpawner(catalog).Spawn(store, count, new Random(456));

        foreach (var trait in catalog.Traits)
        {
            var present = store.Query<Psychology>().Entities.Count(entity =>
                (entity.GetComponent<Psychology>().TraitMask & trait.Bit) != 0);
            var observed = (double)present / count;
            Assert.InRange(observed, trait.Prevalence - 0.03, trait.Prevalence + 0.03);
        }
    }

    [Fact]
    public void TraitPrevalenceZeroAndOneAreDeterministic()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(System.IO.Path.Combine(directory.RootPath, "traits.json"),
            "[{\"id\":\"never\",\"name\":\"Never\",\"bit\":1,\"prevalence\":0},{\"id\":\"always\",\"name\":\"Always\",\"bit\":2,\"prevalence\":1},{\"id\":\"greedy\",\"name\":\"Greedy\",\"bit\":4,\"prevalence\":0},{\"id\":\"paranoid\",\"name\":\"Paranoid\",\"bit\":8,\"prevalence\":0}]" );

        var catalog = ContentCatalog.Load(directory.RootPath);
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 100, new Random(19));

        Assert.All(store.Query<Psychology>().Entities, entity =>
        {
            var mask = entity.GetComponent<Psychology>().TraitMask;
            Assert.Equal(0, mask & 1);
            Assert.Equal(2, mask & 2);
        });
    }

    [Fact]
    public void SeededRandomnessProducesTheSameFixture()
    {
        var catalog = LoadCatalog();
        var firstStore = new EntityStore();
        var secondStore = new EntityStore();
        new AgentSpawner(catalog).Spawn(firstStore, 1, new Random(42));
        new AgentSpawner(catalog).Spawn(secondStore, 1, new Random(42));

        var first = firstStore.Query<Identity>().Entities.First();
        var second = secondStore.Query<Identity>().Entities.First();
        Assert.Equal(first.GetComponent<Identity>(), second.GetComponent<Identity>());
        Assert.Equal(first.GetComponent<PoliticalAlignment>(), second.GetComponent<PoliticalAlignment>());
        Assert.Equal(first.GetComponent<AgentAttributes>().Values, second.GetComponent<AgentAttributes>().Values);
        Assert.Equal(first.GetComponent<Psychology>(), second.GetComponent<Psychology>());
        Assert.Equal(first.GetComponent<AgentState>(), second.GetComponent<AgentState>());
    }

    [Fact]
    public void CatalogRejectsInvalidNumericSchema()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(System.IO.Path.Combine(directory.RootPath, "agent-schema.json"),
            "{\"attributes\":[{\"id\":\"fatigue\",\"min\":10,\"max\":1,\"average\":5},{\"id\":\"stress\",\"min\":0,\"max\":100,\"average\":20}]}" );

        Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
    }

    [Fact]
    public void CatalogRejectsSecretStatesWithoutDefaultNone()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(Path.Combine(directory.RootPath, "secret-states.json"),
            "[{\"id\":\"surveillance\",\"name\":\"Surveillance\",\"hash\":1}]" );

        Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
    }

    [Fact]
    public void CatalogRejectsDuplicateSecretStateHashes()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(Path.Combine(directory.RootPath, "secret-states.json"),
            "[{\"id\":\"none\",\"name\":\"None\",\"hash\":0},{\"id\":\"surveillance\",\"name\":\"Surveillance\",\"hash\":0}]" );

        Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
    }

    [Fact]
    public void CatalogRejectsInvalidJobSchedule()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(Path.Combine(directory.RootPath, "jobs.json"),
            "[{\"id\":\"invalid\",\"name\":\"Invalid\",\"hash\":1,\"workStartMinute\":900,\"workEndMinute\":600,\"workDays\":[1],\"workplaceType\":\"office\"}]" );

        Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
    }

    [Fact]
    public void CatalogRejectsWorldConnectionsWithUnknownEndpoints()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(Path.Combine(directory.RootPath, "world.json"),
            "{" +
            "\"locations\":[" +
            "{\"id\":\"home\",\"name\":\"Home\",\"hash\":1,\"type\":\"residential\"}," +
            "{\"id\":\"office\",\"name\":\"Office\",\"hash\":2,\"type\":\"office\"}," +
            "{\"id\":\"retail\",\"name\":\"Retail\",\"hash\":3,\"type\":\"retail\"}]," +
            "\"connections\":[{\"from\":\"home\",\"to\":\"missing\",\"travelMinutes\":5}]}" );

        Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
    }

    [Fact]
    public void CatalogRejectsInvalidTraitPrevalence()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(System.IO.Path.Combine(directory.RootPath, "traits.json"),
            "[{\"id\":\"greedy\",\"name\":\"Greedy\",\"bit\":1,\"prevalence\":1.5}]" );

        Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.RootPath));
    }

    [Fact]
    public void FatigueStressSystemUpdatesSchemaAttributesForTierOneAgents()
    {
        var catalog = LoadCatalog();
        var values = catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray();
        values[catalog.AgentAttributes.GetIndex("fatigue")] = 10f;
        values[catalog.AgentAttributes.GetIndex("stress")] = 20f;
        var store = new EntityStore();
        var entity = store.CreateEntity(new AgentAttributes { Values = values }, Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { new FatigueStressSystem(catalog.AgentAttributes, 0.5f) };

        root.Update(default);

        var updated = entity.GetComponent<AgentAttributes>().Values;
        Assert.Equal(10.5f, updated[catalog.AgentAttributes.GetIndex("fatigue")]);
        Assert.Equal(20.5f, updated[catalog.AgentAttributes.GetIndex("stress")]);
    }

    [Fact]
    public void FatigueAndStressResetIndependentlyAtTheThreshold()
    {
        var catalog = LoadCatalog();
        var values = catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray();
        values[catalog.AgentAttributes.GetIndex("fatigue")] = 99.95f;
        values[catalog.AgentAttributes.GetIndex("stress")] = 10f;
        var store = new EntityStore();
        var entity = store.CreateEntity(new AgentAttributes { Values = values }, Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { new FatigueStressSystem(catalog.AgentAttributes) };

        root.Update(default);

        var updated = entity.GetComponent<AgentAttributes>().Values;
        Assert.Equal(0f, updated[catalog.AgentAttributes.GetIndex("fatigue")]);
        Assert.Equal(10.1f, updated[catalog.AgentAttributes.GetIndex("stress")]);
    }

    [Fact]
    public void NonTierOneAgentsAreNotUpdated()
    {
        var catalog = LoadCatalog();
        var values = catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray();
        values[catalog.AgentAttributes.GetIndex("fatigue")] = 10f;
        values[catalog.AgentAttributes.GetIndex("stress")] = 20f;
        var store = new EntityStore();
        var entity = store.CreateEntity(new AgentAttributes { Values = values });
        var root = new SystemRoot(store) { new FatigueStressSystem(catalog.AgentAttributes, 1f) };

        root.Update(default);

        var updated = entity.GetComponent<AgentAttributes>().Values;
        Assert.Equal(10f, updated[catalog.AgentAttributes.GetIndex("fatigue")]);
        Assert.Equal(20f, updated[catalog.AgentAttributes.GetIndex("stress")]);
    }

    private static Entity CreateAgent(
        EntityStore store,
        ContentCatalog catalog,
        float perception,
        float willpower,
        long traitMask)
    {
        var values = catalog.AgentAttributes.Definitions
            .Select(definition => definition.Average)
            .ToArray();
        values[catalog.AgentAttributes.GetIndex("perception")] = perception;
        values[catalog.AgentAttributes.GetIndex("willpower")] = willpower;
        return store.CreateEntity(
            new Identity { NameId = 1 },
            new AgentAttributes { Values = values },
            new Psychology { TraitMask = traitMask },
            Tags.Get<Tier1LodTag>());
    }

    private static string[] RelationshipShape(EntityStore store)
    {
        var agents = store.Query<Identity>().Entities.ToArray();
        var agentIndexes = agents
            .Select((agent, index) => (agent, index))
            .ToDictionary(item => item.agent, item => item.index);

        return store.Query<EdgeData>().Entities
            .Select(entity => entity.GetComponent<EdgeData>())
            .Select(edge => $"{agentIndexes[edge.Source]}->{agentIndexes[edge.Target]}")
            .ToArray();
    }

    private sealed class SequenceRandom : Random
    {
        private readonly int[] _values;
        private int _index;

        public SequenceRandom(params int[] values) => _values = values;

        public override int Next(int maxValue)
        {
            if (maxValue <= 0)
            {
                return 0;
            }

            return Math.Abs(NextValue()) % maxValue;
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue >= maxValue)
            {
                return minValue;
            }

            return Math.Clamp(NextValue(), minValue, maxValue - 1);
        }

        private int NextValue()
        {
            if (_values.Length == 0)
            {
                return 0;
            }

            var value = _values[Math.Min(_index, _values.Length - 1)];
            _index++;
            return value;
        }
    }

    private sealed class TestContent : IDisposable
    {
        private TestContent(string path) => RootPath = path;

        public string RootPath { get; }

        public static TestContent CreateDirectory() =>
            new(Directory.CreateTempSubdirectory("proxystate-tests-").FullName);

        public static void CopyCatalogFiles(string directory)
        {
            var source = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            foreach (var fileName in new[] { "actions.json", "secret-states.json", "factions.json", "traits.json", "agent-schema.json", "jobs.json", "world.json", "networks.json", "lod.json" })
            {
                File.Copy(System.IO.Path.Combine(source, fileName), System.IO.Path.Combine(directory, fileName));
            }
        }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }

    private static void AdvanceMinutes(WorldClockSystem clock, SystemRoot root, int minutes)
    {
        clock.Advance(minutes * SimulationDefaults.RealSecondsPerSimulationDay /
            SimulationDefaults.SimulationMinutesPerDay);
        root.Update(default);
    }
}
