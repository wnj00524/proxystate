using System.Text.Json;

namespace ProxyState.Simulation;

public sealed record TraitDefinition(string Id, string Name, long Bit, float Prevalence);
public sealed record ResponsePoint(float X, float Y);
public sealed record UtilityInputDefinition(NumericExpressionDefinition Expression, float Weight, List<ResponsePoint> Curve)
;
public sealed record TraitUtilityModifier(string Trait, float Modifier);
public sealed record ActionControlDefinition(
    int MinimumCommitmentMinutes,
    float SwitchingThreshold,
    int CooldownMinutes,
    float UrgentPreemptionThreshold,
    bool CooldownOnExit = true);
public sealed record ActionEffectDefinition(string Attribute, float PerMinute, string? Subject = null);
public sealed record ActivityDefinition(string Id, string Name, int Hash);
public sealed record TargetRankDefinition(NumericExpressionDefinition Value, string Order)
;
public sealed record TargetQueryDefinition(
    string Relation,
    List<PredicateDefinition> Requirements,
    List<TargetRankDefinition> RankBy,
    int? Limit,
    string? NetworkType = null);
public sealed record TargetDefinition(string Kind, string? Value, TargetQueryDefinition? Query);
public enum ExecutorKind : byte
{
    PerformHere,
    PerformAtLocation,
    PerformWithEntity,
    Wait
}
public sealed record ExecutorDefinition(string Executor, string? Destination)
;
public sealed record ParticipantAcceptanceDefinition(
    float BaseUtility,
    PredicateDefinition Eligibility,
    List<UtilityInputDefinition> UtilityInputs,
    List<TraitUtilityModifier> TraitModifiers);
public sealed record ParticipationDefinition(
    string Mode,
    int MinimumDurationMinutes,
    int MaximumDurationMinutes,
    int RejectionCooldownMinutes,
    ParticipantAcceptanceDefinition Acceptance);
public sealed record ActionDefinition(
    string Id,
    string Name,
    int Hash,
    ActivityDefinition Activity,
    float BaseUtility,
    PredicateDefinition Eligibility,
    List<UtilityInputDefinition> UtilityInputs,
    List<TraitUtilityModifier> TraitModifiers,
    ActionControlDefinition Controls,
    List<ActionEffectDefinition> Effects,
    TargetDefinition Target,
    ExecutorDefinition Execution,
    bool Fallback = false,
    ParticipationDefinition? Participation = null);
public sealed record SecretStateDefinition(string Id, string Name, int Hash);
public sealed record FactionDefinition(
    string Id, string Name, byte FactionId, string? LeaderJobId = null,
    string? ActivistJobId = null, FactionMetaGoalDefinition? MetaGoal = null,
    List<FactionGoalDefinition>? Goals = null);
public sealed record FactionMetaGoalDefinition(string Id, string Name, string Description);
public sealed record FactionGoalDefinition(
    string Id, string Name, string Action, string Metric, float Target,
    float DailyProgress, int Priority, List<string>? Prerequisites = null);
public sealed record AgentAttributeDefinition(string Id, float Min, float Max, float Average);
public sealed record JobDefinition(
    string Id,
    string Name,
    int Hash,
    int WorkStartMinute,
    int WorkEndMinute,
    List<int> WorkDays,
    string WorkplaceType,
    string Sector = "private",
    int WeeklyPay = 0,
    int Prestige = 0,
    string? SelectionMethod = null,
    string? AppointedByJobId = null,
    byte? FactionId = null,
    string? FactionRole = null);
public sealed class PoliticsDocument
{
    public int ElectionIntervalDays { get; init; }
    public int NominationDays { get; init; }
    public int PollOpeningMinute { get; init; }
    public int PollClosingMinute { get; init; }
    public string? PollingLocationId { get; init; }
    public string? PoliticalEngagementAttribute { get; init; }
    public string? MotivationAttribute { get; init; }
    public float CandidateEngagementWeight { get; init; }
    public float CandidateMotivationWeight { get; init; }
    public float CandidateThreshold { get; init; }
    public float CandidateThresholdVariation { get; init; }
    public float VoteEngagementWeight { get; init; }
    public float VoteMotivationWeight { get; init; }
    public float VoteSocialPressureWeight { get; init; }
    public float VoteBaseUtility { get; init; }
    public float VoteThreshold { get; init; }
    public float VoteThresholdVariation { get; init; }
    public float TravelPenaltyPerMinute { get; init; }
    public float MaximumTravelPenalty { get; init; }
    public float WorkOverlapPenalty { get; init; }
}
public sealed record PoliticsSettings(int ElectionIntervalDays, int NominationDays,
    int PollOpeningMinute, int PollClosingMinute, int PollingLocationId,
    int PoliticalEngagementAttributeIndex, int MotivationAttributeIndex,
    float CandidateEngagementWeight, float CandidateMotivationWeight,
    float CandidateThreshold, float CandidateThresholdVariation,
    float VoteEngagementWeight, float VoteMotivationWeight, float VoteSocialPressureWeight,
    float VoteBaseUtility, float VoteThreshold, float VoteThresholdVariation,
    float TravelPenaltyPerMinute, float MaximumTravelPenalty, float WorkOverlapPenalty);
public sealed record WorldLocationDefinition(string Id, string Name, int Hash, string Type);
public sealed record WorldConnectionDefinition(string From, string To, int TravelMinutes);

