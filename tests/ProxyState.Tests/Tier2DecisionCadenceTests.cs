using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class Tier2DecisionCadenceTests
{
    [Fact]
    public void OrdinaryChangesAccumulateUntilTheHourlyPass()
    {
        var fixture = new CadenceFixture();
        fixture.UpdateDecisions(0);
        var initialEvaluations = fixture.EvaluationCount;

        fixture.SignalAttribute("fatigue", 100f);
        fixture.UpdateDecisions(1);
        fixture.UpdateDecisions(59);

        Assert.Equal(initialEvaluations, fixture.EvaluationCount);
        Assert.True(fixture.Agent.GetComponent<DecisionState>().Dirty);

        fixture.UpdateDecisions(60);
        Assert.True(fixture.EvaluationCount > initialEvaluations);
        Assert.False(fixture.Agent.GetComponent<DecisionState>().Dirty);

        fixture.UpdateDecisions(180);
        var multiHourEvaluations = fixture.EvaluationCount;
        fixture.UpdateDecisions(181);
        Assert.Equal(multiHourEvaluations, fixture.EvaluationCount);
    }

    [Fact]
    public void CriticalTargetLossWakesTierTwoImmediately()
    {
        var fixture = new CadenceFixture();
        fixture.UpdateDecisions(0);
        var initialEvaluations = fixture.EvaluationCount;
        ref var state = ref fixture.Agent.GetComponent<DecisionState>();
        DecisionInvalidation.SignalTargetLoss(ref state);

        fixture.UpdateDecisions(1);

        Assert.True(fixture.EvaluationCount > initialEvaluations);
        Assert.Equal(DecisionWakeReason.None,
            fixture.Agent.GetComponent<DecisionState>().ImmediateWakeReasons);
    }

    [Fact]
    public void CoordinationLifecycleWakesTierTwoImmediately()
    {
        var fixture = new CadenceFixture();
        fixture.UpdateDecisions(0);
        var initialEvaluations = fixture.EvaluationCount;
        DecisionInvalidation.SignalCoordinationLifecycle(
            ref fixture.Agent.GetComponent<DecisionState>());

        fixture.UpdateDecisions(1);

        Assert.True(fixture.EvaluationCount > initialEvaluations);
    }

    [Fact]
    public void ActivityEffectsContinueBetweenTierTwoDecisionPasses()
    {
        var fixture = new CadenceFixture();
        fixture.UpdateDecisions(0);
        var initialEvaluations = fixture.EvaluationCount;
        var fatigueIndex = fixture.Catalog.AgentAttributes.GetIndex("fatigue");
        var before = fixture.Agent.GetComponent<AgentAttributes>().Values[fatigueIndex];

        fixture.ApplyEffects(1);

        Assert.NotEqual(before, fixture.Agent.GetComponent<AgentAttributes>().Values[fatigueIndex]);
        fixture.UpdateDecisions(1);
        Assert.Equal(initialEvaluations, fixture.EvaluationCount);
    }

    [Fact]
    public void MovementContinuesBetweenTierTwoDecisionPasses()
    {
        var fixture = new CadenceFixture();
        fixture.UpdateDecisions(0);
        var initialEvaluations = fixture.EvaluationCount;
        ref var intention = ref fixture.Agent.GetComponent<IntentionState>();
        intention.ActionHash = 1001;
        intention.TargetLocationId = 3004;

        fixture.Execute(1, 1);
        fixture.Execute(16, 15);

        Assert.Equal(3003, fixture.Agent.GetComponent<AgentLocation>().CurrentLocationId);
        Assert.Equal(initialEvaluations, fixture.EvaluationCount);
    }

    private sealed class CadenceFixture
    {
        private readonly EntityStore _store = new();
        private readonly Entity _clock;
        private readonly SystemRoot _decisions;
        private readonly SystemRoot _effects;
        private readonly SystemRoot _execution;

        public CadenceFixture()
        {
            Catalog = ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));
            _clock = _store.CreateEntity(new WorldTime());
            Agent = _store.CreateEntity(
                new Identity { NameId = 1, OccupationId = 2001 },
                new AgentAttributes
                {
                    Values = Catalog.AgentAttributes.Definitions.Select(definition => definition.Average).ToArray()
                },
                new Psychology(),
                new AgentLocation { HomeLocationId = 3001, WorkLocationId = 3004, CurrentLocationId = 3001 },
                new AgentTravel { RouteLocationIds = [3001, 3003, 3004] },
                new IntentionState { ActionHash = 1002 },
                new ActivityState { ActionHash = 1002, ActivityTypeHash = 4002, Phase = ActivityPhase.Performing },
                new DecisionState
                {
                    LastConsideredMinute = -1,
                    Dirty = true,
                    ChangedFacts = FactDependencyMask.All,
                    
                    
                },
                Tags.Get<Tier2LodTag, DetailedSimulationTag>());
            _decisions = new SystemRoot(_store) { new AgentDecisionSystem(_store, Catalog, _clock) };
            _effects = new SystemRoot(_store) { new ActivityEffectsSystem(Catalog, _clock) };
            _execution = new SystemRoot(_store) { new IntentExecutionSystem(_store, Catalog, _clock) };
        }

        public ContentCatalog Catalog { get; }
        public Entity Agent { get; }
        public long EvaluationCount => Agent.GetComponent<DecisionState>().EvaluationCount;

        public void SignalAttribute(string id, float value)
        {
            var index = Catalog.AgentAttributes.GetIndex(id);
            Agent.GetComponent<AgentAttributes>().Values[index] = value;
            DecisionInvalidation.SignalAttribute(ref Agent.GetComponent<DecisionState>(), index);
        }

        public void UpdateDecisions(long minute)
        {
            ref var time = ref _clock.GetComponent<WorldTime>();
            time.ElapsedSimulationSeconds = minute * SimulationDefaults.SimulationSecondsPerMinute;
            time.DeltaSimulationSeconds = SimulationDefaults.SimulationSecondsPerMinute;
            _decisions.Update(default);
        }

        public void ApplyEffects(long minute)
        {
            ref var time = ref _clock.GetComponent<WorldTime>();
            time.ElapsedSimulationSeconds = minute * SimulationDefaults.SimulationSecondsPerMinute;
            time.DeltaSimulationSeconds = SimulationDefaults.SimulationSecondsPerMinute;
            _effects.Update(default);
        }

        public void Execute(long minute, double deltaMinutes)
        {
            ref var time = ref _clock.GetComponent<WorldTime>();
            time.ElapsedSimulationSeconds = minute * SimulationDefaults.SimulationSecondsPerMinute;
            time.DeltaSimulationSeconds = deltaMinutes * SimulationDefaults.SimulationSecondsPerMinute;
            _execution.Update(default);
        }
    }
}
