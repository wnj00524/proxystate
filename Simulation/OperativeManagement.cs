using Friflo.Engine.ECS;
using System.Text.Json;

namespace ProxyState.Simulation;

/// <summary>Player-authored recurring work hours for an operative (Monday is bit zero).</summary>
public struct OperativeWorkSchedule : IComponent
{
    public byte WorkDaysMask;
    public int WorkStartMinute;
    public int WorkEndMinute;
}

public enum OperativeTaskKind : byte { None, Follow, Talk }

/// <summary>Compact active assignment state retained on the operative.</summary>
public struct OperativeAssignment : IComponent
{
    public OperativeTaskKind Kind;
    public int TargetAgentId;
    public long StartedAtMinute;
    public long EndsAtMinute;
}

public readonly record struct OperativeTaskCommand(
    int OperativeId, OperativeTaskKind Kind, int TargetAgentId, int DurationMinutes = 0);
public readonly record struct OperativeRecallCommand(int OperativeId);
public readonly record struct OperativeRotaCommand(
    int OperativeId, byte WorkDaysMask, int WorkStartMinute, int WorkEndMinute);
public enum OperativeCommandKind : byte { SetRota, Assign, Recall }
public readonly record struct OperativeCommand(
    OperativeCommandKind Kind, int OperativeId, byte WorkDaysMask = 0,
    int WorkStartMinute = 0, int WorkEndMinute = 0, OperativeTaskKind TaskKind = OperativeTaskKind.None,
    int TargetAgentId = 0, int DurationMinutes = 0);

/// <summary>UI command queue processed before the next simulation update.</summary>
public sealed class OperativeCommandQueue
{
    private readonly Queue<OperativeCommand> _commands = new();
    public void Enqueue(OperativeCommand command) => _commands.Enqueue(command);
    public int Process(OperativeManagementSystem system, long minute)
    {
        var accepted = 0;
        while (_commands.TryDequeue(out var command))
        {
            var success = command.Kind switch
            {
                OperativeCommandKind.SetRota => system.SetRota(new(command.OperativeId, command.WorkDaysMask,
                    command.WorkStartMinute, command.WorkEndMinute), minute),
                OperativeCommandKind.Assign => system.Assign(new(command.OperativeId, command.TaskKind,
                    command.TargetAgentId, command.DurationMinutes), minute),
                OperativeCommandKind.Recall => system.Recall(new(command.OperativeId), minute),
                _ => false
            };
            if (success) accepted++;
        }
        return accepted;
    }
}

public readonly record struct OperativeSnapshot(
    int AgentId, string DisplayName, string Role, string Occupation, byte WorkDaysMask,
    int WorkStartMinute, int WorkEndMinute, OperativeTaskKind TaskKind,
    int TargetAgentId, long TaskEndMinute);

public readonly record struct IntelligenceEvidence(
    long Minute, int SourceOperativeId, int SubjectAgentId, string Kind, string Detail);

public sealed class IntelligenceAssessment
{
    public IntelligenceAssessment(long minute, int sourceOperativeId, int subjectAgentId,
        string summary, float confidence, IEnumerable<IntelligenceEvidence> evidence)
    {
        Minute = minute;
        SourceOperativeId = sourceOperativeId;
        SubjectAgentId = subjectAgentId;
        Summary = summary;
        Confidence = confidence;
        Evidence = Array.AsReadOnly(evidence.ToArray());
    }

    public long Minute { get; }
    public int SourceOperativeId { get; }
    public int SubjectAgentId { get; }
    public string Summary { get; }
    public float Confidence { get; }
    public IReadOnlyList<IntelligenceEvidence> Evidence { get; }
}

public sealed class OperativeManagementProjection
{
    public OperativeManagementProjection(IEnumerable<OperativeSnapshot> operatives,
        IEnumerable<IntelligenceAssessment> reports)
    {
        Operatives = Array.AsReadOnly(operatives.ToArray());
        Reports = Array.AsReadOnly(reports.ToArray());
    }