public sealed class LodDocument
{
    public bool Enabled { get; init; }
    public bool Tier3Enabled { get; init; }
    public Tier2LodDocument? Tier2 { get; init; }
    public string? DemotionPolicy { get; init; }
    public Tier3LodDocument? Tier3 { get; init; }
}

public sealed class Tier2LodDocument
{
    public int DecisionIntervalMinutes { get; init; }
    public List<string>? RelatedBy { get; init; }
}

public sealed class Tier3LodDocument
{
    public int ShardCount { get; init; }
    public List<Tier3RoutineSegmentDocument>? Workday { get; init; }
    public List<Tier3RoutineSegmentDocument>? NonWorkday { get; init; }
    public List<TraitDurationModifierDocument>? TraitDurationModifiers { get; init; }
}

// These names are authoring-only. Tier 3 systems consume the compiled enum
// representation below and never decide behaviour from an intent ID string.
public sealed class Tier3RoutineSegmentDocument
{
    public string? Id { get; init; }
    public string? Intent { get; init; }
    public string? Kind { get; init; }
    public string? Location { get; init; }
    public int? FixedMinutes { get; init; }
    public bool FillRemaining { get; init; }
    public string? EffectRole { get; init; }
}

public sealed class TraitDurationModifierDocument
{
    public string? Trait { get; init; }
    public string? SegmentId { get; init; }
    public int Minutes { get; init; }
}

public enum AgentRelationKind : byte
{
    Social,
    NetworkSupervisor,
    NetworkDirectReport
}

public enum AgentDemotionPolicy : byte
{
    EndOfDay
}

public enum CoarseRoutineSegmentKind : byte { Fixed, JobWork, CommuteToWork, CommuteHome }
public enum CoarseRoutineLocation : byte { Home, Work }
public sealed record CompiledCoarseRoutineSegment(
    string Id, ushort TemplateIndex, ushort RuntimeIndex, int IntentHash, CoarseRoutineSegmentKind Kind,
    CoarseRoutineLocation Location, int FixedMinutes, bool FillRemaining, EffectSubject EffectRole);
public sealed record CompiledTraitDurationModifier(long TraitBit, ushort SegmentIndex, int Minutes);
public sealed record CompiledTier3Routines(
    CompiledCoarseRoutineSegment[] Workday, CompiledCoarseRoutineSegment[] NonWorkday,
    CompiledTraitDurationModifier[] TraitDurationModifiers);

// Runtime settings contain enums rather than authoring strings, so simulation
// code cannot silently acquire a second interpretation of the JSON contract.
public sealed record AgentLodSettings(
    bool Enabled,
    bool Tier3Enabled,
    int Tier2DecisionIntervalMinutes,
    IReadOnlyList<AgentRelationKind> RelatedBy,
    AgentDemotionPolicy DemotionPolicy,
    int Tier3ShardCount,
    CompiledTier3Routines Tier3Routines);

public sealed class AgentAttributeSchema
{
    private readonly Dictionary<string, int> _indices;

