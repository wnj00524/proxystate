namespace ProxyState.Simulation;

public sealed record SurveyCategoryDefinition(string Id, string Label, float PopulationShare);
public sealed record SurveyProviderDefinition(string Id, string Name, string SamplingMethod,
    string Methodology, int AttemptCount, bool LikelyVoterOnly, int SeedOffset,
    float MaximumWeight, IReadOnlyList<string> SamplingStrata);
public sealed record PoliticalResearchSettings(int CadenceDays, int FieldworkDays,
    int CallStartMinute, int CallEndMinute, string CandidateOfficeId,
    IReadOnlyList<SurveyCategoryDefinition> AgeBands,
    IReadOnlyList<SurveyCategoryDefinition> Genders, IReadOnlyList<SurveyCategoryDefinition> EducationLevels,
    float BaseResponseProbability, float EngagementResponseWeight, float MotivationResponseWeight,
    float BaseUndecidedProbability, float LowEngagementUndecidedWeight,
    IReadOnlyList<SurveyProviderDefinition> Providers);

/// <summary>A published estimate with raw counts, weighted support, and a 95% sampling interval.</summary>
public sealed record PollEstimate(string Id, string Label, int RawResponses, float RawPercentage,
    float WeightedPercentage, float Lower95, float Upper95);
public sealed record CandidateSupportEstimate(string OfficeId, IReadOnlyList<PollEstimate> Candidates);
public sealed record OpinionPollSnapshot(int Wave, int StartDay, int PublishedDay, int CallsAttempted,
    int CompletedInterviews, int Nonresponse, int LikelyVoterInterviews, float EffectiveSampleSize,
    string Methodology, IReadOnlyList<PollEstimate> PartySupport,
    IReadOnlyList<CandidateSupportEstimate> CandidateSupport);
public sealed record ResearchProviderProjection(string ProviderId, string Name, string Methodology,
    string SamplingMethod, bool LikelyVoterOnly, OpinionPollSnapshot? Latest,
    IReadOnlyList<OpinionPollSnapshot> History);

internal readonly record struct SurveyResponse(byte PartyFactionId, int? CandidateAgentId,
    string? CandidateOfficeId, bool LikelyVoter);
