using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState.Simulation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ProxyState.Tests;

public sealed class RelationshipDrivenLivesTests
{
    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void TownWideFriendGroupsAreBoundedDeterministicAndSeedSocialEdges()
    {
        var catalog = LoadCatalog();
        var first = new EntityStore();
        var second = new EntityStore();
        new AgentSpawner(catalog).Spawn(first, 100, 16_001);
        new AgentSpawner(catalog).Spawn(second, 100, 16_001);
        var friendType = catalog.Networks.GetType("friend-group");

        static string[] Capture(EntityStore store, int typeHash) => store.Query<AgentNetworkData>().Entities
            .Where(network => network.GetComponent<AgentNetworkData>().TypeHash == typeHash)
            .OrderBy(network => network.Id)
            .Select(network => string.Join(',', network.GetIncomingLinks<AgentNetworkMembership>()
                .Select(link => link.Entity.Id).OrderBy(id => id)))
            .ToArray();

        Assert.Equal(Capture(first, friendType.Hash), Capture(second, friendType.Hash));
        var groups = first.Query<AgentNetworkData>().Entities
            .Where(network => network.GetComponent<AgentNetworkData>().TypeHash == friendType.Hash).ToArray();
        Assert.All(groups, group =>
        {
            Assert.Equal(0, group.GetComponent<AgentNetworkData>().AnchorLocationId);
            Assert.InRange(group.GetIncomingLinks<AgentNetworkMembership>().Count(), 3, 6);
        });
        Assert.Equal(100, groups.Sum(group => group.GetIncomingLinks<AgentNetworkMembership>().Count()));

        var edges = first.Query<EdgeData>().Entities.Select(entity => entity.GetComponent<EdgeData>()).ToArray();
        Assert.Equal(edges.Length, edges.Select(edge => (edge.Source.Id, edge.Target.Id)).Distinct().Count());
        foreach (var group in groups)
        {
            var members = group.GetIncomingLinks<AgentNetworkMembership>().Select(link => link.Entity).ToArray();
            foreach (var source in members)
                foreach (var target in members.Where(target => target != source))
                    Assert.Contains(edges, edge => edge.Source == source && edge.Target == target);
        }
    }

    [Fact]
    public void NetworkSelectorsResolveMembersSupervisorsAndDirectReports()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var head = CreateAgent(store, catalog, 1, stress: 10);
        var manager = CreateAgent(store, catalog, 2, stress: 40);
        var employee = CreateAgent(store, catalog, 3, stress: 80);
        var networks = new AgentNetworkService(store, catalog.Networks);
        var company = networks.CreateNetwork(catalog.Networks.GetType("company").Hash, 3004, 0);
        networks.AddMembership(head, company, catalog.Networks.GetRole("company-head").Hash);
        networks.AddMembership(manager, company, catalog.Networks.GetRole("company-manager").Hash, head);
        networks.AddMembership(employee, company, catalog.Networks.GetRole("company-employee").Hash, manager);
        var clock = store.CreateEntity(new WorldTime
        {
            ElapsedSimulationSeconds = 600 * SimulationDefaults.SimulationSecondsPerMinute,
            DeltaSimulationSeconds = SimulationDefaults.SimulationSecondsPerMinute
        });
        var root = new SystemRoot(store) { new AgentDecisionSystem(store, catalog, clock) };

        root.Update(default);

        AssertTarget(manager, "report-to-supervisor", head);
        AssertTarget(manager, "manage-report", employee);
        AssertTarget(employee, "report-to-supervisor", manager);
        AssertTarget(head, "manage-report", manager);
        Assert.NotEqual(0, TargetFor(manager, "collaborate"));