    internal AgentAttributeSchema(IReadOnlyList<AgentAttributeDefinition> definitions)
    {
        Definitions = definitions;
        _indices = definitions
            .Select((definition, index) => new { definition.Id, index })
            .ToDictionary(item => item.Id, item => item.index, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AgentAttributeDefinition> Definitions { get; }

    public int Count => Definitions.Count;

    public int GetIndex(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _indices.TryGetValue(id, out var index)
            ? index
            : throw new KeyNotFoundException($"Agent attribute '{id}' is not defined in the schema.");
    }
}

public sealed class AgentSchemaDocument
{
    public List<AgentAttributeDefinition>? Attributes { get; init; }
}

public sealed class WorldDocument
{
    public List<WorldLocationDefinition>? Locations { get; init; }
    public List<WorldConnectionDefinition>? Connections { get; init; }
}

public sealed class ContentCatalog
{
    private ContentCatalog(
        IReadOnlyList<TraitDefinition> traits,
        IReadOnlyList<ActionDefinition> actions,
        CompiledIntentCatalog intents,
        IReadOnlyList<SecretStateDefinition> secretStates,
        IReadOnlyList<FactionDefinition> factions,
        AgentAttributeSchema agentAttributes,
        IReadOnlyList<JobDefinition> jobs,
        WorldTopology world,
        AgentNetworkCatalog networks,
        AgentLodSettings lod,
        PoliticsSettings politics)
    {
        Traits = traits;
        Actions = actions;
        Intents = intents;
        SecretStates = secretStates;
        Factions = factions;
        AgentAttributes = agentAttributes;
        Jobs = jobs;
        World = world;
        Networks = networks;
        Lod = lod;
        Politics = politics;
        AllTraitBits = traits.Aggregate(0L, (mask, trait) => mask | trait.Bit);
    }

    public IReadOnlyList<TraitDefinition> Traits { get; }
    public IReadOnlyList<ActionDefinition> Actions { get; }
    public CompiledIntentCatalog Intents { get; }
    public IReadOnlyList<SecretStateDefinition> SecretStates { get; }
    public IReadOnlyList<FactionDefinition> Factions { get; }
    public AgentAttributeSchema AgentAttributes { get; }
    public IReadOnlyList<JobDefinition> Jobs { get; }
    public WorldTopology World { get; }
    public AgentNetworkCatalog Networks { get; }
    public AgentLodSettings Lod { get; }
    public PoliticsSettings Politics { get; }
    public long AllTraitBits { get; }

    public ActivityDefinition GetActivity(int hash) => Actions
        .Select(action => action.Activity)
        .FirstOrDefault(activity => activity.Hash == hash)
        ?? throw new KeyNotFoundException($"Activity type hash '{hash}' is not defined in the content catalog.");

    public static ContentCatalog Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var traits = LoadFile<TraitDefinition>(directory, "traits.json", options);
        var actions = LoadFile<ActionDefinition>(directory, "actions.json", options);
        var secretStates = LoadFile<SecretStateDefinition>(directory, "secret-states.json", options);
        var factions = LoadFile<FactionDefinition>(directory, "factions.json", options);
        var schemaDocument = LoadObject<AgentSchemaDocument>(directory, "agent-schema.json", options);
        var jobs = LoadFile<JobDefinition>(directory, "jobs.json", options);
        var worldDocument = LoadObject<WorldDocument>(directory, "world.json", options);
        var lodDocument = LoadObject<LodDocument>(directory, "lod.json", options);
        var politicsDocument = LoadObject<PoliticsDocument>(directory, "politics.json", options);
        var networksPath = Path.Combine(directory, "networks.json");
        if (!File.Exists(networksPath))
            throw new FileNotFoundException($"Required content file was not found: {networksPath}", networksPath);

        ValidateSecretStates(secretStates);
        var agentAttributes = Validate(traits, actions, factions, schemaDocument.Attributes);
        var world = ValidateWorld(jobs, worldDocument.Locations, worldDocument.Connections);
        ValidateFactions(factions, jobs);
        var networks = AgentNetworkCatalog.Load(networksPath, options);
        var intents = IntentCompiler.Compile(actions, traits, agentAttributes, networks);
        var lod = ValidateLod(lodDocument, intents, traits, world, jobs);
        var politics = ValidatePolitics(politicsDocument, agentAttributes, world, jobs);
        return new ContentCatalog(traits, actions, intents, secretStates, factions, agentAttributes, jobs, world, networks, lod, politics);
    }

    private static void ValidateFactions(IReadOnlyList<FactionDefinition> factions, IReadOnlyList<JobDefinition> jobs)
    {
        var jobsById = jobs.ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var factionJob in jobs.Where(job => job.FactionId is not null))
        {
            var owner = factions.SingleOrDefault(faction => faction.FactionId == factionJob.FactionId);
            if (owner is null || (factionJob.FactionRole == "leader" &&
                    !string.Equals(owner.LeaderJobId, factionJob.Id, StringComparison.OrdinalIgnoreCase)) ||
                (factionJob.FactionRole == "activist" &&
                    !string.Equals(owner.ActivistJobId, factionJob.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"jobs.json faction job '{factionJob.Id}' is not referenced by its owning faction.");
        }
        var goalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var faction in factions)
        {
            var path = $"factions.json:{faction.Id}";
            if (string.IsNullOrWhiteSpace(faction.Id) || string.IsNullOrWhiteSpace(faction.Name) ||
                string.IsNullOrWhiteSpace(faction.LeaderJobId) || string.IsNullOrWhiteSpace(faction.ActivistJobId) ||
                faction.MetaGoal is null || string.IsNullOrWhiteSpace(faction.MetaGoal.Id) ||
                string.IsNullOrWhiteSpace(faction.MetaGoal.Name) || faction.Goals is null || faction.Goals.Count == 0)
                throw new InvalidDataException($"{path} must define its elected leader job, activist job, meta goal, and subgoals.");
            if (!jobsById.TryGetValue(faction.LeaderJobId, out var leader) || leader.SelectionMethod != "elected" ||
                leader.FactionId != faction.FactionId || leader.FactionRole != "leader")
                throw new InvalidDataException($"{path}.leaderJobId must reference this faction's elected leader job.");
            if (!jobsById.TryGetValue(faction.ActivistJobId, out var activist) || activist.SelectionMethod is not null ||
                activist.FactionId != faction.FactionId || activist.FactionRole != "activist")
                throw new InvalidDataException($"{path}.activistJobId must reference this faction's non-elected activist job.");
            if (faction.Goals.Any(goal => string.IsNullOrWhiteSpace(goal.Id) || !goalIds.Add(goal.Id)))
                throw new InvalidDataException($"{path}.goals must have non-empty IDs that are unique across factions.");
            foreach (var goal in faction.Goals)
            {
                if (goal.Action is not ("recruit" or "organize" or "campaign" or "office-seeking" or "govern") ||
                    goal.Metric is not ("members" or "organization" or "support" or "control") ||
                    !float.IsFinite(goal.Target) || goal.Target <= 0 || !float.IsFinite(goal.DailyProgress) ||
                    goal.DailyProgress <= 0 || goal.Priority < 0)
                    throw new InvalidDataException($"{path}.goals[{goal.Id}] has an unsupported action/metric or invalid target, progress, or priority.");
            }
        }
        foreach (var faction in factions)
        foreach (var goal in faction.Goals!)
            if (goal.Prerequisites?.Any(id => !faction.Goals.Any(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))) == true)
                throw new InvalidDataException($"factions.json:{faction.Id}.goals[{goal.Id}] references an unknown prerequisite.");
        foreach (var faction in factions)
        {
            var goals = faction.Goals!.ToDictionary(goal => goal.Id, StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool HasCycle(string id)
            {
                if (visiting.Contains(id)) return true;
                if (!visited.Add(id)) return false;
                visiting.Add(id);
                if (goals[id].Prerequisites?.Any(HasCycle) == true) return true;
                visiting.Remove(id);
                return false;
            }
            if (goals.Keys.Any(HasCycle))
                throw new InvalidDataException($"factions.json:{faction.Id}.goals contains a prerequisite cycle.");
        }
    }

    private static PoliticsSettings ValidatePolitics(PoliticsDocument document, AgentAttributeSchema attributes,
        WorldTopology world, IReadOnlyList<JobDefinition> jobs)
    {
        if (document.ElectionIntervalDays <= 1 || document.NominationDays <= 0 ||
            document.NominationDays >= document.ElectionIntervalDays)
            throw new InvalidDataException("politics.json must define a positive nomination window shorter than the election interval.");
        if (document.PollOpeningMinute < 0 || document.PollClosingMinute > SimulationDefaults.SimulationMinutesPerDay ||
            document.PollOpeningMinute >= document.PollClosingMinute)
            throw new InvalidDataException("politics.json polling hours must form a valid same-day interval.");
        if (string.IsNullOrWhiteSpace(document.PollingLocationId))
            throw new InvalidDataException("politics.json:pollingLocationId is required.");
        var pollingLocation = world.Locations.SingleOrDefault(location =>
            string.Equals(location.Id, document.PollingLocationId, StringComparison.OrdinalIgnoreCase));
        if (pollingLocation is null)
            throw new InvalidDataException($"politics.json references unknown polling location '{document.PollingLocationId}'.");
        foreach (var home in world.Locations.Where(location =>
                     string.Equals(location.Type, SimulationDefaults.ResidentialLocationType, StringComparison.OrdinalIgnoreCase)))
        {
            if (world.FindShortestRoute(home.Hash, pollingLocation.Hash) is null)
                throw new InvalidDataException($"Polling location '{pollingLocation.Id}' is unreachable from residential location '{home.Id}'.");
        }
        if (string.IsNullOrWhiteSpace(document.PoliticalEngagementAttribute) ||
            string.IsNullOrWhiteSpace(document.MotivationAttribute))
            throw new InvalidDataException("politics.json must name political engagement and motivation attributes.");
        int engagementIndex;
        int motivationIndex;
        try
        {
            engagementIndex = attributes.GetIndex(document.PoliticalEngagementAttribute);
            motivationIndex = attributes.GetIndex(document.MotivationAttribute);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException($"politics.json references an unknown agent attribute: {exception.Message}", exception);
        }
        if (!jobs.Any(job => job.SelectionMethod == "elected"))
            throw new InvalidDataException("jobs.json must define at least one elected political office.");
        var values = new[] { document.CandidateEngagementWeight, document.CandidateMotivationWeight,
            document.CandidateThreshold, document.CandidateThresholdVariation,
            document.VoteEngagementWeight, document.VoteMotivationWeight, document.VoteSocialPressureWeight,
            document.VoteBaseUtility, document.VoteThreshold, document.VoteThresholdVariation,
            document.TravelPenaltyPerMinute, document.MaximumTravelPenalty, document.WorkOverlapPenalty };
        if (values.Any(value => !float.IsFinite(value)) || document.CandidateEngagementWeight < 0 ||
            document.CandidateMotivationWeight < 0 || document.CandidateThreshold is < 0 or > 100 ||
            document.CandidateThresholdVariation is < 0 or > 100 ||
            document.CandidateEngagementWeight + document.CandidateMotivationWeight <= 0 ||
            document.VoteEngagementWeight < 0 ||
            document.VoteMotivationWeight < 0 || document.VoteSocialPressureWeight < 0 ||
            document.VoteEngagementWeight + document.VoteMotivationWeight + document.VoteSocialPressureWeight <= 0 ||
            document.VoteBaseUtility < 0 || document.VoteThreshold is < 0 or > 100 ||
            document.VoteThresholdVariation is < 0 or > 100 || document.TravelPenaltyPerMinute < 0 ||
            document.MaximumTravelPenalty < 0 || document.WorkOverlapPenalty < 0)
            throw new InvalidDataException("politics.json contains invalid candidate, turnout, access, or work-pressure weights.");
        return new PoliticsSettings(document.ElectionIntervalDays, document.NominationDays,
            document.PollOpeningMinute, document.PollClosingMinute, pollingLocation.Hash,
            engagementIndex, motivationIndex, document.CandidateEngagementWeight, document.CandidateMotivationWeight,
            document.CandidateThreshold, document.CandidateThresholdVariation, document.VoteEngagementWeight,
            document.VoteMotivationWeight, document.VoteSocialPressureWeight, document.VoteBaseUtility,
            document.VoteThreshold, document.VoteThresholdVariation, document.TravelPenaltyPerMinute,
            document.MaximumTravelPenalty, document.WorkOverlapPenalty);
    }

    private static AgentLodSettings ValidateLod(LodDocument document, CompiledIntentCatalog intents,
        IReadOnlyList<TraitDefinition> traits, WorldTopology world, IReadOnlyList<JobDefinition> jobs)
    {
        if (document.Tier2 is null)
            throw new InvalidDataException("lod.json:tier2 is required.");
        if (document.Tier2.DecisionIntervalMinutes <= 0)
            throw new InvalidDataException("lod.json:tier2.decisionIntervalMinutes must be positive.");
        if (document.Tier2.RelatedBy is null || document.Tier2.RelatedBy.Count == 0)
            throw new InvalidDataException("lod.json:tier2.relatedBy must contain the supported relationship scopes.");

        var relations = new List<AgentRelationKind>(document.Tier2.RelatedBy.Count);
        for (var index = 0; index < document.Tier2.RelatedBy.Count; index++)
        {
            var relation = document.Tier2.RelatedBy[index];
            relations.Add(relation switch
            {
                "social" => AgentRelationKind.Social,
                "networkSupervisor" => AgentRelationKind.NetworkSupervisor,
                "networkDirectReport" => AgentRelationKind.NetworkDirectReport,
                _ => throw new InvalidDataException(
                    $"lod.json:tier2.relatedBy[{index}] has unsupported relationship '{relation}'.")
            });
        }

        if (relations.Count != 3 || relations.Distinct().Count() != 3)
            throw new InvalidDataException(
                "lod.json:tier2.relatedBy must contain each of social, networkSupervisor, and networkDirectReport exactly once.");

        var demotionPolicy = document.DemotionPolicy switch
        {
            "endOfDay" => AgentDemotionPolicy.EndOfDay,
            _ => throw new InvalidDataException(
                $"lod.json:demotionPolicy has unsupported policy '{document.DemotionPolicy}'.")
        };

        if (document.Tier3 is null)
            throw new InvalidDataException("lod.json:tier3 is required.");
        if (document.Tier3.ShardCount <= 0)
            throw new InvalidDataException("lod.json:tier3.shardCount must be positive.");

        var routines = CompileTier3Routines(document.Tier3, intents, traits, world, jobs);
        return new AgentLodSettings(document.Enabled, document.Tier3Enabled,
            document.Tier2.DecisionIntervalMinutes, relations.AsReadOnly(), demotionPolicy,
            document.Tier3.ShardCount, routines);
    }

    private static CompiledTier3Routines CompileTier3Routines(Tier3LodDocument tier3,
        CompiledIntentCatalog intents, IReadOnlyList<TraitDefinition> traits, WorldTopology world,
        IReadOnlyList<JobDefinition> jobs)
    {
        if (tier3.Workday is null || tier3.NonWorkday is null || tier3.TraitDurationModifiers is null)
            throw new InvalidDataException("lod.json:tier3 must define workday, nonWorkday, and traitDurationModifiers.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workday = CompileRoutine(tier3.Workday, "workday", intents, ids, 0);
        var nonWorkday = CompileRoutine(tier3.NonWorkday, "nonWorkday", intents, ids, workday.Length);
        ValidateCommuteWindows(jobs);
        var segments = workday.Concat(nonWorkday).ToArray();
        var indexById = segments.Select((segment, index) => new { segment.Id, Index = (ushort)index })
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.OrdinalIgnoreCase);
        var traitBits = traits.ToDictionary(trait => trait.Id, trait => trait.Bit, StringComparer.OrdinalIgnoreCase);
        var modifiers = tier3.TraitDurationModifiers.Select((modifier, index) =>
        {
            var path = $"lod.json:tier3.traitDurationModifiers[{index}]";
            if (string.IsNullOrWhiteSpace(modifier.Trait) || !traitBits.TryGetValue(modifier.Trait, out var bit))
                throw new InvalidDataException($"{path}.trait references unknown trait '{modifier.Trait}'.");
            if (string.IsNullOrWhiteSpace(modifier.SegmentId) || !indexById.TryGetValue(modifier.SegmentId, out var segmentIndex))
                throw new InvalidDataException($"{path}.segmentId references unknown segment '{modifier.SegmentId}'.");
            if (modifier.Minutes == 0)
                throw new InvalidDataException($"{path}.minutes must be non-zero.");
            return new CompiledTraitDurationModifier(bit, segmentIndex, modifier.Minutes);
        }).ToArray();
        return new CompiledTier3Routines(workday, nonWorkday, modifiers);
    }

    private static CompiledCoarseRoutineSegment[] CompileRoutine(IReadOnlyList<Tier3RoutineSegmentDocument> source,
        string routineName, CompiledIntentCatalog intents, HashSet<string> allIds, int templateOffset)
    {
        if (source.Count == 0) throw new InvalidDataException($"lod.json:tier3.{routineName} must not be empty.");
        var result = new CompiledCoarseRoutineSegment[source.Count];
        var fillCount = 0; var fixedMinutes = 0; var jobCount = 0; var outboundCount = 0; var returnCount = 0;
        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index]; var path = $"lod.json:tier3.{routineName}[{index}]";
            if (string.IsNullOrWhiteSpace(item.Id) || !allIds.Add(item.Id))
                throw new InvalidDataException($"{path}.id must be non-empty and globally unique.");
            if (string.IsNullOrWhiteSpace(item.Intent) || !intents.All.Any(intent => string.Equals(intent.Id, item.Intent, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{path}.intent references unknown intent '{item.Intent}'.");
            var intent = intents.All.Single(intent => string.Equals(intent.Id, item.Intent, StringComparison.OrdinalIgnoreCase));
            var kind = item.Kind switch
            {
                "fixed" => CoarseRoutineSegmentKind.Fixed,
                "jobWork" => CoarseRoutineSegmentKind.JobWork,
                "commuteToWork" => CoarseRoutineSegmentKind.CommuteToWork,
                "commuteHome" => CoarseRoutineSegmentKind.CommuteHome,
                _ => throw new InvalidDataException($"{path}.kind is unsupported.")
            };
            var location = item.Location switch
            {
                "home" => CoarseRoutineLocation.Home,
                "work" => CoarseRoutineLocation.Work,
                _ => throw new InvalidDataException($"{path}.location must be 'home' or 'work'.")
            };
            var role = item.EffectRole switch
            {
                "initiator" => EffectSubject.Initiator,
                "participant" => EffectSubject.Participant,
                _ => throw new InvalidDataException($"{path}.effectRole must be 'initiator' or 'participant'.")
            };
            if (!intent.Effects.Any(effect => effect.Subject == role))
                throw new InvalidDataException($"{path}.effectRole has no matching effect on intent '{item.Intent}'.");
            if (item.FillRemaining) fillCount++;
            if (item.FixedMinutes is < 0) throw new InvalidDataException($"{path}.fixedMinutes cannot be negative.");
            if (kind == CoarseRoutineSegmentKind.Fixed && !item.FillRemaining && item.FixedMinutes is null)
                throw new InvalidDataException($"{path}.fixedMinutes is required for a fixed segment unless fillRemaining is true.");
            if (kind != CoarseRoutineSegmentKind.Fixed && (item.FixedMinutes is not null || item.FillRemaining))
                throw new InvalidDataException($"{path} job and commute segments cannot define fixedMinutes or fillRemaining.");
            fixedMinutes += item.FixedMinutes ?? 0;
            jobCount += kind == CoarseRoutineSegmentKind.JobWork ? 1 : 0;
            outboundCount += kind == CoarseRoutineSegmentKind.CommuteToWork ? 1 : 0;
            returnCount += kind == CoarseRoutineSegmentKind.CommuteHome ? 1 : 0;
            result[index] = new(item.Id, checked((ushort)(templateOffset + index)), intent.RuntimeIndex, intent.Hash, kind, location, item.FixedMinutes ?? 0, item.FillRemaining, role);
        }
        if (fillCount != 1) throw new InvalidDataException($"lod.json:tier3.{routineName} must contain exactly one fillRemaining segment.");
        if (fixedMinutes >= SimulationDefaults.SimulationMinutesPerDay)
            throw new InvalidDataException($"lod.json:tier3.{routineName} fixed durations leave no minute for fillRemaining.");
        var isWorkday = routineName == "workday";
        if (isWorkday && (jobCount != 1 || outboundCount != 1 || returnCount != 1))
            throw new InvalidDataException("lod.json:tier3.workday must contain one jobWork, commuteToWork, and commuteHome segment.");
        if (!isWorkday && (jobCount != 0 || outboundCount != 0 || returnCount != 0))
            throw new InvalidDataException("lod.json:tier3.nonWorkday cannot contain job or commute segments.");
        return result;
    }

    private static void ValidateCommuteWindows(IReadOnlyList<JobDefinition> jobs)
    {
        foreach (var job in jobs)
        {
            // A route is assignment-specific and therefore cannot be rejected
            // at catalog load. The profile compiler validates its exact length;
            // this check still rejects a job interval with no possible commute.
            if (job.WorkStartMinute <= 0 || job.WorkEndMinute >= SimulationDefaults.SimulationMinutesPerDay)
                throw new InvalidDataException($"lod.json:tier3 commute cannot fit around job '{job.Id}'.");
        }
    }

    private static IReadOnlyList<T> LoadFile<T>(
        string directory,
        string fileName,
        JsonSerializerOptions options)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required content file was not found: {path}", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<T>>(stream, options)
            ?? throw new InvalidDataException($"Content file is empty or invalid: {path}");
    }

    private static T LoadObject<T>(
        string directory,
        string fileName,
        JsonSerializerOptions options)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required content file was not found: {path}", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, options)
            ?? throw new InvalidDataException($"Content file is empty or invalid: {path}");
    }