    public IReadOnlyList<OperativeSnapshot> Operatives { get; }
    public IReadOnlyList<IntelligenceAssessment> Reports { get; }
}

public sealed record IntelligenceTaskSettings(
    int TalkDurationMinutes, int MaximumFollowMinutes, int TalkSuccessDifference,
    int FollowObservationIntervalMinutes, float FollowConfidenceBase,
    float FollowConfidencePerSkillPoint, float TalkSuccessConfidence,
    float TalkFailureConfidence)
{
    public static IntelligenceTaskSettings Load(string contentDirectory)
    {
        var path = Path.Combine(contentDirectory, "intelligence-tasks.json");
        var value = JsonSerializer.Deserialize<IntelligenceTaskSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value is null || value.TalkDurationMinutes < 1 || value.MaximumFollowMinutes < 1 ||
            value.TalkSuccessDifference < 0 || value.FollowObservationIntervalMinutes < 1 ||
            value.FollowConfidenceBase is < 0 or > 1 || value.FollowConfidencePerSkillPoint is < 0 or > 1 ||
            value.TalkSuccessConfidence is < 0 or > 1 || value.TalkFailureConfidence is < 0 or > 1)
            throw new InvalidDataException("intelligence-tasks.json contains invalid task settings.");
        return value;
    }
}

/// <summary>
/// Simulation-owned command boundary and task resolver. Only sanitized value
/// projections leave this service; UI code never retains an ECS Entity.
/// </summary>
public sealed class OperativeManagementSystem
{
    private readonly EntityStore _store;
    private readonly ContentCatalog _catalog;
    private readonly AgentLodService _lod;
    private readonly IntelligenceTaskSettings _settings;
    private readonly Dictionary<int, Entity> _agents;
    private readonly List<IntelligenceAssessment> _reports = [];
    private readonly Dictionary<int, List<IntelligenceEvidence>> _taskEvidence = [];
    private readonly Dictionary<int, long> _lastObservationMinute = [];
    private long _lastUpdatedMinute = -1;

    public OperativeManagementSystem(EntityStore store, ContentCatalog catalog, AgentLodService lod,
        IntelligenceTaskSettings? settings = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _lod = lod ?? throw new ArgumentNullException(nameof(lod));
        _settings = settings ?? new IntelligenceTaskSettings(240, 10080, 12, 60, 0.35f, 0.005f, 0.55f, 0.2f);
        _agents = store.Query<Identity>().Entities.ToDictionary(entity => entity.Id);
        foreach (var operative in store.Query<Identity>().Entities
                     .Where(entity => entity.Tags.Has<OperativeTag>()).ToArray())
        {
            var job = catalog.Jobs.First(item => item.Hash == operative.GetComponent<Identity>().OccupationId);
            operative.AddComponent(new OperativeWorkSchedule
            {
                WorkDaysMask = ToMask(job.WorkDays),
                WorkStartMinute = job.WorkStartMinute,
                WorkEndMinute = job.WorkEndMinute
            });
            operative.AddComponent<OperativeAssignment>();
        }
    }

    public OperativeManagementProjection Capture(long minute)
    {
        var roster = _store.Query<Identity>().Entities.Where(agent => agent.Tags.Has<OperativeTag>())
            .OrderBy(agent => agent.Id).Select(agent =>
        {
            var identity = agent.GetComponent<Identity>();
            var rota = agent.GetComponent<OperativeWorkSchedule>();
            var task = agent.GetComponent<OperativeAssignment>();
            var job = _catalog.Jobs.FirstOrDefault(item => item.Hash == identity.OccupationId)?.Name ?? "Unknown occupation";
            return new OperativeSnapshot(agent.Id, $"Agent {agent.Id} (Name ID {identity.NameId})",
                identity.IntelligenceRole.ToString(), job, rota.WorkDaysMask, rota.WorkStartMinute,
                rota.WorkEndMinute, task.Kind, task.TargetAgentId, task.EndsAtMinute);
        }).ToArray();
        return new OperativeManagementProjection(roster,
            _reports.OrderByDescending(report => report.Minute));
    }

