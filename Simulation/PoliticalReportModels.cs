namespace ProxyState.Simulation;

/// <summary>A compact, stable record of one political decision or outcome.</summary>
public sealed record PoliticalDiagnosticEvent(
    int Day,
    int MinuteOfDay,
    string Type,
    int? AgentId = null,
    byte? FactionId = null,
    string? OfficeId = null,
    int? ElectionId = null,
    int? OtherAgentId = null,
    string? Detail = null);

public sealed record ElectionCandidateTally(int AgentId, byte FactionId, int Votes);
public sealed record ElectionOfficeResult(
    string OfficeId,
    byte? FactionId,
    int? WinnerAgentId,
    int? WinnerFactionId,
    int CandidateCount,
    int VoteCount,
    IReadOnlyList<ElectionCandidateTally> Tallies);
public sealed record ElectionDiagnosticResult(int ElectionId, int Day, int CandidateCount,
    int VoterCount, IReadOnlyList<ElectionOfficeResult> Offices);