    private static AgentAttributeSchema Validate(
        IReadOnlyList<TraitDefinition> traits,
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyList<FactionDefinition> factions,
        IReadOnlyList<AgentAttributeDefinition>? attributeDefinitions)
    {
        if (traits.Count == 0 || actions.Count == 0 || factions.Count == 0)
        {
            throw new InvalidDataException("Traits, actions, and factions must each contain at least one definition.");
        }

        if (attributeDefinitions is null || attributeDefinitions.Count == 0)
        {
            throw new InvalidDataException("The agent attribute schema must contain at least one attribute.");
        }

        var traitBits = new HashSet<long>();
        var traitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trait in traits)
        {
            if (string.IsNullOrWhiteSpace(trait.Id) || !traitIds.Add(trait.Id))
            {
                throw new InvalidDataException($"Trait IDs must be non-empty and unique; '{trait.Id}' is invalid or duplicated.");
            }

            if (trait.Bit <= 0 || (trait.Bit & (trait.Bit - 1)) != 0 || !traitBits.Add(trait.Bit))
            {
                throw new InvalidDataException($"Trait '{trait.Id}' must have a unique positive single-bit value.");
            }

            if (!float.IsFinite(trait.Prevalence) || trait.Prevalence is < 0f or > 1f)
            {
                throw new InvalidDataException($"Trait '{trait.Id}' prevalence must be a finite value between 0 and 1.");
            }
        }

