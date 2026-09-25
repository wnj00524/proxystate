using Friflo.Engine.ECS;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class PoliticalSystemTests
{
    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void PopulationStartsWithoutRandomPoliticalOfficeAssignments()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);

        spawner.Spawn(store, 180, 9071);

        var politicalHashes = catalog.Jobs.Where(job => job.SelectionMethod is not null)
            .Select(job => job.Hash).ToHashSet();
        Assert.All(store.Query<Identity>().Entities, agent =>
            Assert.DoesNotContain(agent.GetComponent<Identity>().OccupationId, politicalHashes));
        Assert.All(store.Query<Identity>().Entities, agent =>
            Assert.True(agent.HasComponent<PoliticalParticipation>()));
    }

    [Fact]
    public void CoarseAgentsCanRegisterAndTravelToThePollingLocation()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, 80, 3049);
        var coarseAgents = store.Query<Identity>().Entities
            .Where(agent => agent.Tags.Has<Tier3LodTag>()).ToArray();
        Assert.NotEmpty(coarseAgents);
        var candidate = coarseAgents[0];
        ref var attributes = ref candidate.GetComponent<AgentAttributes>();
        attributes.Values[catalog.Politics.PoliticalEngagementAttributeIndex] = 100;
        attributes.Values[catalog.Politics.MotivationAttributeIndex] = 100;
        var clock = new WorldClockSystem(store);
        var politics = new PoliticalSystem(store, catalog, clock.ClockEntity, spawner.Indexes, spawner.LodService);

        var nominationStart = 20L * SimulationDefaults.SimulationMinutesPerDay;
        SetMinute(clock.ClockEntity, nominationStart);
        politics.Update();
        var participation = candidate.GetComponent<PoliticalParticipation>();
        Assert.Equal(PoliticalTripKind.Nomination, participation.TripKind);
        SetMinute(clock.ClockEntity, participation.TripArrivalMinute);
        politics.Update();

        Assert.NotEqual(0, candidate.GetComponent<PoliticalParticipation>().CandidateJobHash);
        Assert.Equal(catalog.Politics.PollingLocationId, candidate.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.True(candidate.Tags.Has<Tier3LodTag>());
        Assert.False(candidate.HasComponent<DecisionState>());
    }

    [Fact]
    public void EngagedAgentsRegisterAtTownHallAndUnengagedAgentsDoNot()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = store.CreateEntity(new WorldTime());
        var engaged = CreateAgent(store, catalog, 2001, 0, 100, 100, 55, 3001);
        var unengaged = CreateAgent(store, catalog, 2001, 0, 0, 0, 50, 3001);
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var politics = new PoliticalSystem(store, catalog, clock, indexes);

        SetMinute(clock, 20L * SimulationDefaults.SimulationMinutesPerDay);
        politics.Update();

        Assert.Equal(PoliticalTripKind.Nomination, engaged.GetComponent<PoliticalParticipation>().TripKind);
        Assert.Equal(PoliticalTripKind.None, unengaged.GetComponent<PoliticalParticipation>().TripKind);
        var arrival = engaged.GetComponent<PoliticalParticipation>().TripArrivalMinute;
        SetMinute(clock, arrival);
        politics.Update();

        Assert.Equal(catalog.Politics.PollingLocationId, engaged.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.NotEqual(0, engaged.GetComponent<PoliticalParticipation>().CandidateJobHash);
        SetMinute(clock, engaged.GetComponent<PoliticalParticipation>().TripReturnMinute);
        politics.Update();
        Assert.Equal(3001, engaged.GetComponent<AgentLocation>().CurrentLocationId);
    }

    [Fact]
    public void ElectionHonorsFactionPreferenceAndMayorAppointsCommissionerApplicant()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = store.CreateEntity(new WorldTime());
        var mayor = catalog.Jobs.Single(job => job.Id == "mayor");
        var council = catalog.Jobs.Single(job => job.Id == "council-member");
        var commissioner = catalog.Jobs.Single(job => job.Id == "public-works-commissioner");
        var redFaction = catalog.Factions[0].FactionId;
        var blueFaction = catalog.Factions[1].FactionId;

        var redCandidate = CreateAgent(store, catalog, 2001, redFaction, 85, 85, 96, catalog.Politics.PollingLocationId);
        var redRival = CreateAgent(store, catalog, 2001, redFaction, 85, 85, 75, catalog.Politics.PollingLocationId);
        var blueCandidate = CreateAgent(store, catalog, 2001, blueFaction, 85, 85, 100, catalog.Politics.PollingLocationId);
        var councilCandidate = CreateAgent(store, catalog, 2001, redFaction, 85, 85, 80, catalog.Politics.PollingLocationId);
        var commissionerApplicant = CreateAgent(store, catalog, 2001, redFaction, 85, 85, 70, catalog.Politics.PollingLocationId);

        MarkCandidate(redCandidate, mayor.Hash);
        MarkCandidate(redRival, mayor.Hash);
        MarkCandidate(blueCandidate, mayor.Hash);
        MarkCandidate(councilCandidate, council.Hash);
        MarkCandidate(commissionerApplicant, commissioner.Hash);
        for (var index = 0; index < 12; index++)
            CreateAgent(store, catalog, 2001, redFaction, 100, 100, 40, catalog.Politics.PollingLocationId);
        CreateAgent(store, catalog, 2001, blueFaction, 100, 100, 40, catalog.Politics.PollingLocationId);

        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var politics = new PoliticalSystem(store, catalog, clock, indexes);
        var electionStart = 27L * SimulationDefaults.SimulationMinutesPerDay + catalog.Politics.PollOpeningMinute;
        SetMinute(clock, electionStart);
        politics.Update();
        SetMinute(clock, electionStart + 1);
        politics.Update();
        Assert.Equal(catalog.Politics.PollingLocationId, redCandidate.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(1, redCandidate.GetComponent<PoliticalParticipation>().VotedElectionId);

        SetMinute(clock, 27L * SimulationDefaults.SimulationMinutesPerDay + catalog.Politics.PollClosingMinute);
        politics.Update();

        Assert.Equal(1, politics.ResolvedElectionCount);
        Assert.True(politics.LastElectionVoteCount >= 10);
        Assert.Equal(mayor.Hash, redCandidate.GetComponent<Identity>().OccupationId);
        Assert.Equal(2001, redCandidate.GetComponent<PoliticalParticipation>().PreviousOccupationId);
        Assert.Equal(commissioner.Hash, commissionerApplicant.GetComponent<Identity>().OccupationId);
    }

    [Fact]
    public void SocialPressureCanTipAResidentTowardVoting()
    {
        var catalog = LoadCatalog();
        var thresholdAgentId = Enumerable.Range(1, 256).First(id =>
        {
            var threshold = 27f + StableUnit(id, 1, 37) * 48f;
            return threshold > 55.1f && threshold < 65.9f;
        });
        var withoutContacts = RunSocialPressureCase(catalog, thresholdAgentId, withContacts: false);
        var withContacts = RunSocialPressureCase(catalog, thresholdAgentId, withContacts: true);

        Assert.Equal(0, withoutContacts.GetComponent<PoliticalParticipation>().VotedElectionId);
        Assert.Equal(1, withContacts.GetComponent<PoliticalParticipation>().VotedElectionId);
    }

    [Fact]
    public void ScheduledWorkPressureReducesElectionDayTurnout()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = store.CreateEntity(new WorldTime());
        var offWork = new List<Entity>();
        var working = new List<Entity>();
        for (var index = 0; index < 256; index++)
        {
            offWork.Add(CreateAgent(store, catalog, 2001, catalog.Factions[0].FactionId,
                60, 60, 50, catalog.Politics.PollingLocationId));
            working.Add(CreateAgent(store, catalog, 2012, catalog.Factions[0].FactionId,
                60, 60, 50, catalog.Politics.PollingLocationId));
        }
        foreach (var agent in offWork.Concat(working))
            agent.GetComponent<PoliticalParticipation>().CandidateElectionId = 1;

        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var politics = new PoliticalSystem(store, catalog, clock, indexes);
        var opening = 27L * SimulationDefaults.SimulationMinutesPerDay + catalog.Politics.PollOpeningMinute;
        SetMinute(clock, opening);
        politics.Update();
        SetMinute(clock, opening + 1);
        politics.Update();

        var offWorkVotes = offWork.Count(agent => agent.GetComponent<PoliticalParticipation>().VotedElectionId == 1);
        var workingVotes = working.Count(agent => agent.GetComponent<PoliticalParticipation>().VotedElectionId == 1);
        Assert.True(offWorkVotes > workingVotes, $"Expected schedule pressure to reduce turnout ({offWorkVotes} vs {workingVotes}).");
    }

    [Fact]
    public void LongTravelToPollingPlaceReducesTurnout()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var clock = store.CreateEntity(new WorldTime());
        var nearby = new List<Entity>();
        var distant = new List<Entity>();
        for (var index = 0; index < 256; index++)
        {
            nearby.Add(CreateAgent(store, catalog, 2001, catalog.Factions[0].FactionId,
                60, 60, 50, catalog.Politics.PollingLocationId));
            distant.Add(CreateAgent(store, catalog, 2001, catalog.Factions[0].FactionId,
                60, 60, 50, 3001));
        }
        foreach (var agent in nearby.Concat(distant))
            agent.GetComponent<PoliticalParticipation>().CandidateElectionId = 1;
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var politics = new PoliticalSystem(store, catalog, clock, indexes);
        var opening = 27L * SimulationDefaults.SimulationMinutesPerDay + catalog.Politics.PollOpeningMinute;
        SetMinute(clock, opening);
        politics.Update();
        SetMinute(clock, opening + 22);
        politics.Update();

        var nearbyVotes = nearby.Count(agent => agent.GetComponent<PoliticalParticipation>().VotedElectionId == 1);
        var distantVotes = distant.Count(agent => agent.GetComponent<PoliticalParticipation>().VotedElectionId == 1);
        Assert.True(nearbyVotes > distantVotes, $"Expected travel access pressure to reduce turnout ({nearbyVotes} vs {distantVotes}).");
    }

    [Fact]
    public void CatalogLoadsElectionRulesAndValidatesAppointingOffices()
    {
        var catalog = LoadCatalog();
        Assert.Equal(28, catalog.Politics.ElectionIntervalDays);
        Assert.Equal(7, catalog.Politics.NominationDays);
        Assert.Equal("elected", catalog.Jobs.Single(job => job.Id == "mayor").SelectionMethod);
        Assert.Equal("appointed", catalog.Jobs.Single(job => job.Id == "public-works-commissioner").SelectionMethod);
        Assert.Equal("mayor", catalog.Jobs.Single(job => job.Id == "public-works-commissioner").AppointedByJobId);

        var source = Path.Combine(AppContext.BaseDirectory, "data");
        var temporary = Directory.CreateTempSubdirectory("proxystate-politics-");
        try
        {
            foreach (var file in Directory.GetFiles(source, "*.json"))
                File.Copy(file, Path.Combine(temporary.FullName, Path.GetFileName(file)));
            var jobsPath = Path.Combine(temporary.FullName, "jobs.json");
            var jobs = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jobsPath))!.AsArray();
            var commissioner = jobs.Single(job => (string?)job!["id"] == "public-works-commissioner")!;
            commissioner["appointedByJobId"] = "civic-clerk";
            File.WriteAllText(jobsPath, jobs.ToJsonString());

            Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(temporary.FullName));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static Entity RunSocialPressureCase(ContentCatalog catalog, int targetAgentId, bool withContacts)
    {
        var store = new EntityStore();
        var clock = store.CreateEntity(new WorldTime());
        var nextEntityId = 2;
        while (nextEntityId < targetAgentId)
        {
            store.CreateEntity(new AgentState());
            nextEntityId++;
        }
        var voter = CreateAgent(store, catalog, 2001, catalog.Factions[0].FactionId, 50, 50, 50,
            catalog.Politics.PollingLocationId);
        Assert.Equal(targetAgentId, voter.Id);
        if (withContacts)
        {
            for (var index = 0; index < 4; index++)
            {
                var contact = CreateAgent(store, catalog, 2001, catalog.Factions[0].FactionId, 100, 100, 50,
                    catalog.Politics.PollingLocationId);
                store.CreateEntity(new EdgeData { Source = voter, Target = contact, Affinity = 1f });
            }
        }
        voter.GetComponent<PoliticalParticipation>().CandidateElectionId = 1;
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var politics = new PoliticalSystem(store, catalog, clock, indexes);
        var opening = 27L * SimulationDefaults.SimulationMinutesPerDay + catalog.Politics.PollOpeningMinute;
        SetMinute(clock, opening);
        politics.Update();
        SetMinute(clock, opening + 1);
        politics.Update();
        return voter;
    }

    private static Entity CreateAgent(EntityStore store, ContentCatalog catalog, int occupationHash,
        byte faction, float engagement, float motivation, float charisma, int locationId)
    {
        var values = new float[AgentAttributeValuesLength(catalog)];
        values[catalog.Politics.PoliticalEngagementAttributeIndex] = engagement;
        values[catalog.Politics.MotivationAttributeIndex] = motivation;
        values[catalog.AgentAttributes.GetIndex("charisma")] = charisma;
        var location = catalog.World.GetLocation(locationId);
        var agent = store.CreateEntity(
            new Identity { OccupationId = occupationHash },
            new PoliticalAlignment { FactionId = faction },
            new AgentAttributes { Values = values },
            new AgentLocation { HomeLocationId = locationId, WorkLocationId = locationId, CurrentLocationId = location.Hash },
            new PoliticalParticipation());
        var route = catalog.World.FindShortestRoute(locationId, locationId)!;
        agent.AddComponent(new AgentCommute { TravelMinutes = route.TravelMinutes });
        return agent;
    }

    private static int AgentAttributeValuesLength(ContentCatalog catalog) => catalog.AgentAttributes.Count;

    private static void MarkCandidate(Entity agent, int officeHash)
    {
        ref var participation = ref agent.GetComponent<PoliticalParticipation>();
        participation.CandidateJobHash = officeHash;
        participation.CandidateElectionId = 1;
    }

    private static void SetMinute(Entity clock, long minute) =>
        clock.GetComponent<WorldTime>() = new WorldTime
        {
            ElapsedSimulationSeconds = minute * SimulationDefaults.SimulationSecondsPerMinute
        };

    private static float StableUnit(int agentId, int electionId, int salt)
    {
        var value = unchecked((uint)agentId * 0x9E3779B9u ^ (uint)electionId * 0x85EBCA6Bu ^ (uint)salt * 0xC2B2AE35u);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value / (float)uint.MaxValue;
    }
}
