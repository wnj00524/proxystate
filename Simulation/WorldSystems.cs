using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

public sealed class WorldClockSystem : QuerySystem<WorldTime>
{
    private static readonly double[] RealSecondsPerDayBySpeed =
        { SimulationDefaults.RealSecondsPerSimulationDay, 300d, 60d, 30d };
    private readonly Entity _clockEntity;
    private double _pendingRealSeconds;
    private int _speed = 1;

    public WorldClockSystem(EntityStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var clocks = store.Query<WorldTime>().Entities;
        if (clocks.Count > 1)
        {
            throw new InvalidOperationException("The world must contain exactly one WorldTime singleton.");
        }

        _clockEntity = clocks.Count == 1
            ? clocks.First()
            : store.CreateEntity(new WorldTime
            {
                ElapsedSimulationSeconds = SimulationDefaults.StartingMinuteOfDay *
                    SimulationDefaults.SimulationSecondsPerMinute
            });
    }

    public Entity ClockEntity => _clockEntity;

    /// <summary>Selected interactive simulation speed, from 1 (default) to 4.</summary>
    public int Speed
    {
        get => _speed;
        set
        {
            if (value is < 1 or > 4)
                throw new ArgumentOutOfRangeException(nameof(value), "Simulation speed must be between 1 and 4.");
            _speed = value;
        }
    }

    public void Advance(double realElapsedSeconds)
    {
        if (!double.IsFinite(realElapsedSeconds) || realElapsedSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(realElapsedSeconds), "Elapsed time must be finite and non-negative.");
        }

        _pendingRealSeconds += realElapsedSeconds;
    }

    protected override void OnUpdate()
    {
        var simulationSeconds = _pendingRealSeconds *
            (SimulationDefaults.SimulationSecondsPerDay / RealSecondsPerDayBySpeed[_speed - 1]);
        _pendingRealSeconds = 0d;

        Query.ForEachEntity((ref WorldTime time, Entity _) =>
        {
            time.DeltaSimulationSeconds = simulationSeconds;
            time.ElapsedSimulationSeconds += simulationSeconds;
        });
    }
}

// Intent execution owns generic movement and performance mechanics. It never
// identifies an action by name or hash; executor definitions supply all semantics.
public sealed class IntentExecutionSystem : QuerySystem<AgentLocation, AgentTravel, IntentionState, ActivityState, DecisionState>
{
    private readonly WorldTopology _world;
    private readonly Entity _clockEntity;
    private readonly AgentSocialIndexes _socialIndexes;
    private readonly Dictionary<int, ExecutorKind> _executors;
    private readonly Dictionary<int, int> _activityTypes;

    public IntentExecutionSystem(EntityStore store, ContentCatalog catalog, Entity clockEntity,
        AgentSocialIndexes? socialIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        _world = catalog.World;
        _clockEntity = clockEntity;
        _socialIndexes = socialIndexes ?? BuildIndexes(store);
        _executors = catalog.Intents.All.ToDictionary(intent => intent.Hash, intent => intent.Executor);
        _activityTypes = catalog.Intents.All.ToDictionary(intent => intent.Hash, intent => intent.Activity.Hash);
        Filter.AnyTags(Tags.Get<Tier1LodTag, Tier2LodTag>());
    }

    protected override void OnUpdate()
    {
        var time = _clockEntity.GetComponent<WorldTime>();
        var elapsedMinutes = time.DeltaSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute;
        if (elapsedMinutes <= 0d) return;

        var minute = (long)Math.Floor(time.ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute);

        Query.ForEachEntity((ref AgentLocation location, ref AgentTravel travel,
            ref IntentionState intention, ref ActivityState activity,
            ref DecisionState decision, Entity entity) =>
        {
            if (entity.TryGetComponent<PoliticalParticipation>(out var political) &&
                political.TripKind != PoliticalTripKind.None) return;
            if (!_executors.TryGetValue(intention.ActionHash, out var executor))
            {
                DecisionInvalidation.SignalTargetLoss(ref decision);
                SetActivity(ref activity, ActivityPhase.Blocked, 0, 0, minute);
                return;
            }

            if (entity.HasComponent<CoordinationState>())
            {
                ref var coordination = ref entity.GetComponent<CoordinationState>();
                if (coordination.Active && coordination.Role == CoordinationRole.Participant)
                {
                    CancelTravel(ref travel);
                    SetActivity(ref activity,
                        coordination.Status == CoordinationStatus.Performing
                            ? ActivityPhase.Performing : ActivityPhase.Waiting,
                        intention.ActionHash, _activityTypes[intention.ActionHash], minute);
                    return;
                }
            }

            var destination = ResolveDestination(executor, intention, location, ref decision);
            if (destination is null)
            {
                CancelTravel(ref travel);
                SetActivity(ref activity, ActivityPhase.Blocked, intention.ActionHash,
                    _activityTypes[intention.ActionHash], minute);
                return;
            }

            if (travel.Mode == AgentTravelMode.Travelling)
            {
                // Moving entity targets can invalidate the old route. Rebuild it
                // deterministically from the actor's current graph node.
                if (travel.DestinationLocationId != destination.Value)
                    BeginTravel(location.CurrentLocationId, destination.Value, ref travel, ref decision);
                if (travel.Mode == AgentTravelMode.Travelling &&
                    AdvanceTravel(ref location, ref travel, elapsedMinutes))
                    DecisionInvalidation.SignalLocation(ref decision);
            }

            if (location.CurrentLocationId != destination.Value)
            {
                if (travel.Mode != AgentTravelMode.Travelling)
                    BeginTravel(location.CurrentLocationId, destination.Value, ref travel, ref decision);
                SetActivity(ref activity, travel.Mode == AgentTravelMode.Travelling
                    ? ActivityPhase.Moving : ActivityPhase.Blocked, intention.ActionHash,
                    _activityTypes[intention.ActionHash], minute);
                return;
            }

            CancelTravel(ref travel);
            var phase = executor == ExecutorKind.Wait ? ActivityPhase.Idle : ActivityPhase.Performing;
            if (entity.HasComponent<CoordinationState>())
            {
                var coordination = entity.GetComponent<CoordinationState>();
                if (coordination.Active && coordination.Status != CoordinationStatus.Performing)
                    phase = ActivityPhase.Waiting;
            }
            SetActivity(ref activity, phase, intention.ActionHash, _activityTypes[intention.ActionHash], minute);
        });
    }

