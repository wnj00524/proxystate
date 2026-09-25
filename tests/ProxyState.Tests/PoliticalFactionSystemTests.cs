using Friflo.Engine.ECS;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class PoliticalFactionSystemTests
{
    private static ContentCatalog LoadCatalog() => ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void FactionContentDefinesDistinctBranchingInfluenceAgendasAndStaffJobs()
    {
        var catalog = LoadCatalog();
        Assert.Equal(2, catalog.Factions.Count);
        Assert.All(catalog.Factions, faction =>
        {
            Assert.NotNull(faction.MetaGoal);
            Assert.True(faction.Goals!.Count >= 5);
            Assert.Contains(faction.Goals, goal => goal.Action == "recruit");
            Assert.Contains(faction.Goals, goal => goal.Action == "campaign");
            Assert.Contains(faction.Goals, goal => goal.Action == "office-seeking");
            Assert.Equal("elected", catalog.Jobs.Single(job => job.Id == faction.LeaderJobId).SelectionMethod);
            Assert.Null(catalog.Jobs.Single(job => job.Id == faction.ActivistJobId).SelectionMethod);
        });
        Assert.NotEqual(catalog.Factions[0].MetaGoal!.Id, catalog.Factions[1].MetaGoal!.Id);
    }

    [Fact]
    public void FactionGoalPrerequisitesAreValidated()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "data");
        var temporary = Directory.CreateTempSubdirectory("proxystate-factions-");
        try
        {
            foreach (var file in Directory.GetFiles(source, "*.json"))
                File.Copy(file, Path.Combine(temporary.FullName, Path.GetFileName(file)));
            var path = Path.Combine(temporary.FullName, "factions.json");
            var factions = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            factions[0]!["goals"]![1]!["prerequisites"]![0] = "missing-goal";
            File.WriteAllText(path, factions.ToJsonString());

            Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(temporary.FullName));
        }
        finally { temporary.Delete(recursive: true); }
    }

    [Fact]
    public void VolunteersChooseOneFactionAndKeepTheirRegularOccupation()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, 40, 4421);
        var system = new PoliticalFactionSystem(store, catalog, spawner.Indexes, spawner.LodService);
        var agent = store.Query<Identity>().Entities.First();
        var occupation = agent.GetComponent<Identity>().OccupationId;

        Assert.True(system.AcceptRecruitment(agent.Id, catalog.Factions[0].Id));
        Assert.False(system.AcceptRecruitment(agent.Id, catalog.Factions[1].Id));
        Assert.Equal(occupation, agent.GetComponent<Identity>().OccupationId);
        Assert.Equal(FactionMemberRole.Volunteer, agent.GetComponent<FactionParticipation>().Role);
        Assert.True(system.LeaveFaction(agent.Id));
        Assert.Equal(FactionMemberRole.None, agent.GetComponent<FactionParticipation>().Role);
        Assert.Equal(occupation, agent.GetComponent<Identity>().OccupationId);
    }

    [Fact]
    public void RecruitmentAndGoalProgressIncludeCoarseLodAgents()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, 120, 8041);
        var faction = catalog.Factions[0];
        var coarse = store.Query<Identity>().Entities.First(agent => agent.Tags.Has<Tier3LodTag>());
        foreach (var agent in store.Query<Identity>().Entities)
        {
            agent.GetComponent<PoliticalAlignment>().FactionId = faction.FactionId;
            ref var attributes = ref agent.GetComponent<AgentAttributes>();
            attributes.Values[catalog.Politics.PoliticalEngagementAttributeIndex] = 20;
            attributes.Values[catalog.Politics.MotivationAttributeIndex] = 20;
        }
        ref var coarseAttributes = ref coarse.GetComponent<AgentAttributes>();
        coarseAttributes.Values[catalog.Politics.PoliticalEngagementAttributeIndex] = 100;
        coarseAttributes.Values[catalog.Politics.MotivationAttributeIndex] = 100;

        var system = new PoliticalFactionSystem(store, catalog, spawner.Indexes, spawner.LodService);
        system.Update(1);

        Assert.Equal(FactionMemberRole.Volunteer, coarse.GetComponent<FactionParticipation>().Role);
        var snapshot = system.Snapshots.Single(value => value.Id == faction.Id);
        Assert.True(snapshot.Members >= 1);
        Assert.Equal("blue-recruit", snapshot.ActiveGoalId);
        Assert.Equal("red-recruit", system.Snapshots.Single(value => value.Id == catalog.Factions[1].Id).ActiveGoalId);
    }

    [Fact]
    public void ElectedLeaderSelectsFullTimeActivistsAndVolunteersCanLeaveTheirStaffJob()
    {
        var catalog = LoadCatalog();
        var faction = catalog.Factions[0];
        var leaderJob = catalog.Jobs.Single(job => job.Id == faction.LeaderJobId);
        var activistJob = catalog.Jobs.Single(job => job.Id == faction.ActivistJobId);
        var store = new EntityStore();
        var leader = CreateAgent(store, catalog, faction.FactionId, true);
        leader.GetComponent<Identity>().OccupationId = leaderJob.Hash;
        var volunteer = CreateAgent(store, catalog, faction.FactionId, true);
        var oldOccupation = volunteer.GetComponent<Identity>().OccupationId;
        var values = volunteer.GetComponent<AgentAttributes>();
        values.Values[catalog.Politics.PoliticalEngagementAttributeIndex] = 100;
        values.Values[catalog.Politics.MotivationAttributeIndex] = 100;
        volunteer.GetComponent<AgentAttributes>() = values;
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var system = new PoliticalFactionSystem(store, catalog, indexes);

        system.Update(1);

        Assert.Equal(activistJob.Hash, volunteer.GetComponent<Identity>().OccupationId);
        Assert.Equal(FactionMemberRole.Activist, volunteer.GetComponent<FactionParticipation>().Role);
        Assert.True(system.LeaveFaction(volunteer.Id));
        Assert.Equal(oldOccupation, volunteer.GetComponent<Identity>().OccupationId);
    }

    [Fact]
    public void LeaderAdvancesEligibleOrganizingAndCampaignBranches()
    {
        var catalog = LoadCatalog();
        var faction = catalog.Factions[0];
        var store = new EntityStore();
        var leader = CreateAgent(store, catalog, faction.FactionId, true);
        leader.GetComponent<Identity>().OccupationId = catalog.Jobs.Single(job => job.Id == faction.LeaderJobId).Hash;
        for (var index = 0; index < 23; index++) CreateAgent(store, catalog, faction.FactionId, true);
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var system = new PoliticalFactionSystem(store, catalog, indexes);

        for (var day = 1; day <= 42; day++) system.Update(day);

        var snapshot = system.Snapshots.Single(value => value.Id == faction.Id);
        Assert.Contains("blue-recruit", snapshot.CompletedGoals);
        Assert.Contains("blue-organize", snapshot.CompletedGoals);
        Assert.Contains("blue-housing", snapshot.CompletedGoals);
        Assert.True(snapshot.Organization >= 45);
        Assert.True(snapshot.PublicSupport >= 58);
    }

    [Fact]
    public void FactionLeaderElectionUsesPhysicalBallotsFromMembersOnly()
    {
        var catalog = LoadCatalog();
        var faction = catalog.Factions[0];
        var leaderJob = catalog.Jobs.Single(job => job.Id == faction.LeaderJobId);
        var store = new EntityStore();
        var clock = store.CreateEntity(new WorldTime());
        var candidate = CreateAgent(store, catalog, faction.FactionId, true);
        candidate.GetComponent<PoliticalParticipation>() = new PoliticalParticipation
        {
            CandidateElectionId = 1,
            CandidateJobHash = leaderJob.Hash
        };
        var members = Enumerable.Range(0, 12).Select(_ => CreateAgent(store, catalog, faction.FactionId, true)).ToArray();
        var outsider = CreateAgent(store, catalog, faction.FactionId, false);
        outsider.GetComponent<AgentAttributes>().Values[catalog.AgentAttributes.GetIndex("charisma")] = 100;
        outsider.GetComponent<PoliticalParticipation>() = new PoliticalParticipation
        {
            CandidateElectionId = 1,
            CandidateJobHash = leaderJob.Hash
        };
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        var system = new PoliticalSystem(store, catalog, clock, indexes);
        var opening = 27L * SimulationDefaults.SimulationMinutesPerDay + catalog.Politics.PollOpeningMinute;
        SetMinute(clock, opening);
        system.Update();
        SetMinute(clock, opening + 1);
        system.Update();

        Assert.All(members, member => Assert.Equal(1, member.GetComponent<PoliticalParticipation>().VotedElectionId));
        Assert.Equal(1, outsider.GetComponent<PoliticalParticipation>().VotedElectionId);
        SetMinute(clock, 27L * SimulationDefaults.SimulationMinutesPerDay + catalog.Politics.PollClosingMinute);
        system.Update();
        Assert.Equal(leaderJob.Hash, candidate.GetComponent<Identity>().OccupationId);
    }

    private static Entity CreateAgent(EntityStore store, ContentCatalog catalog, byte factionId, bool member)
    {
        var values = new float[catalog.AgentAttributes.Count];
        values[catalog.Politics.PoliticalEngagementAttributeIndex] = 100;
        values[catalog.Politics.MotivationAttributeIndex] = 100;
        values[catalog.AgentAttributes.GetIndex("charisma")] = 70;
        var at = catalog.Politics.PollingLocationId;
        var agent = store.CreateEntity(new Identity { OccupationId = catalog.Jobs.First(job => job.SelectionMethod is null && job.FactionRole is null).Hash },
            new PoliticalAlignment { FactionId = factionId },
            new AgentAttributes { Values = values },
            new AgentLocation { HomeLocationId = at, WorkLocationId = at, CurrentLocationId = at },
            new PoliticalParticipation(),
            new FactionParticipation
            {
                FactionId = member ? factionId : byte.MaxValue,
                Role = member ? FactionMemberRole.Volunteer : FactionMemberRole.None
            });
        agent.AddComponent(new AgentCommute());
        return agent;
    }

    private static void SetMinute(Entity clock, long minute) =>
        clock.GetComponent<WorldTime>() = new WorldTime
        {
            ElapsedSimulationSeconds = minute * SimulationDefaults.SimulationSecondsPerMinute
        };
}