        if (factions.Select(faction => faction.FactionId).Distinct().Count() != factions.Count)
        {
            throw new InvalidDataException("Faction IDs must be unique.");
        }

        if (actions.Select(action => action.Hash).Distinct().Count() != actions.Count)
        {
            throw new InvalidDataException("Action hashes must be unique.");
        }

        ValidateActions(actions, traits, attributeDefinitions);

        var attributeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributeDefinitions)
        {
            if (string.IsNullOrWhiteSpace(attribute.Id) || !attributeIds.Add(attribute.Id))
            {
                throw new InvalidDataException($"Agent attribute IDs must be non-empty and unique; '{attribute.Id}' is invalid or duplicated.");
            }

            if (!float.IsFinite(attribute.Min) || !float.IsFinite(attribute.Max) || !float.IsFinite(attribute.Average))
            {
                throw new InvalidDataException($"Agent attribute '{attribute.Id}' must contain only finite numeric values.");
            }

            if (attribute.Min > attribute.Max || attribute.Average < attribute.Min || attribute.Average > attribute.Max)
            {
                throw new InvalidDataException($"Agent attribute '{attribute.Id}' must satisfy min <= average <= max.");
            }
        }

        var schema = new AgentAttributeSchema(attributeDefinitions);
        _ = schema.GetIndex("fatigue");
        _ = schema.GetIndex("stress");
        return schema;
    }

    private static void ValidateActions(
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyList<TraitDefinition> traits,
        IReadOnlyList<AgentAttributeDefinition> attributes)
    {
        var activityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activityHashes = new HashSet<int>();
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Id) || string.IsNullOrWhiteSpace(action.Name) ||
                !float.IsFinite(action.BaseUtility) || action.Activity is null || action.Eligibility is null ||
                action.UtilityInputs is null || action.TraitModifiers is null || action.Controls is null || action.Effects is null || action.Target is null || action.Execution is null)
                throw new InvalidDataException($"Action '{action.Id}' has an invalid decision definition.");
            if (string.IsNullOrWhiteSpace(action.Activity.Id) || string.IsNullOrWhiteSpace(action.Activity.Name) ||
                action.Activity.Hash == 0 || !activityIds.Add(action.Activity.Id) || !activityHashes.Add(action.Activity.Hash))
                throw new InvalidDataException($"Action '{action.Id}' must define a unique, non-zero activity ID and hash with a display name.");
            if (action.Controls.MinimumCommitmentMinutes < 0 || action.Controls.CooldownMinutes < 0 ||
                !float.IsFinite(action.Controls.SwitchingThreshold) || action.Controls.SwitchingThreshold < 0 ||
                !float.IsFinite(action.Controls.UrgentPreemptionThreshold))
                throw new InvalidDataException($"Action '{action.Id}' has invalid controls.");
            foreach (var input in action.UtilityInputs)
            {
                if (input.Expression is null || !float.IsFinite(input.Weight) || input.Curve is null || input.Curve.Count < 2 ||
                    input.Curve.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)) ||
                    input.Curve.Select(point => point.X).Zip(input.Curve.Skip(1), (x, next) => next.X > x).Any(increasing => !increasing))
                    throw new InvalidDataException($"Action '{action.Id}' has an invalid utility input.");
            }
            if (action.TraitModifiers.Any(modifier => !float.IsFinite(modifier.Modifier)))
                throw new InvalidDataException($"Action '{action.Id}' has a non-finite trait modifier.");
            if (action.Effects.Any(effect => !float.IsFinite(effect.PerMinute)))
                throw new InvalidDataException($"Action '{action.Id}' has a non-finite effect rate.");
            if (action.Participation is { } participation)
            {
                var acceptance = participation.Acceptance;
                if (acceptance is null || acceptance.Eligibility is null || acceptance.UtilityInputs is null ||
                    acceptance.TraitModifiers is null || !float.IsFinite(acceptance.BaseUtility))
                    throw new InvalidDataException($"Action '{action.Id}' has an invalid participation definition.");
                foreach (var input in acceptance.UtilityInputs)
                {
                    if (input.Expression is null || !float.IsFinite(input.Weight) || input.Curve is null || input.Curve.Count < 2 ||
                        input.Curve.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)) ||
                        input.Curve.Select(point => point.X).Zip(input.Curve.Skip(1), (x, next) => next.X > x).Any(increasing => !increasing))
                        throw new InvalidDataException($"Action '{action.Id}' has an invalid participant utility input.");
                }
                if (acceptance.TraitModifiers.Any(modifier => !float.IsFinite(modifier.Modifier)))
                    throw new InvalidDataException($"Action '{action.Id}' has a non-finite participant trait modifier.");
            }
        }
    }


    private static void ValidateSecretStates(IReadOnlyList<SecretStateDefinition> secretStates)
    {
        if (secretStates.Count == 0)
        {
            throw new InvalidDataException("At least one secret-state definition is required.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<int>();
        foreach (var secretState in secretStates)
        {
            if (string.IsNullOrWhiteSpace(secretState.Id) || !ids.Add(secretState.Id))
            {
                throw new InvalidDataException(
                    $"Secret-state IDs must be non-empty and unique; '{secretState.Id}' is invalid or duplicated.");
            }

            if (string.IsNullOrWhiteSpace(secretState.Name))
            {
                throw new InvalidDataException($"Secret state '{secretState.Id}' must have a name.");
            }

            if (!hashes.Add(secretState.Hash))
            {
                throw new InvalidDataException(
                    $"Secret-state hashes must be unique; '{secretState.Hash}' is duplicated.");
            }
        }

        // Hash zero keeps a default-initialized AgentState safe even before a
        // system or content assignment supplies a covert activity.
        var noneStates = secretStates
            .Where(secretState => string.Equals(secretState.Id, "none", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (noneStates.Length != 1 || noneStates[0].Hash != 0)
        {
            throw new InvalidDataException("A unique 'none' secret-state definition with hash 0 is required.");
        }
    }

    private static WorldTopology ValidateWorld(
        IReadOnlyList<JobDefinition> jobs,
        IReadOnlyList<WorldLocationDefinition>? locations,
        IReadOnlyList<WorldConnectionDefinition>? connections)
    {
        if (jobs.Count == 0)
        {
            throw new InvalidDataException("At least one job definition is required.");
        }

        var jobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobHashes = new HashSet<int>();
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id) || !jobIds.Add(job.Id))
            {
                throw new InvalidDataException($"Job IDs must be non-empty and unique; '{job.Id}' is invalid or duplicated.");
            }

            if (!jobHashes.Add(job.Hash))
            {
                throw new InvalidDataException($"Job hashes must be unique; '{job.Hash}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(job.Name) || string.IsNullOrWhiteSpace(job.WorkplaceType))
            {
                throw new InvalidDataException($"Job '{job.Id}' must have a name and workplace type.");
            }

            if (job.Sector is not ("public" or "private") || job.WeeklyPay < 0 || job.Prestige is < 1 or > 100)
            {
                throw new InvalidDataException($"Job '{job.Id}' must use the public or private sector, non-negative weekly pay, and prestige from 1 through 100.");
            }
            if (job.SelectionMethod is not null && job.FactionId is null && job.Sector != "public")
                throw new InvalidDataException($"Civic political job '{job.Id}' must belong to the public sector.");
            if (job.FactionId is not null && job.FactionRole is null)
                throw new InvalidDataException($"Faction job '{job.Id}' must define factionRole.");
            if (job.FactionRole is not null && job.FactionRole is not ("leader" or "activist"))
                throw new InvalidDataException($"Job '{job.Id}' factionRole must be leader or activist.");

            if (job.SelectionMethod is not (null or "elected" or "appointed"))
                throw new InvalidDataException($"Job '{job.Id}' selectionMethod must be null, elected, or appointed.");
            if (job.SelectionMethod == "appointed" && string.IsNullOrWhiteSpace(job.AppointedByJobId))
                throw new InvalidDataException($"Appointed job '{job.Id}' must name appointedByJobId.");
            if (job.SelectionMethod != "appointed" && job.AppointedByJobId is not null)
                throw new InvalidDataException($"Job '{job.Id}' may only name appointedByJobId when its selectionMethod is appointed.");

            if (job.WorkStartMinute < 0 || job.WorkEndMinute > SimulationDefaults.SimulationMinutesPerDay ||
                job.WorkStartMinute >= job.WorkEndMinute)
            {
                throw new InvalidDataException($"Job '{job.Id}' must define a non-overnight interval within a day.");
            }

            if (job.WorkDays is null || job.WorkDays.Count == 0 ||
                job.WorkDays.Any(day => day < 1 || day > SimulationDefaults.DaysPerWeek) ||
                job.WorkDays.Distinct().Count() != job.WorkDays.Count)
            {
                throw new InvalidDataException($"Job '{job.Id}' must define unique workdays from 1 through 7.");
            }
        }

        if (locations is null || locations.Count == 0)
        {
            throw new InvalidDataException("The world must contain at least one location.");
        }

        var locationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locationHashes = new HashSet<int>();
        foreach (var location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.Id) || !locationIds.Add(location.Id))
            {
                throw new InvalidDataException($"Location IDs must be non-empty and unique; '{location.Id}' is invalid or duplicated.");
            }

            if (!locationHashes.Add(location.Hash))
            {
                throw new InvalidDataException($"Location hashes must be unique; '{location.Hash}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(location.Name) || string.IsNullOrWhiteSpace(location.Type))
            {
                throw new InvalidDataException($"Location '{location.Id}' must have a name and type.");
            }
        }

        if (!locations.Any(location => string.Equals(
                location.Type,
                SimulationDefaults.ResidentialLocationType,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The world must contain at least one residential location.");
        }

        var locationTypes = locations
            .Select(location => location.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (!locationTypes.Contains(job.WorkplaceType))
            {
                throw new InvalidDataException($"Job '{job.Id}' requires unavailable workplace type '{job.WorkplaceType}'.");
            }
        }
        foreach (var job in jobs.Where(job => job.SelectionMethod == "appointed"))
        {
            var appointingJob = jobs.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, job.AppointedByJobId, StringComparison.OrdinalIgnoreCase));
            if (appointingJob?.SelectionMethod != "elected")
                throw new InvalidDataException($"Appointed job '{job.Id}' must reference an existing elected appointing office.");
        }

        if (connections is null || connections.Count == 0)
        {
            throw new InvalidDataException("The world must contain at least one connection.");
        }

        var locationById = locations.ToDictionary(location => location.Id, StringComparer.OrdinalIgnoreCase);
        var connectionPairs = new HashSet<(int From, int To)>();
        foreach (var connection in connections)
        {
            if (string.IsNullOrWhiteSpace(connection.From) || string.IsNullOrWhiteSpace(connection.To) ||
                string.Equals(connection.From, connection.To, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("World connections must connect two different locations.");
            }

            if (!locationById.TryGetValue(connection.From, out var from) ||
                !locationById.TryGetValue(connection.To, out var to))
            {
                throw new InvalidDataException($"World connection '{connection.From}' -> '{connection.To}' references an unknown location.");
            }

            if (connection.TravelMinutes <= 0)
            {
                throw new InvalidDataException("World connection travel durations must be positive.");
            }

            var pair = from.Hash < to.Hash
                ? (from.Hash, to.Hash)
                : (to.Hash, from.Hash);
            if (!connectionPairs.Add(pair))
            {
                throw new InvalidDataException($"World connection '{connection.From}' -> '{connection.To}' is duplicated.");
            }
        }

        return new WorldTopology(locations, connections);
    }
}
