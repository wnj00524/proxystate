using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class DecisionBaselineTests
{
    private const int Work = 1001;
    private const int Rest = 1002;
    private const int Socialize = 1003;

    [Theory]
    [InlineData(600, Work)]
    [InlineData(1_020, Rest)]
    [InlineData(6 * 1_440 + 600, Rest)]
    public void WorkEligibilityIsLockedToTheConfiguredSchedule(int minute, int expected)
    {
        using var fixture = new DecisionFixture(minute);
        fixture.Set(fatigue: 0, stress: 0, wealth: 0);

        Assert.Equal(expected, fixture.Decide().IntentHash);
    }

    [Fact]
    public void HighFatigueMakesRestWin()
    {
        using var fixture = new DecisionFixture(600, Work, selectedAtMinute: 500);
        fixture.Set(fatigue: 100, stress: 100, wealth: 10_000);

        var trace = fixture.Decide();

        Assert.Equal(Rest, trace.IntentHash);
        Assert.Equal(95f, trace.Utility, precision: 3);
        Assert.Equal(3001, trace.TargetLocationId);
    }

    [Fact]
    public void MeetFriendsRequiresAndTargetsAFriendGroupMember()
    {
        using var withoutPeer = new DecisionFixture(1_020);
        withoutPeer.Set(preference: 1, charisma: 100, fatigue: 0, stress: 0);
        Assert.Equal(Rest, withoutPeer.Decide().IntentHash);

        using var withPeer = new DecisionFixture(1_020);
        withPeer.Set(preference: 1, charisma: 100, fatigue: 0, stress: 0);
        var peer = withPeer.AddPeer(affinity: 100);
        var trace = withPeer.Decide();

        Assert.Equal(Socialize, trace.IntentHash);
        Assert.Equal(peer.Id, trace.TargetEntityId);
        Assert.Equal(3001, trace.TargetLocationId);
        Assert.Equal(69f, trace.Utility, precision: 3);
    }

    [Fact]
    public void FriendTargetAffinityRankingAndTieBreakAreDataDefined()
    {
        using var fixture = new DecisionFixture(1_020);
        fixture.Set(preference: 1, charisma: 100, fatigue: 0, stress: 0);
        var highest = fixture.AddPeer(affinity: 100, location: 3004);
        var first = fixture.AddPeer(affinity: 50);
        var second = fixture.AddPeer(affinity: 50);

        var trace = fixture.Decide();

        Assert.Equal(Socialize, trace.IntentHash);
        Assert.Equal(highest.Id, trace.TargetEntityId);
        Assert.Equal(3004, trace.TargetLocationId);
    }

    [Fact]
    public void MinimumCommitmentBlocksANonUrgentWinner()
    {
        using var fixture = new DecisionFixture(600, Work, selectedAtMinute: 595);
        fixture.Set(fatigue: 70, stress: 90, wealth: 4_000);

        Assert.Equal(Work, fixture.Decide().IntentHash);
    }

    [Fact]
    public void SwitchingThresholdBlocksSmallImprovementsThenAllowsLargerOnes()
    {
        using var fixture = new DecisionFixture(600, Work, selectedAtMinute: 500);
        fixture.Set(fatigue: 60, stress: 90, wealth: 4_000);
        Assert.Equal(Work, fixture.Decide().IntentHash);

        fixture.AdvanceTo(601);
        fixture.Set(fatigue: 70, stress: 90, wealth: 4_000);
        Assert.Equal(Rest, fixture.Decide().IntentHash);
    }

    [Fact]
    public void UrgentWinnerPreemptsMinimumCommitment()
    {
        using var fixture = new DecisionFixture(600, Work, selectedAtMinute: 599);
        fixture.Set(fatigue: 100, stress: 100, wealth: 10_000);

        Assert.Equal(Rest, fixture.Decide().IntentHash);
    }

    [Fact]
    public void ExitedIntentRemainsUnavailableUntilItsCooldownExpires()
    {
        using var fixture = new DecisionFixture(600, Work, selectedAtMinute: 500);
        fixture.Set(fatigue: 100, stress: 100, wealth: 10_000);
        var exited = fixture.Decide();
        Assert.Equal(615, exited.Cooldowns[Work]);

        fixture.AdvanceTo(601);
        fixture.Set(fatigue: 0, stress: 0, wealth: 0);
        Assert.Equal(Rest, fixture.Decide().IntentHash);

        fixture.AdvanceTo(620);
        Assert.Equal(Work, fixture.Decide().IntentHash);
    }

    [Fact]
    public void DecisionAndExecutionTraceLocksTravelAndActivityTransitions()
    {
        using var fixture = new DecisionFixture(600);
        fixture.Set(fatigue: 0, stress: 0, wealth: 0);

        var selected = fixture.Decide(runExecution: true);

        Assert.Equal(Work, selected.IntentHash);
        Assert.Equal(3004, selected.TargetLocationId);
        Assert.Equal(AgentTravelMode.Travelling, selected.TravelMode);
        Assert.Equal(ActivityPhase.Moving, selected.ActivityPhase);
        Assert.Equal(Work, selected.ActivityActionHash);
        Assert.Equal(4001, selected.ActivityTypeHash);
    }

    [Fact]
    public void SameMinuteAttributeInvalidationOnlyRescoresDependentIntents()
    {
        using var fixture = new DecisionFixture(600);
        fixture.Set(fatigue: 0, stress: 0, wealth: 0);
        fixture.Decide();
        var fullPassCount = fixture.EvaluationCount;

        fixture.SignalAttribute("fatigue", 100);
        fixture.Decide();

        var selectiveCount = fixture.EvaluationCount - fullPassCount;
        Assert.InRange(selectiveCount, 1, 3);
    }

    [Fact]
    public void EligibilityNoLongerUsesNamedRuntimeGates()
    {
        var repository = FindRepositoryRoot();
        var runtimeFiles = Directory.GetFiles(Path.Combine(repository, "Simulation"), "*.cs");
        var source = string.Join('\n', runtimeFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("Eligibility.Gate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("workSchedule", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("homeReachable", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("availablePeer", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Action.Id.Equals(\"socialize\"", source);
        Assert.DoesNotContain("Action.Id.Equals(\"work\"", source);
        Assert.Contains("TargetResolver", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProxyState.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate ProxyState.sln.");
    }

    private sealed class DecisionFixture : IDisposable
    {
        private readonly ContentCatalog _catalog;
        private readonly EntityStore _store = new();
        private readonly Entity _clock;
        private readonly AgentDecisionSystem _decisions;
        private readonly IntentExecutionSystem _execution;
        private readonly AgentNetworkService _networkService;
        private readonly AgentSocialIndexes _indexes = new();
        private readonly Entity _friendNetwork;
        private readonly SystemRoot _decisionRoot;
        private readonly SystemRoot _executionRoot;

        public DecisionFixture(long minute, int currentAction = Rest, long selectedAtMinute = 0)
        {
            _catalog = ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));
            _clock = _store.CreateEntity(new WorldTime());
            var values = _catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray();
            Agent = _store.CreateEntity(
                new Identity { NameId = 1, OccupationId = 2001 },
                new AgentAttributes { Values = values },
                new Psychology(),
                new AgentLocation { HomeLocationId = 3001, WorkLocationId = 3004, CurrentLocationId = 3001 },
                new AgentTravel { RouteLocationIds = new[] { 3001, 3003, 3004 }, Mode = AgentTravelMode.Stationary },
                new IntentionState { ActionHash = currentAction, SelectedAtMinute = selectedAtMinute },
                new ActivityState
                {
                    ActionHash = currentAction,
                    ActivityTypeHash = _catalog.Actions.Single(action => action.Hash == currentAction).Activity.Hash,
                    Phase = ActivityPhase.Performing
                },
                new DecisionState { Dirty = true,   },
                Tags.Get<Tier1LodTag>());
            _networkService = new AgentNetworkService(_store, _catalog.Networks);
            _friendNetwork = _networkService.CreateNetwork(_catalog.Networks.GetType("friend-group").Hash, 0, 0);
            _networkService.AddMembership(Agent, _friendNetwork, _catalog.Networks.GetRole("friend").Hash);
            _decisions = new AgentDecisionSystem(_store, _catalog, _clock, socialIndexes: _indexes);
            _execution = new IntentExecutionSystem(_store, _catalog, _clock);
            _decisionRoot = new SystemRoot(_store) { _decisions };
            _executionRoot = new SystemRoot(_store) { _execution };
            AdvanceTo(minute);
        }

        public Entity Agent { get; }
        public long EvaluationCount => Agent.GetComponent<DecisionState>().EvaluationCount;

        public void SignalAttribute(string id, float value)
        {
            var index = _catalog.AgentAttributes.GetIndex(id);
            Agent.GetComponent<AgentAttributes>().Values[index] = value;
            ref var decision = ref Agent.GetComponent<DecisionState>();
            DecisionInvalidation.SignalAttribute(ref decision, index);
        }

        public void Set(float? fatigue = null, float? stress = null, float? wealth = null,
            float? preference = null, float? charisma = null)
        {
            var values = Agent.GetComponent<AgentAttributes>().Values;
            SetValue("fatigue", fatigue); SetValue("stress", stress); SetValue("wealth", wealth);
            SetValue("preference", preference); SetValue("charisma", charisma);
            Agent.GetComponent<DecisionState>().Dirty = true;
            void SetValue(string id, float? value)
            {
                if (value.HasValue) values[_catalog.AgentAttributes.GetIndex(id)] = value.Value;
            }
        }

        public Entity AddPeer(float affinity, int location = 3001)
        {
            var peer = _store.CreateEntity(
                new Identity { NameId = 2, OccupationId = 2001 },
                new AgentAttributes { Values = _catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray() },
                new Psychology(),
                new AgentLocation { HomeLocationId = 3001, WorkLocationId = 3004, CurrentLocationId = location },
                new AgentTravel { RouteLocationIds = Array.Empty<int>() });
            _networkService.AddMembership(peer, _friendNetwork, _catalog.Networks.GetRole("friend").Hash);
            _store.CreateEntity(new EdgeData { Source = Agent, Target = peer, Affinity = affinity });
            Agent.GetComponent<DecisionState>().Dirty = true;
            return peer;
        }

        public void AdvanceTo(long minute)
        {
            ref var time = ref _clock.GetComponent<WorldTime>();
            time.ElapsedSimulationSeconds = minute * SimulationDefaults.SimulationSecondsPerMinute;
            time.DeltaSimulationSeconds = SimulationDefaults.SimulationSecondsPerMinute;
            Agent.GetComponent<DecisionState>().Dirty = true;
        }

        public DecisionTrace Decide(bool runExecution = false)
        {
            // This fixture mutates its tiny population between decisions; the
            // production bootstrap instead builds the same snapshot once.
            _indexes.Rebuild(_store);
            _decisionRoot.Update(default);
            if (runExecution) _executionRoot.Update(default);
            return DecisionTrace.Capture(Agent);
        }

        public void Dispose()
        {
            // Friflo stores and system roots are managed objects and expose no
            // disposal contract; the fixture exists only to scope test state.
        }
    }
}
