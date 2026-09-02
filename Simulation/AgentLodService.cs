using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

/// <summary>A sanitized notification suitable for copying across the intelligence boundary.</summary>
public readonly record struct InvestigationChangedEvent(int AgentId, bool Enabled);

// This is the sole mutation boundary for tier tags and AgentLodState. It owns
// the POI frontier so overlapping relationships can be reference-counted.
public sealed class AgentLodService : IDisposable
{
    private readonly AgentLodSettings _settings;
    private readonly EntityStore? _store;
    private readonly AgentSocialIndexes? _indexes;
    private readonly Dictionary<int, Entity> _agents = new();
    private readonly HashSet<int> _pointsOfInterest = [];
    private readonly Dictionary<int, int[]> _poiNeighbours = new();
    private readonly List<InvestigationChangedEvent> _investigationEvents = [];
    private readonly Dictionary<int, int> _interactionPins = [];
    private ContentCatalog? _catalog;
    private CoarseRoutineSystem? _coarse;
    private bool _initialized;

    public long CoarseAgentVisits => _coarse?.AgentVisits ?? 0;

    // Retained for isolated contract tests and bootstrap construction. Runtime
    // classification requires the store/index overload below.
    public AgentLodService(AgentLodSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public AgentLodService(EntityStore store, AgentLodSettings settings, AgentSocialIndexes indexes)
        : this(settings)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _indexes = indexes ?? throw new ArgumentNullException(nameof(indexes));
        _store.OnEntityDelete += HandleEntityDelete;
    }

    /// <summary>Connects LOD transitions to the shared Tier 3 routine store.</summary>
    public void ConfigureCoarseRuntime(ContentCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _coarse = new CoarseRoutineSystem(catalog);
    }

    /// <summary>Advances the scheduled coarse shard before detailed systems run.</summary>
    public void UpdateCoarse(long currentMinute)
    {
        _coarse?.UpdateHour(currentMinute);
        ProcessScheduledDemotions();
    }

    /// <summary>Classifies the completed population after all relationship indexes exist.</summary>
    public void InitializeClassification()
    {
        RequireRuntime();
        if (_initialized) throw new InvalidOperationException("Agent LOD classification is already initialized.");

        foreach (var entity in _store!.Query<Identity>().Entities.OrderBy(entity => entity.Id))
        {
            _agents.Add(entity.Id, entity);
            InitializeTierOne(entity, entity.Tags.Has<OperativeTag>()
                ? AgentInterestReason.Operative
                : AgentInterestReason.None);
        }

        foreach (var agent in _agents.Values.Where(agent => agent.Tags.Has<OperativeTag>()).OrderBy(agent => agent.Id))
            AddPointOfInterest(agent);

        foreach (var agent in _agents.Values.OrderBy(agent => agent.Id)) ApplyClassification(agent);
        _initialized = true;
    }

    /// <summary>Idempotently changes investigation interest for a live agent ID.</summary>
    public bool SetInvestigation(int agentId, bool enabled)
    {
        RequireInitialized();
        if (!_agents.TryGetValue(agentId, out var agent) || !IsLiveAgent(agent))
            throw new ArgumentOutOfRangeException(nameof(agentId), agentId, "The agent ID does not identify a live agent.");

        ref var state = ref agent.GetComponent<AgentLodState>();
        var currentlyEnabled = (state.InterestReasons & AgentInterestReason.Investigation) != 0;
        if (currentlyEnabled == enabled) return false;

        if (enabled)
        {
            state.InterestReasons |= AgentInterestReason.Investigation;
            if (!_pointsOfInterest.Contains(agentId)) AddPointOfInterest(agent);
        }
        else
        {
            state.InterestReasons &= ~AgentInterestReason.Investigation;
            if (!agent.Tags.Has<OperativeTag>()) RemovePointOfInterest(agent);
        }

        ApplyClassification(agent);
        if (agent.HasComponent<DecisionState>())
            DecisionInvalidation.SignalCritical(ref agent.GetComponent<DecisionState>(), FactDependencyMask.All,
                DecisionWakeReason.Investigation);
        _investigationEvents.Add(new InvestigationChangedEvent(agentId, enabled));
        return true;
    }