        void AssertTarget(Entity actor, string intent, Entity expected) =>
            Assert.Equal(expected.Id, TargetFor(actor, intent));
        int TargetFor(Entity actor, string intent)
        {
            var index = catalog.Intents.All.Single(candidate => candidate.Id == intent).RuntimeIndex;
            return actor.GetComponent<DecisionState>().CachedTargetEntityIds[index];
        }
    }

    [Fact]
    public void MutualFriendActivityPairsWaitsPerformsEffectsAndReleasesAtMaximum()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = CreateAgent(store, catalog, 1, stress: 50, preference: 1, charisma: 100, location: 3001);
        var second = CreateAgent(store, catalog, 2, stress: 50, preference: 1, charisma: 100, location: 3001);
        LinkFriends(store, catalog, first, second);
        var clock = store.CreateEntity(new WorldTime());
        var root = CreateSimulationRoot(store, catalog, clock);

        Advance(clock, 1_020);
        root.Update(default);
        Assert.True(first.GetComponent<CoordinationState>().Active);
        Assert.True(second.GetComponent<CoordinationState>().Active);
        Assert.Equal(ActivityPhase.Waiting, first.GetComponent<ActivityState>().Phase);
        Assert.Equal(ActivityPhase.Waiting, second.GetComponent<ActivityState>().Phase);

        var stressIndex = catalog.AgentAttributes.GetIndex("stress");
        var beforeFirst = first.GetComponent<AgentAttributes>().Values[stressIndex];
        var beforeSecond = second.GetComponent<AgentAttributes>().Values[stressIndex];
        Advance(clock, 1_021);
        root.Update(default);
        Assert.Equal(ActivityPhase.Performing, first.GetComponent<ActivityState>().Phase);
        Assert.Equal(ActivityPhase.Performing, second.GetComponent<ActivityState>().Phase);
        Assert.True(first.GetComponent<AgentAttributes>().Values[stressIndex] < beforeFirst);
        Assert.True(second.GetComponent<AgentAttributes>().Values[stressIndex] < beforeSecond);

        Advance(clock, 1_111);
        root.Update(default);
        Assert.False(first.GetComponent<CoordinationState>().Active);
        Assert.False(second.GetComponent<CoordinationState>().Active);
        Assert.Equal(catalog.Intents.Fallback.Hash, first.GetComponent<IntentionState>().ActionHash);
        Assert.Equal(catalog.Intents.Fallback.Hash, second.GetComponent<IntentionState>().ActionHash);
    }

    [Fact]
    public void InvitationArbitrationNeverDoubleBooksAndCoolsDownTheRejectedInitiator()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = CreateAgent(store, catalog, 1, stress: 50, preference: 1, charisma: 100);
        var second = CreateAgent(store, catalog, 2, stress: 50, preference: 1, charisma: 100);
        var third = CreateAgent(store, catalog, 3, stress: 50, preference: 1, charisma: 100);
        LinkFriends(store, catalog, first, second, third);
        var clock = store.CreateEntity(new WorldTime());
        var root = CreateSimulationRoot(store, catalog, clock);

        Advance(clock, 1_020);
        root.Update(default);

        var active = new[] { first, second, third }.Where(agent => agent.GetComponent<CoordinationState>().Active).ToArray();
        Assert.Equal(2, active.Length);
        Assert.Equal(active[0].Id, active[1].GetComponent<CoordinationState>().PartnerEntityId);
        Assert.Equal(active[1].Id, active[0].GetComponent<CoordinationState>().PartnerEntityId);
        var rejected = new[] { first, second, third }.Single(agent => !agent.GetComponent<CoordinationState>().Active);
        var decision = rejected.GetComponent<DecisionState>();
        var cooldownIndex = ((ReadOnlySpan<int>)decision.CooldownActionHashes).IndexOf(1003);
        Assert.True(cooldownIndex >= 0);
        Assert.Equal(1_035, decision.CooldownUntilMinutes[cooldownIndex]);
    }

    [Fact]
    public void CoordinationGroundTruthDoesNotEnterPlayerIntelligence()
    {
        Assert.DoesNotContain(typeof(PlayerIntelligenceAgentSnapshot).GetProperties(), property =>
            property.Name.Contains("Coordination", StringComparison.OrdinalIgnoreCase) ||
            property.PropertyType == typeof(CoordinationState));
        Assert.Contains(typeof(DebugAgentSnapshot).GetProperties(), property =>
            property.Name == nameof(DebugAgentSnapshot.Coordination));
    }

    [Theory]
    [InlineData("socialize")]
    [InlineData("family-time")]
    [InlineData("support-family")]
    [InlineData("report-to-supervisor")]
    [InlineData("manage-report")]
    [InlineData("collaborate")]
    public void EveryRelationshipBehaviorCanWinAndApplyItsAuthoredEffects(string intentId)
    {
        using var content = RelationshipContent.Create(intentId);
        var catalog = ContentCatalog.Load(content.Directory);
        var store = new EntityStore();
        var actor = CreateAgent(store, catalog, 1, stress: 80, preference: 1, charisma: 100, wealth: 5_000);
        var partner = CreateAgent(store, catalog, 2, stress: 80, preference: 1, charisma: 100, wealth: 1_000);

        switch (intentId)
        {
            case "socialize":
                LinkFriends(store, catalog, actor, partner);
                break;
            case "family-time":
            case "support-family":
                LinkFlatNetwork(store, catalog, "family", "family-member", actor, partner);
                break;
            case "report-to-supervisor":
                LinkCompany(store, catalog, partner, actor);
                break;
            case "manage-report":
            case "collaborate":
                LinkCompany(store, catalog, actor, partner);
                break;
        }

        var clock = store.CreateEntity(new WorldTime());
        var root = CreateSimulationRoot(store, catalog, clock);
        var startMinute = intentId is "socialize" or "family-time" ? 1_020 : 600;
        Advance(clock, startMinute);
        root.Update(default);

        var intent = catalog.Intents.All.Single(candidate => candidate.Id == intentId);
        Assert.Equal(intent.Hash, actor.GetComponent<IntentionState>().ActionHash);
        Assert.Equal(partner.Id, actor.GetComponent<CoordinationState>().PartnerEntityId);
        Assert.Equal(CoordinationRole.Initiator, actor.GetComponent<CoordinationState>().Role);
        var before = new Dictionary<int, float[]>
        {
            [actor.Id] = ((ReadOnlySpan<float>)actor.GetComponent<AgentAttributes>().Values).ToArray(),
            [partner.Id] = ((ReadOnlySpan<float>)partner.GetComponent<AgentAttributes>().Values).ToArray()
        };

        Advance(clock, startMinute + 1);
        root.Update(default);

        Assert.Equal(ActivityPhase.Performing, actor.GetComponent<ActivityState>().Phase);
        Assert.Equal(ActivityPhase.Performing, partner.GetComponent<ActivityState>().Phase);
        foreach (var effect in intent.Effects)
        {
            var affected = effect.Subject == EffectSubject.Initiator ? actor : partner;
            var definition = catalog.AgentAttributes.Definitions[effect.AttributeIndex];
            var expected = Math.Clamp(before[affected.Id][effect.AttributeIndex] + effect.PerMinute,
                definition.Min, definition.Max);
            Assert.Equal(expected, affected.GetComponent<AgentAttributes>().Values[effect.AttributeIndex], 3);
        }
    }

    [Fact]
    public void MutualMinimumCommitmentHoldsBeforeAllowingABetterAlternativeToReleaseBoth()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = CreateAgent(store, catalog, 1, stress: 50, preference: 1, charisma: 100, location: 3001);
        var second = CreateAgent(store, catalog, 2, stress: 50, preference: 1, charisma: 100, location: 3001);
        LinkFriends(store, catalog, first, second);
        var clock = store.CreateEntity(new WorldTime());
        var root = CreateSimulationRoot(store, catalog, clock);
        Advance(clock, 1_020);
        root.Update(default);
        Advance(clock, 1_021);
        root.Update(default);

        ForceRestNeed(first, catalog);
        Advance(clock, 1_022);
        root.Update(default);
        Assert.True(first.GetComponent<CoordinationState>().Active);
        Assert.True(second.GetComponent<CoordinationState>().Active);

        Advance(clock, 1_051);
        root.Update(default);
        Assert.False(first.GetComponent<CoordinationState>().Active);
        Assert.False(second.GetComponent<CoordinationState>().Active);
        Assert.Equal(catalog.Intents.Fallback.Hash, first.GetComponent<IntentionState>().ActionHash);
        Assert.Equal(catalog.Intents.Fallback.Hash, second.GetComponent<IntentionState>().ActionHash);
    }

    [Fact]
    public void InvitationRespectsCommitmentUnlessParticipantOfferIsUrgent()
    {
        Assert.False(RunCommittedInvitation(LoadCatalog()));
        using var urgentContent = RelationshipContent.Create("socialize");
        Assert.True(RunCommittedInvitation(ContentCatalog.Load(urgentContent.Directory)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingPartnerOrImpossibleTravelImmediatelyReleasesThePair(bool deleteInitiator)
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = CreateAgent(store, catalog, 1, stress: 50, preference: 1, charisma: 100, location: 3001);
        var second = CreateAgent(store, catalog, 2, stress: 50, preference: 1, charisma: 100, location: 3001);
        LinkFriends(store, catalog, first, second);
        var clock = store.CreateEntity(new WorldTime());
        var root = CreateSimulationRoot(store, catalog, clock);
        Advance(clock, 1_020);
        root.Update(default);

        var initiator = first.GetComponent<CoordinationState>().Role == CoordinationRole.Initiator ? first : second;
        var participant = initiator == first ? second : first;
        if (deleteInitiator)
            initiator.DeleteEntity();
        else
        {
            ref var location = ref initiator.GetComponent<AgentLocation>();
            location.CurrentLocationId = 99_999;
        }

        Advance(clock, 1_021);
        root.Update(default);
        Assert.False(participant.GetComponent<CoordinationState>().Active);
        Assert.Equal(catalog.Intents.Fallback.Hash, participant.GetComponent<IntentionState>().ActionHash);
        if (!deleteInitiator) Assert.False(initiator.GetComponent<CoordinationState>().Active);
    }

    [Fact]
    public void NetworkMutationsDirtyEveryAffectedMembersRelationshipDecisions()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var first = CreateAgent(store, catalog, 1);
        var second = CreateAgent(store, catalog, 2);
        first.GetComponent<DecisionState>().Dirty = false;
        first.GetComponent<DecisionState>().ChangedFacts = FactDependencyMask.None;
        second.GetComponent<DecisionState>().Dirty = false;
        second.GetComponent<DecisionState>().ChangedFacts = FactDependencyMask.None;
        using var service = new AgentNetworkService(store, catalog.Networks);
        var network = service.CreateNetwork(catalog.Networks.GetType("family").Hash, 3001, 0);
        var role = catalog.Networks.GetRole("family-member").Hash;

        service.AddMembership(first, network, role);
        first.GetComponent<DecisionState>().Dirty = false;
        first.GetComponent<DecisionState>().ChangedFacts = FactDependencyMask.None;
        service.AddMembership(second, network, role);

        Assert.True(first.GetComponent<DecisionState>().Dirty);
        Assert.True(second.GetComponent<DecisionState>().Dirty);
        Assert.True(first.GetComponent<DecisionState>().ChangedFacts.Intersects(
            new(FactDependencyCategory.NetworkTargets | FactDependencyCategory.Coordination)));
    }

    private static Entity CreateAgent(EntityStore store, ContentCatalog catalog, int nameId,
        float stress = 20, float preference = 0.5f, float charisma = 55, float wealth = 5_000,
        int location = 3004)
    {
        var values = catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray();
        values[catalog.AgentAttributes.GetIndex("stress")] = stress;
        values[catalog.AgentAttributes.GetIndex("preference")] = preference;
        values[catalog.AgentAttributes.GetIndex("charisma")] = charisma;
        values[catalog.AgentAttributes.GetIndex("wealth")] = wealth;
        var fallback = catalog.Intents.Fallback;
        var entity = store.CreateEntity(
            new Identity { NameId = nameId, OccupationId = 2001 },
            new PoliticalAlignment(),
            new AgentAttributes { Values = values },
            new Psychology(),
            new AgentState(),
            new IntentionState { ActionHash = fallback.Hash },
            new ActivityState { ActionHash = fallback.Hash, ActivityTypeHash = fallback.Activity.Hash, Phase = ActivityPhase.Idle },
            new DecisionState
            {
                Dirty = true,
                ChangedFacts = FactDependencyMask.All,
                LastConsideredMinute = -1,
                
                
                
                
                
                
            },
            new AgentLocation { HomeLocationId = 3001, WorkLocationId = 3004, CurrentLocationId = location },
            new AgentTravel { RouteLocationIds = new[] { 3001, 3003, 3004 } },
            Tags.Get<Tier1LodTag>());
        entity.AddComponent<CoordinationState>();
        return entity;
    }

    private static void LinkFriends(EntityStore store, ContentCatalog catalog, params Entity[] agents)
    {
        var service = new AgentNetworkService(store, catalog.Networks);
        var network = service.CreateNetwork(catalog.Networks.GetType("friend-group").Hash, 0, 0);
        var role = catalog.Networks.GetRole("friend").Hash;
        foreach (var agent in agents) service.AddMembership(agent, network, role);
        for (var first = 0; first < agents.Length; first++)
            for (var second = first + 1; second < agents.Length; second++)
            {
                store.CreateEntity(new EdgeData { Source = agents[first], Target = agents[second], Affinity = 100 });
                store.CreateEntity(new EdgeData { Source = agents[second], Target = agents[first], Affinity = 100 });
            }
    }

    private static void LinkFlatNetwork(EntityStore store, ContentCatalog catalog, string typeId, string roleId,
        params Entity[] agents)
    {
        var service = new AgentNetworkService(store, catalog.Networks);
        var network = service.CreateNetwork(catalog.Networks.GetType(typeId).Hash, 0, 0);
        var role = catalog.Networks.GetRole(roleId).Hash;
        foreach (var agent in agents) service.AddMembership(agent, network, role);
    }

    private static void LinkCompany(EntityStore store, ContentCatalog catalog, Entity manager, Entity employee)
    {
        var service = new AgentNetworkService(store, catalog.Networks);
        var network = service.CreateNetwork(catalog.Networks.GetType("company").Hash, 3004, 0);
        service.AddMembership(manager, network, catalog.Networks.GetRole("company-head").Hash);
        service.AddMembership(employee, network, catalog.Networks.GetRole("company-employee").Hash, manager);
    }

    private static void ForceRestNeed(Entity agent, ContentCatalog catalog)
    {
        ref var attributes = ref agent.GetComponent<AgentAttributes>();
        var fatigue = catalog.AgentAttributes.GetIndex("fatigue");
        var stress = catalog.AgentAttributes.GetIndex("stress");
        attributes.Values[fatigue] = 100;
        attributes.Values[stress] = 100;
        ref var decision = ref agent.GetComponent<DecisionState>();
        DecisionInvalidation.SignalAttribute(ref decision, fatigue);
        DecisionInvalidation.SignalAttribute(ref decision, stress);
    }

    private static bool RunCommittedInvitation(ContentCatalog catalog)
    {
        var store = new EntityStore();
        var initiator = CreateAgent(store, catalog, 1, stress: 50, preference: 1, charisma: 100, location: 3001);
        var participant = CreateAgent(store, catalog, 2, stress: 50, preference: 1, charisma: 100, location: 3001);
        LinkFriends(store, catalog, initiator, participant);
        var minute = 1_020L;
        ref var intention = ref participant.GetComponent<IntentionState>();
        intention.ActionHash = catalog.Intents.All.Single(intent => intent.Id == "rest").Hash;
        intention.TargetLocationId = 3001;
        intention.SelectedAtMinute = minute - 1;
        intention.Utility = 50;
        ref var decision = ref participant.GetComponent<DecisionState>();
        decision.Dirty = false;
        decision.ChangedFacts = FactDependencyMask.None;
        decision.LastConsideredMinute = minute;
        var clock = store.CreateEntity(new WorldTime());
        Advance(clock, minute);
        var root = CreateSimulationRoot(store, catalog, clock);
        root.Update(default);
        return initiator.GetComponent<CoordinationState>().Active &&
            participant.GetComponent<CoordinationState>().Active;
    }

    private static SystemRoot CreateSimulationRoot(EntityStore store, ContentCatalog catalog, Entity clock) => new(store)
    {
        new AgentDecisionSystem(store, catalog, clock),
        new CoordinationSystem(store, catalog, clock),
        new IntentExecutionSystem(store, catalog, clock),
        new ActivityEffectsSystem(catalog, clock)
    };

    private static void Advance(Entity clock, long minute)
    {
        ref var time = ref clock.GetComponent<WorldTime>();
        time.ElapsedSimulationSeconds = minute * SimulationDefaults.SimulationSecondsPerMinute;
        time.DeltaSimulationSeconds = SimulationDefaults.SimulationSecondsPerMinute;
    }

    private sealed class RelationshipContent : IDisposable
    {
        private RelationshipContent(string directory) => Directory = directory;
        public string Directory { get; }

        public static RelationshipContent Create(string selectedIntentId)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"proxystate-relationships-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            foreach (var source in System.IO.Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "data"), "*.json"))
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));

            var path = Path.Combine(directory, "actions.json");
            var actions = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            foreach (var action in actions)
            {
                var id = action!["id"]!.GetValue<string>();
                if (id == selectedIntentId)
                {
                    action["baseUtility"] = 100;
                    action["participation"]!["acceptance"]!["baseUtility"] = 100;
                }
                else if (id != "idle")
                {
                    action["eligibility"] = new JsonObject { ["op"] = "constant", ["value"] = false };
                }
            }
            File.WriteAllText(path, actions.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return new(directory);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