    public bool SetRota(OperativeRotaCommand command, long currentMinute)
    {
        var dayMask = (byte)(command.WorkDaysMask & 0x7f);
        if (!TryOperative(command.OperativeId, out var agent) || dayMask == 0 ||
            command.WorkStartMinute < 0 || command.WorkEndMinute > SimulationDefaults.SimulationMinutesPerDay ||
            command.WorkStartMinute >= command.WorkEndMinute) return false;
        ref var rota = ref agent.GetComponent<OperativeWorkSchedule>();
        var previous = rota;
        rota.WorkDaysMask = dayMask;
        rota.WorkStartMinute = command.WorkStartMinute;
        rota.WorkEndMinute = command.WorkEndMinute;
        try
        {
            _lod.RefreshCoarseProfile(agent);
            if (agent.TryGetComponent<DecisionState>(out var decision))
                DecisionInvalidation.SignalCritical(ref agent.GetComponent<DecisionState>(),
                    FactDependencyMask.All, DecisionWakeReason.Schedule);
        }
        catch (InvalidDataException)
        {
            rota = previous;
            _lod.RefreshCoarseProfile(agent);
            return false;
        }
        return true;
    }

    public bool Assign(OperativeTaskCommand command, long currentMinute)
    {
        if (!TryOperative(command.OperativeId, out var operative) ||
            !_agents.TryGetValue(command.TargetAgentId, out var target) || target.IsNull ||
            command.TargetAgentId == command.OperativeId || command.Kind is not (OperativeTaskKind.Follow or OperativeTaskKind.Talk) ||
            operative.GetComponent<OperativeAssignment>().Kind != OperativeTaskKind.None ||
            (command.Kind == OperativeTaskKind.Follow &&
                (command.DurationMinutes < 1 || command.DurationMinutes > _settings.MaximumFollowMinutes))) return false;

        var ends = command.Kind == OperativeTaskKind.Follow
            ? currentMinute + command.DurationMinutes : currentMinute + _settings.TalkDurationMinutes;
        ref var active = ref operative.GetComponent<OperativeAssignment>();
        active = new OperativeAssignment
        {
            Kind = command.Kind, TargetAgentId = command.TargetAgentId,
            StartedAtMinute = currentMinute, EndsAtMinute = ends
        };
        if (!BeginTravel(operative, target.GetComponent<AgentLocation>().CurrentLocationId))
        {
            active = default;
            return false;
        }
        _taskEvidence[operative.Id] = [];
        _lastObservationMinute[operative.Id] = currentMinute - _settings.FollowObservationIntervalMinutes;
        return true;
    }

    public bool Recall(OperativeRecallCommand command, long minute)
    {
        if (!TryOperative(command.OperativeId, out var agent)) return false;
        ref var assignment = ref agent.GetComponent<OperativeAssignment>();
        if (assignment.Kind == OperativeTaskKind.None) return false;
        Finish(agent, assignment.TargetAgentId, minute, "Assignment recalled before completion.", 0.1f,
            _taskEvidence.GetValueOrDefault(agent.Id) ?? []);
        _taskEvidence.Remove(agent.Id);
        _lastObservationMinute.Remove(agent.Id);
        ClearAssignment(agent, ref assignment);
        return true;
    }

    /// <summary>Advance one simulation minute, resolving tasks and collecting sourced reports.</summary>
    public void Update(long minute)
    {
        var elapsed = _lastUpdatedMinute < 0 ? 0 : Math.Max(0, minute - _lastUpdatedMinute);
        Update(elapsed, minute);
    }