    /// <summary>Returns immutable value copies and clears the pending event buffer.</summary>
    public InvestigationChangedEvent[] DrainInvestigationEvents()
    {
        var result = _investigationEvents.ToArray();
        _investigationEvents.Clear();
        return result;
    }

    public void InitializeTierOne(Entity entity, AgentInterestReason reasons = AgentInterestReason.None)
    {
        if (!entity.HasComponent<AgentLodState>())
        {
            entity.AddComponent(new AgentLodState
            {
                DesiredTier = AgentLodTier.Tier1,
                InterestReasons = reasons,
                ScheduledDemotionMinute = -1,
                CoarseProfileId = 0,
                LastCoarseSimulatedMinute = -1
            });
        }
        else
        {
            ref var state = ref entity.GetComponent<AgentLodState>();
            state.InterestReasons = reasons;
            state.DirectPoiReferenceCount = 0;
        }
        SynchronizeTags(entity, AgentLodTier.Tier1);
    }

    public void SetDesiredTier(Entity entity, AgentLodTier desiredTier)
    {
        if (!entity.HasComponent<AgentLodState>())
            throw new InvalidOperationException("Agent LOD state must be initialized before changing tier.");
        ref var state = ref entity.GetComponent<AgentLodState>();
        var previousDesiredTier = state.DesiredTier;
        var previousMaterializedTier = entity.Tags.Has<Tier1LodTag>() ? AgentLodTier.Tier1
            : entity.Tags.Has<Tier2LodTag>() ? AgentLodTier.Tier2 : AgentLodTier.Tier3;
        state.DesiredTier = desiredTier;
        var requestedMaterializedTier = desiredTier == AgentLodTier.Tier3 && !_settings.Tier3Enabled
            ? AgentLodTier.Tier2 : desiredTier;
        // Pins constrain materialization, not classification: DesiredTier remains
        // useful diagnostics while a coordinated activity temporarily needs detail.
        var materializedTier = _interactionPins.ContainsKey(entity.Id) && requestedMaterializedTier > AgentLodTier.Tier2
            ? AgentLodTier.Tier2 : requestedMaterializedTier;
        if (!_initialized)
        {
            state.ScheduledDemotionMinute = -1;
            SynchronizeTags(entity, materializedTier);
            if (materializedTier < previousMaterializedTier && entity.HasComponent<DecisionState>())
                DecisionInvalidation.SignalCritical(ref entity.GetComponent<DecisionState>(), FactDependencyMask.All,
                    DecisionWakeReason.Promotion);
            return;
        }
        if (desiredTier < previousDesiredTier || materializedTier < previousMaterializedTier)
        {
            state.ScheduledDemotionMinute = -1;
            SynchronizeTags(entity, materializedTier);
        }
        else if (desiredTier > previousDesiredTier || materializedTier > previousMaterializedTier)
        {
            if (_interactionPins.ContainsKey(entity.Id)) return;
            var boundary = NextDayBoundaryMinute(CurrentMinute());
            if (state.ScheduledDemotionMinute < 0 || boundary < state.ScheduledDemotionMinute)
                state.ScheduledDemotionMinute = boundary;
        }
        else
        {
            // An equivalent request must not push an already queued reduction
            // to a later day boundary.
            if (state.ScheduledDemotionMinute < 0) SynchronizeTags(entity, materializedTier);
        }
        if (materializedTier < previousMaterializedTier && entity.HasComponent<DecisionState>())
            DecisionInvalidation.SignalCritical(ref entity.GetComponent<DecisionState>(), FactDependencyMask.All,
                DecisionWakeReason.Promotion);
    }

