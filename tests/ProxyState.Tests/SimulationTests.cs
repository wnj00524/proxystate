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
            typeof(AgentState),
            typeof(WorldTime),
            typeof(AgentLocation),
            typeof(AgentTravel)
        };
        var tags = new[] { typeof(Tier1LodTag), typeof(Tier2LodTag), typeof(Tier3LodTag) };

        Assert.All(components, type => Assert.True(typeof(IComponent).IsAssignableFrom(type), type.Name));
        Assert.All(tags, type => Assert.True(typeof(ITag).IsAssignableFrom(type), type.Name));
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
    public void SpawnerCreatesTheRequestedPopulationWithGeneralizedAttributesAndTierOneTag()
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
        Assert.Equal(SimulationDefaults.AgentCount, store.Query<AgentAttributes>().AllTags(Tags.Get<Tier1LodTag>()).Count);
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
            var travel = entity.GetComponent<AgentTravel>();

            Assert.Equal(location.HomeLocationId, location.CurrentLocationId);
            Assert.Equal(SimulationDefaults.ResidentialLocationType,
                catalog.World.GetLocation(location.HomeLocationId).Type);
            Assert.Equal(job.WorkplaceType,
                catalog.World.GetLocation(location.WorkLocationId).Type,
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(location.HomeLocationId, travel.RouteLocationIds[0]);
            Assert.Equal(location.WorkLocationId, travel.RouteLocationIds[^1]);
            Assert.Equal(AgentTravelMode.AtHome, travel.Mode);
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
    public void CommutingSystemMovesAgentToWorkAndBackHomeOnAWorkday()
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
                Mode = AgentTravelMode.AtHome
            },
            new AgentState { CurrentActionHash = 1002 },
            Tags.Get<Tier1LodTag>());
        var commuting = new CommutingSystem(catalog, clock.ClockEntity);
        var root = new SystemRoot(store) { clock, commuting };

        AdvanceMinutes(clock, root, 455);
        Assert.Equal(AgentTravelMode.TravellingToWork, entity.GetComponent<AgentTravel>().Mode);

        AdvanceMinutes(clock, root, 25);
        Assert.Equal(AgentTravelMode.AtWork, entity.GetComponent<AgentTravel>().Mode);
        Assert.Equal(3004, entity.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(1001, entity.GetComponent<AgentState>().CurrentActionHash);

        AdvanceMinutes(clock, root, 480);
        Assert.Equal(AgentTravelMode.TravellingHome, entity.GetComponent<AgentTravel>().Mode);

        AdvanceMinutes(clock, root, 25);
        Assert.Equal(AgentTravelMode.AtHome, entity.GetComponent<AgentTravel>().Mode);
        Assert.Equal(3001, entity.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(1002, entity.GetComponent<AgentState>().CurrentActionHash);
    }

    [Fact]
    public void CommutingSystemKeepsAgentHomeOnANonWorkday()
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
                Mode = AgentTravelMode.AtHome
            },
            new AgentState { CurrentActionHash = 1002 },
            Tags.Get<Tier1LodTag>());
        var root = new SystemRoot(store) { clock, new CommutingSystem(catalog, clock.ClockEntity) };

        AdvanceMinutes(clock, root, 6 * SimulationDefaults.SimulationMinutesPerDay + 600);

        Assert.Equal(AgentTravelMode.AtHome, entity.GetComponent<AgentTravel>().Mode);
        Assert.Equal(3001, entity.GetComponent<AgentLocation>().CurrentLocationId);
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
            Assert.Contains(entity.GetComponent<AgentState>().CurrentActionHash,
                catalog.Actions.Select(action => action.Hash));
        }
    }

    [Fact]
    public void SchemaSupportsAdditionalAttributesWithoutSpawnerChanges()
    {
        using var directory = TestContent.CreateDirectory();
        TestContent.CopyCatalogFiles(directory.RootPath);
        File.WriteAllText(System.IO.Path.Combine(directory.RootPath, "agent-schema.json"),
            "{\"attributes\":[{\"id\":\"fatigue\",\"min\":0,\"max\":100,\"average\":20},{\"id\":\"stress\",\"min\":0,\"max\":100,\"average\":20},{\"id\":\"luck\",\"min\":-5,\"max\":5,\"average\":1}]}" );

        var catalog = ContentCatalog.Load(directory.RootPath);
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 10, new Random(7));

        Assert.Equal(3, catalog.AgentAttributes.Count);
        Assert.Equal(2, catalog.AgentAttributes.GetIndex("luck"));
        Assert.All(store.Query<AgentAttributes>().Entities,
            entity => Assert.Equal(3, entity.GetComponent<AgentAttributes>().Values.Length));
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
        File.WriteAllText(System.IO.Path.Combine(directory.RootPath, "agent-schema.json"),
            "{\"attributes\":[{\"id\":\"fixed\",\"min\":7,\"max\":7,\"average\":7},{\"id\":\"fatigue\",\"min\":0,\"max\":100,\"average\":20},{\"id\":\"stress\",\"min\":0,\"max\":100,\"average\":20}]}" );

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
            "[{\"id\":\"never\",\"name\":\"Never\",\"bit\":1,\"prevalence\":0},{\"id\":\"always\",\"name\":\"Always\",\"bit\":2,\"prevalence\":1}]" );

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

    private sealed class TestContent : IDisposable
    {
        private TestContent(string path) => RootPath = path;

        public string RootPath { get; }

        public static TestContent CreateDirectory() =>
            new(Directory.CreateTempSubdirectory("proxystate-tests-").FullName);

        public static void CopyCatalogFiles(string directory)
        {
            var source = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            foreach (var fileName in new[] { "actions.json", "factions.json", "traits.json", "agent-schema.json", "jobs.json", "world.json" })
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