    public void Update(double elapsedMinutes, long minute)
    {
        _lastUpdatedMinute = minute;
        foreach (var operative in _store.Query<Identity>().Entities.Where(agent => agent.Tags.Has<OperativeTag>())
                     .OrderBy(agent => agent.Id))
        {
            ref var assignment = ref operative.GetComponent<OperativeAssignment>();
            if (assignment.Kind == OperativeTaskKind.None || minute < assignment.StartedAtMinute) continue;
            if (!_agents.TryGetValue(assignment.TargetAgentId, out var target) || target.IsNull)
            {
                Finish(operative, assignment.TargetAgentId, minute, "Target could not be located.", 0f, []);
                _taskEvidence.Remove(operative.Id);
                _lastObservationMinute.Remove(operative.Id);
                ClearAssignment(operative, ref assignment);
                continue;
            }

            AdvanceTravel(operative, target, Math.Max(0d, elapsedMinutes));

            if (assignment.Kind == OperativeTaskKind.Follow)
            {
                if (!_taskEvidence.TryGetValue(operative.Id, out var evidence))
                    _taskEvidence[operative.Id] = evidence = [];
                var lastObserved = _lastObservationMinute.GetValueOrDefault(operative.Id,
                    assignment.StartedAtMinute - _settings.FollowObservationIntervalMinutes);
                if (minute >= lastObserved + _settings.FollowObservationIntervalMinutes)
                {
                    if (operative.GetComponent<AgentLocation>().CurrentLocationId ==
                        target.GetComponent<AgentLocation>().CurrentLocationId)
                    {
                        var locationId = target.GetComponent<AgentLocation>().CurrentLocationId;
                        var location = _catalog.World.Locations.FirstOrDefault(item => item.Hash == locationId)?.Name ?? "unknown location";
                        evidence.Add(new IntelligenceEvidence(minute, operative.Id, target.Id, "sighting",
                            $"Seen at {location}."));
                        if (target.TryGetComponent<CoordinationState>(out var interaction) && interaction.Active &&
                            _agents.ContainsKey(interaction.PartnerEntityId))
                            evidence.Add(new IntelligenceEvidence(minute, operative.Id, target.Id, "interaction",
                                $"Seen interacting with Agent {interaction.PartnerEntityId}."));
                    }
                    _lastObservationMinute[operative.Id] = minute;
                }
                if (minute >= assignment.EndsAtMinute)
                {
                    var skill = Attribute(operative, "perception");
                    var confidence = Math.Clamp(_settings.FollowConfidenceBase + skill * _settings.FollowConfidencePerSkillPoint,
                        _settings.FollowConfidenceBase, 0.95f);
                    Finish(operative, target.Id, minute, evidence.Count == 0
                        ? "The operative completed the follow assignment without a confirmed sighting."
                        : "The operative completed the follow assignment and recorded sightings.", confidence, evidence);
                    _taskEvidence.Remove(operative.Id);
                    _lastObservationMinute.Remove(operative.Id);
                    ClearAssignment(operative, ref assignment);
                }
            }
            else if (assignment.Kind == OperativeTaskKind.Talk && minute >= assignment.EndsAtMinute)
            {
                var score = Attribute(operative, "charisma") - Attribute(target, "willpower") * 0.35f;
                var reachedTarget = operative.GetComponent<AgentLocation>().CurrentLocationId ==
                    target.GetComponent<AgentLocation>().CurrentLocationId;
                var success = reachedTarget && score >= _settings.TalkSuccessDifference;
                var factionName = success && target.TryGetComponent<PoliticalAlignment>(out var alignment)
                    ? _catalog.Factions.FirstOrDefault(faction => faction.FactionId == alignment.FactionId)?.Name
                    : null;
                var evidence = success
                    ? new[] { new IntelligenceEvidence(minute, operative.Id, target.Id, "interview",
                        factionName is null ? "The subject shared views during a conversation."
                            : $"The subject expressed support for {factionName}.") }
                    : Array.Empty<IntelligenceEvidence>();
                Finish(operative, target.Id, minute,
                    success ? "Conversation completed; the subject shared their views."
                        : reachedTarget ? "The subject did not share their views."
                        : "The operative could not reach the subject before the interview window ended.",
                    success ? _settings.TalkSuccessConfidence : _settings.TalkFailureConfidence, evidence);
                _taskEvidence.Remove(operative.Id);
                _lastObservationMinute.Remove(operative.Id);
                ClearAssignment(operative, ref assignment);
            }
        }
    }