    /// <summary>Keeps an active coordination participant at least Tier 2.</summary>
    public void AcquireInteractionPin(Entity entity)
    {
        RequireManagedAgent(entity);
        _interactionPins[entity.Id] = _interactionPins.GetValueOrDefault(entity.Id) + 1;
        ref var state = ref entity.GetComponent<AgentLodState>();
        state.InterestReasons |= AgentInterestReason.ActiveInteraction;
        state.ScheduledDemotionMinute = -1;
        if (entity.Tags.Has<Tier3LodTag>()) SynchronizeTags(entity, AgentLodTier.Tier2);
    }

    /// <summary>Releases one owner pin and starts normal grace after the final release.</summary>
    public void ReleaseInteractionPin(Entity entity)
    {
        RequireManagedAgent(entity);
        if (!_interactionPins.TryGetValue(entity.Id, out var count)) return;
        if (count > 1) { _interactionPins[entity.Id] = count - 1; return; }
        _interactionPins.Remove(entity.Id);
        ref var state = ref entity.GetComponent<AgentLodState>();
        state.InterestReasons &= ~AgentInterestReason.ActiveInteraction;
        ApplyClassification(entity);
    }

    /// <summary>Applies due reductions. Call before detailed decision systems.</summary>
    public void ProcessScheduledDemotions()
    {
        RequireInitialized();
        var minute = CurrentMinute();
        foreach (var agent in _agents.Values.OrderBy(agent => agent.Id))
        {
            if (!IsLiveAgent(agent)) continue;
            ref var state = ref agent.GetComponent<AgentLodState>();
            if (state.ScheduledDemotionMinute < 0 || minute < state.ScheduledDemotionMinute ||
                _interactionPins.ContainsKey(agent.Id)) continue;
            state.ScheduledDemotionMinute = -1;
            var tier = state.DesiredTier == AgentLodTier.Tier3 && !_settings.Tier3Enabled
                ? AgentLodTier.Tier2 : state.DesiredTier;
            SynchronizeTags(agent, tier);
        }
    }

    /// <summary>Refreshes POI edges touched by a supported hierarchy mutation.</summary>
    public void NotifyNetworkMutation(params ReadOnlySpan<Entity> affectedAgents)
    {
        if (!_initialized) return;
        var affected = new HashSet<int>();
        foreach (var agent in affectedAgents)
        {
            if (IsLiveAgent(agent)) affected.Add(agent.Id);
        }
        foreach (var poiId in _pointsOfInterest.Order().ToArray())
        {
            if (!_agents.TryGetValue(poiId, out var poi) || !IsLiveAgent(poi)) continue;
            var old = _poiNeighbours[poiId];
            if (!affected.Contains(poiId) && !old.Any(affected.Contains)) continue;
            var current = CollectDirectNeighbours(poi).Where(id => id != poiId).Order().ToArray();
            foreach (var removed in old.Except(current)) ChangePoiReference(removed, -1);
            foreach (var added in current.Except(old)) ChangePoiReference(added, 1);
            _poiNeighbours[poiId] = current;
        }
    }

    public static bool HasExactlyOneTierTag(Entity entity)
    {
        var count = entity.Tags.Has<Tier1LodTag>() ? 1 : 0;
        count += entity.Tags.Has<Tier2LodTag>() ? 1 : 0;
        count += entity.Tags.Has<Tier3LodTag>() ? 1 : 0;
        return count == 1;
    }

    public static bool RequiresDetailedSimulation(AgentLodTier tier) =>
        tier is AgentLodTier.Tier1 or AgentLodTier.Tier2;

    public void Dispose()
    {
        if (_store is not null) _store.OnEntityDelete -= HandleEntityDelete;
    }

    private void AddPointOfInterest(Entity poi)
    {
        if (!_pointsOfInterest.Add(poi.Id)) return;
        var neighbours = CollectDirectNeighbours(poi).Where(id => id != poi.Id).Order().ToArray();
        _poiNeighbours.Add(poi.Id, neighbours);
        foreach (var neighbourId in neighbours)
        {
            if (!_agents.TryGetValue(neighbourId, out var neighbour) || !IsLiveAgent(neighbour)) continue;
            ref var state = ref neighbour.GetComponent<AgentLodState>();
            state.DirectPoiReferenceCount++;
            state.InterestReasons |= AgentInterestReason.RelatedPointOfInterest;
            ApplyClassification(neighbour);
        }
    }

