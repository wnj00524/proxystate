using Friflo.Engine.ECS;
using System.Text.Json;

namespace ProxyState.Simulation;

/// <summary>Player-authored recurring work hours for an operative (Monday is bit zero).</summary>
public struct OperativeRota : IComponent
{
    public byte WorkDaysMask;
    public int WorkStartMinute;
    public int WorkEndMinute;
}

public enum OperativeTaskKind : byte { None, Follow, BuildRapport }

/// <summary>Compact active assignment state retained on the operative.</summary>
public struct OperativeAssignment : IComponent
{
    public OperativeTaskKind Kind;
    public int TargetAgentId;
    public long StartedAtMinute;
    public long EndsAtMinute;
}

public readonly record struct OperativeTaskCommand(
    int OperativeId, OperativeTaskKind Kind, int TargetAgentId, int DurationMinutes = 0,
    long StartAtMinute = -1);
public readonly record struct OperativeRecallCommand(int OperativeId);
public readonly record struct OperativeRotaCommand(
    int OperativeId, byte WorkDaysMask, int WorkStartMinute, int WorkEndMinute);
public enum OperativeCommandKind : byte { SetRota, Assign, Recall }
public readonly record struct OperativeCommand(
    OperativeCommandKind Kind, int OperativeId, byte WorkDaysMask = 0,
    int WorkStartMinute = 0, int WorkEndMinute = 0, OperativeTaskKind TaskKind = OperativeTaskKind.None,
    int TargetAgentId = 0, int DurationMinutes = 0, long StartAtMinute = -1);

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
                    command.TargetAgentId, command.DurationMinutes, command.StartAtMinute), minute),
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
    int TargetAgentId, long TaskEndMinute)
{
    /// <summary>Scheduled start minute; equals the current minute for immediate tasks.</summary>
    public long TaskStartMinute { get; init; }
}

public readonly record struct IntelligenceEvidence(
    long Minute, int SourceOperativeId, int SubjectAgentId, string Kind, string Detail,
    string? DiscoveredValue = null, float? NumericValue = null);

/// <summary>Sanitized affinity from a target agent toward one operative.</summary>
public readonly record struct OperativeAffinitySnapshot(int OperativeId, float Affinity);

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

/// <summary>Formats elapsed simulation minutes as the in-world day and clock time.</summary>
public static class SimulationTimeFormatter
{
    public static string Format(long minute)
    {
        var day = minute / SimulationDefaults.SimulationMinutesPerDay + 1;
        var minuteOfDay = (int)(minute % SimulationDefaults.SimulationMinutesPerDay);
        return $"Day {day} · {minuteOfDay / 60:D2}:{minuteOfDay % 60:D2}";
    }
}

public sealed class OperativeManagementProjection
{
    public OperativeManagementProjection(IEnumerable<OperativeSnapshot> operatives,
        IEnumerable<IntelligenceAssessment> reports,
        IEnumerable<IntelligenceEvidence>? newRoutineDiscoveries = null, long currentMinute = 0,
        IEnumerable<RapportAffinityChange>? newRapportAffinities = null)
    {
        Operatives = Array.AsReadOnly(operatives.ToArray());
        Reports = Array.AsReadOnly(reports.ToArray());
        NewRoutineDiscoveries = Array.AsReadOnly(
            (newRoutineDiscoveries ?? Enumerable.Empty<IntelligenceEvidence>()).ToArray());
        NewRapportAffinities = Array.AsReadOnly(
            (newRapportAffinities ?? Enumerable.Empty<RapportAffinityChange>()).ToArray());
        CurrentMinute = currentMinute;
    }

    public IReadOnlyList<OperativeSnapshot> Operatives { get; }
    public IReadOnlyList<IntelligenceAssessment> Reports { get; }
    /// <summary>New copied facts for immediate application to player dossiers.</summary>
    public IReadOnlyList<IntelligenceEvidence> NewRoutineDiscoveries { get; }
    /// <summary>Sanitized relationship values updated by completed rapport tasks.</summary>
    public IReadOnlyList<RapportAffinityChange> NewRapportAffinities { get; }
    /// <summary>Simulation minute used by schedule selectors and pending status.</summary>
    public long CurrentMinute { get; }
}