    private bool BeginTravel(Entity operative, int destination)
    {
        ref var travel = ref operative.GetComponent<AgentTravel>();
        var start = operative.GetComponent<AgentLocation>().CurrentLocationId;
        var route = _catalog.World.FindShortestRoute(start, destination);
        if (route is null) return false;
        travel.RouteLocationIds = route.LocationIds.ToArray();
        travel.TotalTravelMinutes = route.TravelMinutes;
        travel.RoutePosition = 0;
        travel.DestinationLocationId = destination;
        travel.Mode = route.LocationIds.Count > 1 ? AgentTravelMode.Travelling : AgentTravelMode.Stationary;
        travel.RemainingTravelMinutes = route.LocationIds.Count > 1
            ? _catalog.World.GetTravelMinutes(route.LocationIds[0], route.LocationIds[1]) : 0f;
        return true;
    }

    private void AdvanceTravel(Entity operative, Entity target, double elapsedMinutes)
    {
        ref var location = ref operative.GetComponent<AgentLocation>();
        ref var travel = ref operative.GetComponent<AgentTravel>();
        var destination = target.GetComponent<AgentLocation>().CurrentLocationId;
        if (location.CurrentLocationId == destination)
        {
            travel.Mode = AgentTravelMode.Stationary;
            travel.DestinationLocationId = 0;
            travel.RemainingTravelMinutes = 0f;
            return;
        }
        if (travel.Mode != AgentTravelMode.Travelling || travel.DestinationLocationId != destination)
            if (!BeginTravel(operative, destination)) return;

        while (elapsedMinutes > 0d && travel.Mode == AgentTravelMode.Travelling)
        {
            if (travel.RemainingTravelMinutes > elapsedMinutes)
            {
                travel.RemainingTravelMinutes -= (float)elapsedMinutes;
                break;
            }
            elapsedMinutes -= travel.RemainingTravelMinutes;
            travel.RoutePosition++;
            location.CurrentLocationId = travel.RouteLocationIds[travel.RoutePosition];
            if (travel.RoutePosition == travel.RouteLocationIds.Length - 1)
            {
                travel.Mode = AgentTravelMode.Stationary;
                travel.DestinationLocationId = 0;
                travel.RemainingTravelMinutes = 0f;
                break;
            }
            travel.RemainingTravelMinutes = _catalog.World.GetTravelMinutes(
                travel.RouteLocationIds[travel.RoutePosition], travel.RouteLocationIds[travel.RoutePosition + 1]);
        }
    }

    private static void ClearAssignment(Entity operative, ref OperativeAssignment assignment)
    {
        assignment = default;
        ref var travel = ref operative.GetComponent<AgentTravel>();
        travel.Mode = AgentTravelMode.Stationary;
        travel.DestinationLocationId = 0;
        travel.RemainingTravelMinutes = 0f;
        if (operative.TryGetComponent<DecisionState>(out var decision))
            DecisionInvalidation.SignalCritical(ref operative.GetComponent<DecisionState>(),
                FactDependencyMask.All, DecisionWakeReason.Schedule);
    }

    private void Finish(Entity operative, int targetId, long minute, string summary, float confidence,
        IReadOnlyList<IntelligenceEvidence> evidence) =>
        _reports.Add(new IntelligenceAssessment(minute, operative.Id, targetId, summary,
            Math.Clamp(confidence, 0f, 1f), evidence));

    private bool TryOperative(int id, out Entity entity) =>
        _agents.TryGetValue(id, out entity) && !entity.IsNull && entity.Tags.Has<OperativeTag>();

    private float Attribute(Entity entity, string id)
    {
        var index = _catalog.AgentAttributes.Definitions.ToList().FindIndex(item => item.Id == id);
        return index >= 0 ? entity.GetComponent<AgentAttributes>().Values[index] : 0f;
    }

    private static byte ToMask(IEnumerable<int> days)
    {
        byte mask = 0;
        foreach (var day in days) if (day is >= 1 and <= 7) mask |= (byte)(1 << (day - 1));
        return mask;
    }
}