    private void RemovePointOfInterest(Entity poi)
    {
        if (!_pointsOfInterest.Remove(poi.Id) || !_poiNeighbours.Remove(poi.Id, out var neighbours)) return;
        foreach (var neighbourId in neighbours)
        {
            if (!_agents.TryGetValue(neighbourId, out var neighbour) || !IsLiveAgent(neighbour)) continue;
            ref var state = ref neighbour.GetComponent<AgentLodState>();
            state.DirectPoiReferenceCount = Math.Max(0, state.DirectPoiReferenceCount - 1);
            if (state.DirectPoiReferenceCount == 0)
                state.InterestReasons &= ~AgentInterestReason.RelatedPointOfInterest;
            ApplyClassification(neighbour);
        }
    }

    private HashSet<int> CollectDirectNeighbours(Entity poi)
    {
        var result = new HashSet<int>();
        if (_settings.RelatedBy.Contains(AgentRelationKind.Social))
            foreach (var edge in _indexes!.GetOutgoingEdges(poi.Id)) result.Add(edge.TargetAgentId);

        foreach (var membership in poi.GetRelations<AgentNetworkMembership>())
        {
            if (_settings.RelatedBy.Contains(AgentRelationKind.NetworkSupervisor) && !membership.Supervisor.IsNull)
                result.Add(membership.Supervisor.Id);
            if (!_settings.RelatedBy.Contains(AgentRelationKind.NetworkDirectReport) || membership.Network.IsNull) continue;
            foreach (var link in membership.Network.GetIncomingLinks<AgentNetworkMembership>())
                if (link.Entity.TryGetRelation<AgentNetworkMembership, Entity>(membership.Network, out var candidate) &&
                    candidate.Supervisor == poi) result.Add(link.Entity.Id);
        }
        return result;
    }

    private void ApplyClassification(Entity agent)
    {
        ref var state = ref agent.GetComponent<AgentLodState>();
        var isPoi = (state.InterestReasons & (AgentInterestReason.Operative | AgentInterestReason.Investigation)) != 0;
        SetDesiredTier(agent, isPoi ? AgentLodTier.Tier1
            : state.DirectPoiReferenceCount > 0 ? AgentLodTier.Tier2 : AgentLodTier.Tier3);
    }

    private void HandleEntityDelete(EntityDelete deletion)
    {
        if (!_initialized || !deletion.Entity.HasComponent<Identity>()) return;
        var agent = deletion.Entity;
        if (_pointsOfInterest.Contains(agent.Id)) RemovePointOfInterest(agent);
        _agents.Remove(agent.Id);
        _interactionPins.Remove(agent.Id);
        if ((agent.GetComponent<AgentLodState>().InterestReasons & AgentInterestReason.Investigation) != 0)
            _investigationEvents.Add(new InvestigationChangedEvent(agent.Id, false));
    }

    private void ChangePoiReference(int agentId, int delta)
    {
        if (!_agents.TryGetValue(agentId, out var agent) || !IsLiveAgent(agent)) return;
        ref var state = ref agent.GetComponent<AgentLodState>();
        state.DirectPoiReferenceCount = Math.Max(0, state.DirectPoiReferenceCount + delta);
        if (state.DirectPoiReferenceCount > 0) state.InterestReasons |= AgentInterestReason.RelatedPointOfInterest;
        else state.InterestReasons &= ~AgentInterestReason.RelatedPointOfInterest;
        ApplyClassification(agent);
    }

    private long CurrentMinute()
    {
        if (_store is null) return 0;
        var clocks = _store.Query<WorldTime>().Entities;
        return clocks.Count == 1
            ? (long)Math.Floor(clocks.First().GetComponent<WorldTime>().ElapsedSimulationSeconds /
                SimulationDefaults.SimulationSecondsPerMinute)
            : 0;
    }

