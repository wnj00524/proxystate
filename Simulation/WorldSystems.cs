using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

public sealed class WorldClockSystem : QuerySystem<WorldTime>
{
    private readonly Entity _clockEntity;
    private double _pendingRealSeconds;

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
            : store.CreateEntity(new WorldTime());
    }

    public Entity ClockEntity => _clockEntity;

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
            (SimulationDefaults.SimulationSecondsPerDay / SimulationDefaults.RealSecondsPerSimulationDay);
        _pendingRealSeconds = 0d;

        Query.ForEachEntity((ref WorldTime time, Entity _) =>
        {
            time.DeltaSimulationSeconds = simulationSeconds;
            time.ElapsedSimulationSeconds += simulationSeconds;
        });
    }
}

public sealed class CommutingSystem : QuerySystem<AgentLocation, AgentTravel, Identity, AgentState>
{
    private readonly WorldTopology _world;
    private readonly Entity _clockEntity;
    private readonly Dictionary<int, JobDefinition> _jobsByHash;
    private readonly int _workActionHash;
    private readonly int _restActionHash;

    public CommutingSystem(ContentCatalog catalog, Entity clockEntity)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _world = catalog.World;
        _clockEntity = clockEntity;
        _jobsByHash = catalog.Jobs.ToDictionary(job => job.Hash);
        _workActionHash = catalog.Actions.First(action =>
            string.Equals(action.Id, "work", StringComparison.OrdinalIgnoreCase)).Hash;
        _restActionHash = catalog.Actions.First(action =>
            string.Equals(action.Id, "rest", StringComparison.OrdinalIgnoreCase)).Hash;
        Filter.AllTags(Tags.Get<Tier1LodTag>());
    }

    protected override void OnUpdate()
    {
        var time = _clockEntity.GetComponent<WorldTime>();
        var elapsedMinutes = time.DeltaSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute;
        if (elapsedMinutes <= 0d)
        {
            return;
        }

        var jobDay = time.DayOfWeek;
        var minuteOfDay = time.MinuteOfDay;
        Query.ForEachEntity((
            ref AgentLocation location,
            ref AgentTravel travel,
            ref Identity identity,
            ref AgentState state,
            Entity _) =>
        {
            if (!_jobsByHash.TryGetValue(identity.OccupationId, out var job))
            {
                return;
            }

            UpdateAgent(
                ref location,
                ref travel,
                ref state,
                job,
                jobDay,
                minuteOfDay,
                elapsedMinutes);
        });
    }

    private void UpdateAgent(
        ref AgentLocation location,
        ref AgentTravel travel,
        ref AgentState state,
        JobDefinition job,
        int dayOfWeek,
        int minuteOfDay,
        double elapsedMinutes)
    {
        if (travel.Mode is AgentTravelMode.TravellingToWork or AgentTravelMode.TravellingHome)
        {
            AdvanceTravel(ref location, ref travel, elapsedMinutes);
        }

        var isWorkday = job.WorkDays.Contains(dayOfWeek);
        var routeDuration = travel.TotalTravelMinutes;
        var departureMinute = job.WorkStartMinute - routeDuration;

        if (travel.Mode == AgentTravelMode.AtWork &&
            (!isWorkday || minuteOfDay >= job.WorkEndMinute))
        {
            BeginTravelHome(ref location, ref travel);
        }
        else if (travel.Mode == AgentTravelMode.AtHome && isWorkday &&
                 minuteOfDay >= departureMinute && minuteOfDay < job.WorkEndMinute)
        {
            BeginTravelToWork(ref location, ref travel);
        }

        if (travel.Mode == AgentTravelMode.AtWork)
        {
            state.CurrentActionHash = _workActionHash;
        }
        else if (travel.Mode == AgentTravelMode.AtHome)
        {
            state.CurrentActionHash = _restActionHash;
        }
    }

    private void BeginTravelToWork(ref AgentLocation location, ref AgentTravel travel)
    {
        if (travel.RouteLocationIds.Length == 1)
        {
            location.CurrentLocationId = location.WorkLocationId;
            travel.Mode = AgentTravelMode.AtWork;
            travel.RemainingTravelMinutes = 0f;
            return;
        }

        travel.Mode = AgentTravelMode.TravellingToWork;
        travel.RoutePosition = 0;
        travel.RemainingTravelMinutes = _world.GetTravelMinutes(
            travel.RouteLocationIds[0],
            travel.RouteLocationIds[1]);
    }

    private void BeginTravelHome(ref AgentLocation location, ref AgentTravel travel)
    {
        if (travel.RouteLocationIds.Length == 1)
        {
            location.CurrentLocationId = location.HomeLocationId;
            travel.Mode = AgentTravelMode.AtHome;
            travel.RemainingTravelMinutes = 0f;
            return;
        }

        travel.Mode = AgentTravelMode.TravellingHome;
        travel.RoutePosition = travel.RouteLocationIds.Length - 1;
        travel.RemainingTravelMinutes = _world.GetTravelMinutes(
            travel.RouteLocationIds[^1],
            travel.RouteLocationIds[^2]);
    }

    private void AdvanceTravel(
        ref AgentLocation location,
        ref AgentTravel travel,
        double elapsedMinutes)
    {
        while (elapsedMinutes > 0d &&
               travel.Mode is AgentTravelMode.TravellingToWork or AgentTravelMode.TravellingHome)
        {
            var nextPosition = travel.Mode == AgentTravelMode.TravellingToWork
                ? travel.RoutePosition + 1
                : travel.RoutePosition - 1;

            if (travel.RemainingTravelMinutes > elapsedMinutes)
            {
                travel.RemainingTravelMinutes -= (float)elapsedMinutes;
                break;
            }

            elapsedMinutes -= travel.RemainingTravelMinutes;
            travel.RoutePosition = nextPosition;
            location.CurrentLocationId = travel.RouteLocationIds[nextPosition];

            var arrived = travel.Mode == AgentTravelMode.TravellingToWork
                ? nextPosition == travel.RouteLocationIds.Length - 1
                : nextPosition == 0;
            if (arrived)
            {
                travel.RemainingTravelMinutes = 0f;
                travel.Mode = travel.Mode == AgentTravelMode.TravellingToWork
                    ? AgentTravelMode.AtWork
                    : AgentTravelMode.AtHome;
                continue;
            }

            var followingPosition = travel.Mode == AgentTravelMode.TravellingToWork
                ? nextPosition + 1
                : nextPosition - 1;
            travel.RemainingTravelMinutes = _world.GetTravelMinutes(
                travel.RouteLocationIds[nextPosition],
                travel.RouteLocationIds[followingPosition]);
        }
    }
}