public readonly record struct RapportAffinityChange(
    int TargetAgentId, int OperativeId, float Affinity);

public sealed record IntelligenceTaskSettings
{
    public int RapportDurationMinutes { get; init; } = 120;
    public int MaximumFollowMinutes { get; init; } = 10080;
    public int FollowObservationIntervalMinutes { get; init; } = 60;
    public float FollowConfidenceBase { get; init; } = 0.35f;
    public float FollowConfidencePerSkillPoint { get; init; } = 0.005f;
    public float RapportBaseSuccessChance { get; init; } = 50f;
    public float RapportMinimumSuccessChance { get; init; } = 5f;
    public float RapportMaximumSuccessChance { get; init; } = 80f;
    public float RapportDecreaseChance { get; init; } = 20f;
    public float RapportIncreaseDelta { get; init; } = 5f;
    public float RapportDecreaseDelta { get; init; } = 3f;
    public Dictionary<string, float> RapportOperativeAttributeWeights { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float> RapportTargetAttributeWeights { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float> RapportTargetTraitModifiers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static IntelligenceTaskSettings Load(string contentDirectory, ContentCatalog? catalog = null)
    {
        var path = Path.Combine(contentDirectory, "intelligence-tasks.json");
        var value = JsonSerializer.Deserialize<IntelligenceTaskSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value is null)
            throw new InvalidDataException("intelligence-tasks.json contains invalid task settings.");
        value.Validate(catalog);
        return value;
    }

    public void Validate(ContentCatalog? catalog = null)
    {
        if (RapportDurationMinutes < 1 || MaximumFollowMinutes < 1 ||
            FollowObservationIntervalMinutes < 1 ||
            FollowConfidenceBase is < 0 or > 1 || FollowConfidencePerSkillPoint is < 0 or > 1 ||
            !float.IsFinite(RapportMinimumSuccessChance) || RapportMinimumSuccessChance is < 0 or > 100 ||
            !float.IsFinite(RapportMaximumSuccessChance) || RapportMaximumSuccessChance < RapportMinimumSuccessChance ||
            !float.IsFinite(RapportDecreaseChance) || RapportMaximumSuccessChance + RapportDecreaseChance > 100 ||
            !float.IsFinite(RapportBaseSuccessChance) || RapportBaseSuccessChance is < 0 or > 100 ||
            RapportDecreaseChance is < 0 or > 100 ||
            !float.IsFinite(RapportIncreaseDelta) || RapportIncreaseDelta < 0 ||
            !float.IsFinite(RapportDecreaseDelta) || RapportDecreaseDelta < 0 ||
            !ValidWeights(RapportOperativeAttributeWeights, allowNegative: true) ||
            !ValidWeights(RapportTargetAttributeWeights, allowNegative: true) ||
            !ValidWeights(RapportTargetTraitModifiers, allowNegative: true))
            throw new InvalidDataException("intelligence-tasks.json contains invalid task settings.");
        if (catalog is not null)
        {
            var attributeIds = catalog.AgentAttributes.Definitions.Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in RapportOperativeAttributeWeights.Keys.Concat(RapportTargetAttributeWeights.Keys))
                if (!attributeIds.Contains(id))
                    throw new InvalidDataException($"intelligence-tasks.json references unknown rapport attribute '{id}'.");
            var traitIds = catalog.Traits.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in RapportTargetTraitModifiers.Keys)
                if (!traitIds.Contains(id))
                    throw new InvalidDataException($"intelligence-tasks.json references unknown rapport trait '{id}'.");
        }
    }

    private static bool ValidWeights(IReadOnlyDictionary<string, float> values, bool allowNegative) =>
        values is not null && values.All(item => !string.IsNullOrWhiteSpace(item.Key) &&
            float.IsFinite(item.Value) && (allowNegative || item.Value >= 0));
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
    private readonly Random _random;
    private readonly AgentSocialIndexes _socialIndexes;
    private readonly RapportAttributeModifier[] _operativeModifiers;
    private readonly RapportAttributeModifier[] _targetModifiers;
    private readonly RapportTraitModifier[] _traitModifiers;
    private readonly Dictionary<int, Entity> _agents;
    private readonly List<IntelligenceAssessment> _reports = [];
    private readonly Dictionary<int, List<IntelligenceEvidence>> _taskEvidence = [];
    private readonly List<IntelligenceEvidence> _pendingRoutineDiscoveries = [];
    private readonly List<RapportAffinityChange> _pendingRapportAffinities = [];
    private readonly Dictionary<int, long> _lastObservationMinute = [];
    private long _lastUpdatedMinute = -1;

    public OperativeManagementSystem(EntityStore store, ContentCatalog catalog, AgentLodService lod,
        IntelligenceTaskSettings? settings = null, Random? random = null,
        AgentSocialIndexes? socialIndexes = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _lod = lod ?? throw new ArgumentNullException(nameof(lod));
        _settings = settings ?? new IntelligenceTaskSettings();
        _settings.Validate(catalog);
        _random = random ?? new Random(0);
        _socialIndexes = socialIndexes ?? BuildSocialIndexes(store);
        _operativeModifiers = CompileAttributeModifiers(_settings.RapportOperativeAttributeWeights, catalog);
        _targetModifiers = CompileAttributeModifiers(_settings.RapportTargetAttributeWeights, catalog);
        _traitModifiers = CompileTraitModifiers(_settings.RapportTargetTraitModifiers, catalog);
        _agents = store.Query<Identity>().Entities.ToDictionary(entity => entity.Id);
        foreach (var operative in store.Query<Identity>().Entities
                     .Where(entity => entity.Tags.Has<OperativeTag>()).ToArray())
        {
            var job = catalog.Jobs.First(item => item.Hash == operative.GetComponent<Identity>().OccupationId);
            operative.AddComponent(new OperativeRota
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
            var rota = agent.GetComponent<OperativeRota>();
            var task = agent.GetComponent<OperativeAssignment>();
            var job = _catalog.Jobs.FirstOrDefault(item => item.Hash == identity.OccupationId)?.Name ?? "Unknown occupation";
            return new OperativeSnapshot(agent.Id, $"Agent {agent.Id} (Name ID {identity.NameId})",
                identity.IntelligenceRole.ToString(), job, rota.WorkDaysMask, rota.WorkStartMinute,
                rota.WorkEndMinute, task.Kind, task.TargetAgentId, task.EndsAtMinute)
            {
                TaskStartMinute = task.StartedAtMinute
            };
        }).ToArray();
        var discoveries = _pendingRoutineDiscoveries.ToArray();
        _pendingRoutineDiscoveries.Clear();
        var rapportChanges = _pendingRapportAffinities.ToArray();
        _pendingRapportAffinities.Clear();
        return new OperativeManagementProjection(roster,
            _reports.OrderByDescending(report => report.Minute), discoveries, minute, rapportChanges);
    }

    public bool SetRota(OperativeRotaCommand command, long currentMinute)
    {
        var dayMask = (byte)(command.WorkDaysMask & 0x7f);
        if (!TryOperative(command.OperativeId, out var agent) || dayMask == 0 ||
            command.WorkStartMinute < 0 || command.WorkEndMinute > SimulationDefaults.SimulationMinutesPerDay ||
            command.WorkStartMinute >= command.WorkEndMinute) return false;
        ref var rota = ref agent.GetComponent<OperativeRota>();
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
        var startAtMinute = command.StartAtMinute < 0 ? currentMinute : command.StartAtMinute;
        if (!TryOperative(command.OperativeId, out var operative) ||
            !_agents.TryGetValue(command.TargetAgentId, out var target) || target.IsNull ||
            command.TargetAgentId == command.OperativeId || command.Kind is not (OperativeTaskKind.Follow or OperativeTaskKind.BuildRapport) ||
            operative.GetComponent<OperativeAssignment>().Kind != OperativeTaskKind.None ||
            (command.Kind == OperativeTaskKind.Follow &&
                (command.DurationMinutes < 1 || command.DurationMinutes > _settings.MaximumFollowMinutes))) return false;

        var taskDuration = command.Kind == OperativeTaskKind.Follow
            ? command.DurationMinutes : _settings.RapportDurationMinutes;
        if (startAtMinute < currentMinute || startAtMinute > long.MaxValue - taskDuration) return false;

        var ends = command.Kind == OperativeTaskKind.Follow
            ? startAtMinute + command.DurationMinutes : startAtMinute + _settings.RapportDurationMinutes;
        var targetLocationId = target.GetComponent<AgentLocation>().CurrentLocationId;
        if (startAtMinute == currentMinute &&
            !BeginTravel(operative, targetLocationId))
            return false;
        if (startAtMinute > currentMinute)
        {
            try
            {
                if (_catalog.World.FindShortestRoute(
                        operative.GetComponent<AgentLocation>().CurrentLocationId, targetLocationId) is null)
                    return false;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }
        if (command.Kind == OperativeTaskKind.BuildRapport)
            EnsureRapportRelationship(target, operative);
        // Promotion catches up the target before follow or rapport behavior reads detailed state.
        _lod.AcquireOperativeTaskTarget(target.Id);
        ref var active = ref operative.GetComponent<OperativeAssignment>();
        active = new OperativeAssignment
        {
            Kind = command.Kind, TargetAgentId = command.TargetAgentId,
            StartedAtMinute = startAtMinute, EndsAtMinute = ends
        };
        _taskEvidence[operative.Id] = [];
        _lastObservationMinute[operative.Id] = startAtMinute - _settings.FollowObservationIntervalMinutes;
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
        var previousMinute = _lastUpdatedMinute;
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

            var taskElapsed = previousMinute < assignment.StartedAtMinute
                ? Math.Max(0d, minute - assignment.StartedAtMinute)
                : Math.Max(0d, elapsedMinutes);
            AdvanceTravel(operative, target, taskElapsed);

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
                        RecordRoutineDiscoveries(target, locationId, minute, operative.Id, evidence);
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
            else if (assignment.Kind == OperativeTaskKind.BuildRapport && minute >= assignment.EndsAtMinute)
            {
                var reachedTarget = operative.GetComponent<AgentLocation>().CurrentLocationId ==
                    target.GetComponent<AgentLocation>().CurrentLocationId;
                if (!reachedTarget)
                {
                    Finish(operative, target.Id, minute,
                        "The operative could not reach the subject before the rapport window ended.",
                        _settings.FollowConfidenceBase, []);
                }
                else
                {
                    var relationship = EnsureRapportRelationship(target, operative);
                    var roll = _random.Next(100);
                    var chance = RapportSuccessChance(operative, target);
                    var rapportDelta = roll < chance ? _settings.RapportIncreaseDelta
                        : roll < chance + _settings.RapportDecreaseChance ? -_settings.RapportDecreaseDelta : 0f;
                    ref var edge = ref relationship.GetComponent<EdgeData>();
                    var oldAffinity = edge.Affinity;
                    if (rapportDelta != 0f)
                    {
                        var affinityWithoutRapport = AgentAffinityCalculator.Calculate(edge.Source, edge.Target,
                            edge.IsFamily, edge.KnownTraitMask, _catalog.AllTraitBits,
                            _catalog.Traits.Count, _catalog.Affinity);
                        edge.RapportDelta = Math.Clamp(edge.RapportDelta + rapportDelta,
                            _catalog.Affinity.Minimum - affinityWithoutRapport,
                            _catalog.Affinity.Maximum - affinityWithoutRapport);
                        edge.Affinity = AgentAffinityCalculator.Calculate(edge.Source, edge.Target,
                            edge.IsFamily, edge.KnownTraitMask, _catalog.AllTraitBits,
                            _catalog.Traits.Count, _catalog.Affinity, edge.RapportDelta);
                        if (edge.Affinity != oldAffinity && edge.Source.TryGetComponent<DecisionState>(out _))
                            DecisionInvalidation.SignalTargetAvailability(ref edge.Source.GetComponent<DecisionState>());
                    }

                    var outcome = edge.Affinity > oldAffinity ? "increased"
                        : edge.Affinity < oldAffinity ? "decreased" : "unchanged";
                    var detail = $"The target's affinity toward the operative {outcome} from {oldAffinity:0.#} to {edge.Affinity:0.#}.";
                    var evidence = new[] { new IntelligenceEvidence(minute, operative.Id, target.Id,
                        "rapport", detail, NumericValue: edge.Affinity) };
                    _pendingRapportAffinities.Add(new RapportAffinityChange(target.Id, operative.Id, edge.Affinity));
                    Finish(operative, target.Id, minute, $"Rapport interaction completed; affinity {outcome}.",
                        rapportDelta > 0f ? 0.7f : 0.4f, evidence);
                }
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
        WorldRoute? route;
        try
        {
            route = _catalog.World.FindShortestRoute(start, destination);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
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

    /// <summary>Gets or creates the target-to-operative edge used for rapport.</summary>
    private Entity EnsureRapportRelationship(Entity target, Entity operative)
    {
        if (_socialIndexes.TryGetDirectedEdge(target.Id, operative.Id, out var indexed) &&
            _socialIndexes.TryGetEdge(indexed.EdgeEntityId, out var existing) && !existing.IsNull)
            return existing;

        var isFamily = IsFamilyPair(target.Id, operative.Id);
        var edgeEntity = _store.CreateEntity(new EdgeData
        {
            Source = target,
            Target = operative,
            IsFamily = isFamily,
            Affinity = AgentAffinityCalculator.Calculate(target, operative, isFamily,
                knownTraitMask: 0, allTraitBits: 0, traitCount: 0, _catalog.Affinity)
        });
        _socialIndexes.NotifySocialGraphChanged();
        _socialIndexes.Rebuild(_store);
        return edgeEntity;
    }

    private bool IsFamilyPair(int firstAgentId, int secondAgentId)
    {
        foreach (var network in _store.Query<AgentNetworkData>().Entities)
        {
            if (network.GetComponent<AgentNetworkData>().TypeHash != _catalog.Affinity.FamilyNetworkTypeHash)
                continue;
            var containsFirst = false;
            var containsSecond = false;
            foreach (var member in network.GetIncomingLinks<AgentNetworkMembership>())
            {
                containsFirst |= member.Entity.Id == firstAgentId;
                containsSecond |= member.Entity.Id == secondAgentId;
                if (containsFirst && containsSecond) return true;
            }
        }
        return false;
    }

    private float RapportSuccessChance(Entity operative, Entity target)
    {
        var chance = _settings.RapportBaseSuccessChance;
        var operativeAttributes = operative.GetComponent<AgentAttributes>().Values;
        foreach (var modifier in _operativeModifiers)
            chance += (operativeAttributes[modifier.Index] - modifier.Average) * modifier.Weight;

        var targetAttributes = target.GetComponent<AgentAttributes>().Values;
        foreach (var modifier in _targetModifiers)
            chance += (targetAttributes[modifier.Index] - modifier.Average) * modifier.Weight;

        if (target.TryGetComponent<Psychology>(out var psychology))
            foreach (var modifier in _traitModifiers)
                if ((psychology.TraitMask & modifier.Bit) != 0)
                    chance += modifier.SuccessChanceModifier;

        return Math.Clamp(chance, _settings.RapportMinimumSuccessChance,
            _settings.RapportMaximumSuccessChance);
    }

    private static RapportAttributeModifier[] CompileAttributeModifiers(
        IReadOnlyDictionary<string, float> configured, ContentCatalog catalog)
    {
        return configured.Select(item =>
        {
            var definition = catalog.AgentAttributes.Definitions.FirstOrDefault(attribute =>
                string.Equals(attribute.Id, item.Key, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
                throw new InvalidDataException($"intelligence-tasks.json references unknown rapport attribute '{item.Key}'.");
            var index = catalog.AgentAttributes.GetIndex(definition.Id);
            return new RapportAttributeModifier(index, item.Value, definition.Average);
        }).ToArray();
    }

    private static RapportTraitModifier[] CompileTraitModifiers(
        IReadOnlyDictionary<string, float> configured, ContentCatalog catalog)
    {
        return configured.Select(item =>
        {
            var trait = catalog.Traits.FirstOrDefault(definition =>
                string.Equals(definition.Id, item.Key, StringComparison.OrdinalIgnoreCase));
            if (trait is null)
                throw new InvalidDataException($"intelligence-tasks.json references unknown rapport trait '{item.Key}'.");
            return new RapportTraitModifier(trait.Bit, item.Value);
        }).ToArray();
    }

    private static AgentSocialIndexes BuildSocialIndexes(EntityStore store)
    {
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        return indexes;
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

    private void ClearAssignment(Entity operative, ref OperativeAssignment assignment)
    {
        if (assignment.Kind is OperativeTaskKind.Follow or OperativeTaskKind.BuildRapport)
            _lod.ReleaseOperativeTaskTarget(assignment.TargetAgentId);
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

    /// <summary>
    /// Converts an actual co-located sighting into routine intelligence. The
    /// simulation may compare the observed location with the target's assigned
    /// home and work nodes; only the resulting sourced evidence leaves ECS.
    /// </summary>
    private void RecordRoutineDiscoveries(Entity target, int observedLocationId, long minute,
        int operativeId, List<IntelligenceEvidence> evidence)
    {
        var location = target.GetComponent<AgentLocation>();
        if (location.HomeLocationId != 0 && observedLocationId == location.HomeLocationId)
        {
            var homeName = LocationName(location.HomeLocationId);
            AddRoutineEvidence(evidence, new IntelligenceEvidence(minute, operativeId, target.Id,
                "residence", $"Identified residence: {homeName}.", homeName));
        }

        if (location.WorkLocationId == 0 || observedLocationId != location.WorkLocationId) return;

        var workplaceName = LocationName(location.WorkLocationId);
        AddRoutineEvidence(evidence, new IntelligenceEvidence(minute, operativeId, target.Id,
            "workplace", $"Identified workplace: {workplaceName}.", workplaceName));

        var occupationId = target.GetComponent<Identity>().OccupationId;
        var occupation = _catalog.Jobs.FirstOrDefault(job => job.Hash == occupationId);
        if (occupation is not null)
            AddRoutineEvidence(evidence, new IntelligenceEvidence(minute, operativeId, target.Id,
                "occupation", $"Identified occupation: {occupation.Name}.", occupation.Name));
    }

    private string LocationName(int locationId) =>
        _catalog.World.Locations.FirstOrDefault(item => item.Hash == locationId)?.Name ?? "unknown location";

    private void AddRoutineEvidence(List<IntelligenceEvidence> evidence, IntelligenceEvidence discovery)
    {
        // A target can be seen at home or work many times during one follow;
        // retain one copy of each discovered fact in that assignment's report.
        if (!evidence.Any(item => item.Kind == discovery.Kind && item.Detail == discovery.Detail))
        {
            evidence.Add(discovery);
            _pendingRoutineDiscoveries.Add(discovery);
        }
    }

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

    private readonly record struct RapportAttributeModifier(int Index, float Weight, float Average);
    private readonly record struct RapportTraitModifier(long Bit, float SuccessChanceModifier);
}