    public static long NextDayBoundaryMinute(long minute) => checked((minute / 1440 + 1) * 1440);

    private void RequireManagedAgent(Entity entity)
    {
        RequireInitialized();
        if (!_agents.TryGetValue(entity.Id, out var managed) || managed != entity || !IsLiveAgent(entity))
            throw new ArgumentException("The entity is not a managed live agent.", nameof(entity));
    }

    private bool IsLiveAgent(Entity entity) => !entity.IsNull && entity.Store == _store && entity.HasComponent<Identity>();
    private void RequireRuntime()
    {
        if (_store is null || _indexes is null)
            throw new InvalidOperationException("Runtime classification requires an entity store and social indexes.");
    }
    private void RequireInitialized()
    {
        RequireRuntime();
        if (!_initialized) throw new InvalidOperationException("Agent LOD classification has not been initialized.");
    }

    private void SynchronizeTags(Entity entity, AgentLodTier tier)
    {
        MaterializeRepresentation(entity, tier);
        entity.RemoveTag<Tier1LodTag>(); entity.RemoveTag<Tier2LodTag>();
        entity.RemoveTag<Tier3LodTag>(); entity.RemoveTag<DetailedSimulationTag>();
        switch (tier)
        {
            case AgentLodTier.Tier1: entity.AddTag<Tier1LodTag>(); break;
            case AgentLodTier.Tier2: entity.AddTag<Tier2LodTag>(); break;
            case AgentLodTier.Tier3: entity.AddTag<Tier3LodTag>(); break;
            default: throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown agent LOD tier.");
        }
        if (RequiresDetailedSimulation(tier)) entity.AddTag<DetailedSimulationTag>();
    }

    private void MaterializeRepresentation(Entity entity, AgentLodTier tier)
    {
        if (_catalog is null || _coarse is null || !entity.HasComponent<Identity>()) return;
        if (tier == AgentLodTier.Tier3)
        {
            _coarse.Add(entity, CurrentMinute());
            if (entity.HasComponent<IntentionState>()) entity.RemoveComponent<IntentionState>();
            if (entity.HasComponent<ActivityState>()) entity.RemoveComponent<ActivityState>();
            if (entity.HasComponent<DecisionState>()) entity.RemoveComponent<DecisionState>();
            if (entity.HasComponent<CoordinationState>()) entity.RemoveComponent<CoordinationState>();
            if (entity.HasComponent<AgentTravel>()) entity.RemoveComponent<AgentTravel>();
            return;
        }
        if (!entity.Tags.Has<Tier3LodTag>()) return;
        _coarse.CatchUp(entity, CurrentMinute());
        _coarse.Remove(entity);
        var fallback = _catalog.Intents.Fallback;
        if (!entity.HasComponent<IntentionState>()) entity.AddComponent(new IntentionState { ActionHash = fallback.Hash });
        if (!entity.HasComponent<ActivityState>()) entity.AddComponent(new ActivityState { ActionHash = fallback.Hash, ActivityTypeHash = fallback.Activity.Hash, Phase = ActivityPhase.Performing });
        if (!entity.HasComponent<DecisionState>()) entity.AddComponent(CreateDecisionState());
        if (!entity.HasComponent<CoordinationState>()) entity.AddComponent<CoordinationState>();
        if (!entity.HasComponent<AgentTravel>())
        {
            var location = entity.GetComponent<AgentLocation>();
            var route = _catalog.World.FindShortestRoute(location.HomeLocationId, location.WorkLocationId) ?? throw new InvalidOperationException("A promoted agent has no route.");
            entity.AddComponent(new AgentTravel { RouteLocationIds = route.LocationIds.ToArray(), TotalTravelMinutes = route.TravelMinutes });
        }
    }

    private static DecisionState CreateDecisionState() => new()
    {
        Dirty = true,
        ChangedFacts = FactDependencyMask.All
    };
}