    private int? ResolveDestination(ExecutorKind executor, IntentionState intention,
        AgentLocation location, ref DecisionState decision)
    {
        switch (executor)
        {
            case ExecutorKind.PerformHere:
            case ExecutorKind.Wait:
                return location.CurrentLocationId;
            case ExecutorKind.PerformAtLocation:
                if (intention.TargetLocationId != 0) return intention.TargetLocationId;
                DecisionInvalidation.SignalTargetLoss(ref decision);
                return null;
            case ExecutorKind.PerformWithEntity:
                if (intention.TargetEntityId != 0 &&
                    _socialIndexes.TryGetAgent(intention.TargetEntityId, out var target) &&
                    !target.IsNull && target.TryGetComponent<AgentLocation>(out var targetLocation))
                    return targetLocation.CurrentLocationId;
                DecisionInvalidation.SignalTargetLoss(ref decision);
                return null;
            default:
                DecisionInvalidation.SignalTargetLoss(ref decision);
                return null;
        }
    }

    private static AgentSocialIndexes BuildIndexes(EntityStore store)
    {
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        return indexes;
    }

    private void BeginTravel(int start, int destination, ref AgentTravel travel, ref DecisionState decision)
    {
        var route = _world.FindShortestRoute(start, destination);
        if (route is null)
        {
            DecisionInvalidation.SignalTargetLoss(ref decision);
            CancelTravel(ref travel);
            return;
        }
        travel.RouteLocationIds = route.LocationIds.ToArray();
        travel.TotalTravelMinutes = route.TravelMinutes;
        travel.RoutePosition = 0;
        travel.DestinationLocationId = destination;
        if (travel.RouteLocationIds.Length == 1)
        {
            travel.Mode = AgentTravelMode.Stationary;
            travel.RemainingTravelMinutes = 0f;
            return;
        }
        travel.Mode = AgentTravelMode.Travelling;
        travel.RemainingTravelMinutes = _world.GetTravelMinutes(travel.RouteLocationIds[0], travel.RouteLocationIds[1]);
    }

    private bool AdvanceTravel(ref AgentLocation location, ref AgentTravel travel, double elapsedMinutes)
    {
        var locationChanged = false;
        while (elapsedMinutes > 0d && travel.Mode == AgentTravelMode.Travelling)
        {
            if (travel.RemainingTravelMinutes > elapsedMinutes)
            {
                travel.RemainingTravelMinutes -= (float)elapsedMinutes;
                return locationChanged;
            }
            elapsedMinutes -= travel.RemainingTravelMinutes;
            travel.RoutePosition++;
            location.CurrentLocationId = travel.RouteLocationIds[travel.RoutePosition];
            locationChanged = true;
            if (travel.RoutePosition == travel.RouteLocationIds.Length - 1)
            {
                CancelTravel(ref travel);
                return locationChanged;
            }
            travel.RemainingTravelMinutes = _world.GetTravelMinutes(
                travel.RouteLocationIds[travel.RoutePosition], travel.RouteLocationIds[travel.RoutePosition + 1]);
        }
        return locationChanged;
    }

    private static void CancelTravel(ref AgentTravel travel)
    {
        travel.Mode = AgentTravelMode.Stationary;
        travel.RemainingTravelMinutes = 0f;
        travel.DestinationLocationId = 0;
    }

    private static void SetActivity(ref ActivityState activity, ActivityPhase phase,
        int actionHash, int activityTypeHash, long minute)
    {
        if (activity.Phase == phase && activity.ActionHash == actionHash &&
            activity.ActivityTypeHash == activityTypeHash) return;
        activity.Phase = phase;
        activity.ActionHash = actionHash;
        activity.ActivityTypeHash = activityTypeHash;
        activity.StartedAtMinute = minute;
    }
}
