using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

public sealed record FactionSnapshot(string Id, string MetaGoalId, string? ActiveGoalId,
    int Members, float Organization, float PublicSupport, int InstitutionalControl,
    int Volunteers, int Activists, int Leaders, IReadOnlySet<string> CompletedGoals);

/// <summary>
/// Owns voluntary faction membership, activist hiring, and data-authored faction
/// strategy. It scans persistent identity state, so LOD never removes participants.
/// </summary>
public sealed class PoliticalFactionSystem
{
    private readonly EntityStore _store;
    private readonly ContentCatalog _catalog;
    private readonly AgentSocialIndexes _socialIndexes;
    private readonly AgentLodService? _lodService;
    private readonly Dictionary<string, FactionRuntime> _factions;
    private readonly Dictionary<string, JobDefinition> _jobs;
    private readonly Dictionary<int, JobDefinition> _jobsByHash;
    private readonly Dictionary<int, Entity> _agents = [];
    private int _lastUpdatedDay;
    private readonly List<PoliticalDiagnosticEvent> _events = [];

    public PoliticalFactionSystem(EntityStore store, ContentCatalog catalog,
        AgentSocialIndexes socialIndexes, AgentLodService? lodService = null)
    {
        _store = store;
        _catalog = catalog;
        _socialIndexes = socialIndexes;
        _lodService = lodService;
        _jobs = catalog.Jobs.ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);
        _jobsByHash = catalog.Jobs.ToDictionary(job => job.Hash);
        _factions = catalog.Factions.ToDictionary(faction => faction.Id,
            faction => new FactionRuntime(faction), StringComparer.OrdinalIgnoreCase);
        RefreshAgents();
    }

    public IReadOnlyList<FactionSnapshot> Snapshots => _factions.Values.OrderBy(value => value.Definition.FactionId)
        .Select(value => new FactionSnapshot(value.Definition.Id, value.Definition.MetaGoal!.Id,
            value.ActiveGoal?.Id, value.MemberCount, value.Organization, value.PublicSupport,
            value.InstitutionalControl, CountRole(value.Definition.FactionId, FactionMemberRole.Volunteer),
            CountRole(value.Definition.FactionId, FactionMemberRole.Activist),
            CountRole(value.Definition.FactionId, FactionMemberRole.Leader),
            new HashSet<string>(value.CompletedGoals, StringComparer.OrdinalIgnoreCase)))
        .ToArray();
    public IReadOnlyList<PoliticalDiagnosticEvent> Events => _events;

    /// <summary>Accepts an invitation and makes the agent a volunteer without changing their occupation.</summary>
    public bool AcceptRecruitment(int agentId, string factionId)
    {
        if (!_agents.TryGetValue(agentId, out var agent) || !_factions.TryGetValue(factionId, out var faction)) return false;
        ref var membership = ref agent.GetComponent<FactionParticipation>();
        if (membership.Role != FactionMemberRole.None) return false;
        membership.FactionId = faction.Definition.FactionId;
        membership.Role = FactionMemberRole.Volunteer;
        Log(_lastUpdatedDay, "membership-accepted", agentId, faction.Definition.FactionId);
        return true;
    }

    /// <summary>Leaving restores any faction occupation before clearing membership.</summary>
    public bool LeaveFaction(int agentId)
    {
        if (!_agents.TryGetValue(agentId, out var agent) ||
            !agent.TryGetComponent<FactionParticipation>(out var membership) || membership.Role == FactionMemberRole.None)
            return false;
        var formerFactionId = membership.FactionId;
        ReleaseFactionOccupation(agent, membership);
        membership.FactionId = byte.MaxValue;
        membership.Role = FactionMemberRole.None;
        membership.AppliedForStaff = false;
        membership.PreviousOccupationId = 0;
        agent.GetComponent<FactionParticipation>() = membership;
        Log(_lastUpdatedDay, "membership-left", agentId, formerFactionId);
        return true;
    }

    public void Update(int dayNumber)
    {
        if (dayNumber <= _lastUpdatedDay) return;
        _lastUpdatedDay = dayNumber;
        RefreshAgents();
        foreach (var runtime in _factions.Values.OrderBy(value => value.Definition.FactionId))
        {
            ReconcileRoles(runtime);
            runtime.MemberCount = CountMembers(runtime.Definition.FactionId);
            runtime.InstitutionalControl = CountControlledOffices(runtime.Definition.FactionId);
            SelectGoal(runtime);
            if (runtime.ActiveGoal is not null) ExecuteGoal(runtime, runtime.ActiveGoal, dayNumber);
            ConsiderApplications(runtime, dayNumber);
            HireActivists(runtime);
        }
    }

    private void ExecuteGoal(FactionRuntime faction, FactionGoalDefinition goal, int dayNumber)
    {
        switch (goal.Action)
        {
            case "recruit":
                Recruit(faction, Math.Max(1, (int)MathF.Floor(goal.DailyProgress)), dayNumber);
                break;
            case "organize":
                if (faction.MemberCount > 0)
                    faction.Organization = Math.Min(100, faction.Organization + goal.DailyProgress);
                break;
            case "campaign":
                if (faction.MemberCount > 0)
                    faction.PublicSupport = Math.Min(100, faction.PublicSupport + goal.DailyProgress);
                break;
            case "office-seeking":
            case "govern":
                // These goals are completed by winning public office; the data
                // target is checked against current elected and appointed control.
                break;
        }
        if (Metric(faction, goal.Metric) >= goal.Target && faction.CompletedGoals.Add(goal.Id))
            Log(dayNumber, "goal-completed", factionId: faction.Definition.FactionId,
                detail: $"goal={goal.Id};metric={goal.Metric};value={Metric(faction, goal.Metric):F2}");
        SelectGoal(faction);
    }

    private void Recruit(FactionRuntime faction, int count, int dayNumber)
    {
        var candidates = _agents.Values.Where(agent =>
                agent.GetComponent<FactionParticipation>().Role == FactionMemberRole.None &&
                agent.GetComponent<PoliticalAlignment>().FactionId == faction.Definition.FactionId &&
                agent.GetComponent<FactionParticipation>().LastRecruitmentDecisionDay < dayNumber)
            .Select(agent => new { Agent = agent, Score = RecruitmentScore(agent) })
            .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Agent.Id).Take(count * 3).ToArray();
        var accepted = 0;
        foreach (var candidate in candidates)
        {
            ref var state = ref candidate.Agent.GetComponent<FactionParticipation>();
            state.LastRecruitmentDecisionDay = dayNumber;
            // A low score means the agent declines this invitation, even when
            // their stated alignment matches the faction.
            var threshold = 42 + StableNoise(candidate.Agent.Id, dayNumber, faction.Definition.FactionId) * 32;
            if (candidate.Score < threshold)
            {
                Log(dayNumber, "recruitment-declined", candidate.Agent.Id, faction.Definition.FactionId,
                    detail: $"score={candidate.Score:F2};threshold={threshold:F2}");
                continue;
            }
            if (AcceptRecruitment(candidate.Agent.Id, faction.Definition.Id)) accepted++;
            if (accepted >= count) break;
        }
        faction.MemberCount = CountMembers(faction.Definition.FactionId);
    }

    private float RecruitmentScore(Entity agent)
    {
        var values = agent.GetComponent<AgentAttributes>().Values;
        var engagement = values[_catalog.Politics.PoliticalEngagementAttributeIndex];
        var motivation = values[_catalog.Politics.MotivationAttributeIndex];
        var social = SocialPressure(agent.Id);
        return engagement * .42f + motivation * .38f + (50 + social * 50) * .20f;
    }

    private void HireActivists(FactionRuntime faction)
    {
        var leaderJob = _jobs[faction.Definition.LeaderJobId!];
        var leaderExists = _agents.Values.Any(agent => agent.GetComponent<Identity>().OccupationId == leaderJob.Hash);
        if (!leaderExists) return;
        var activistJob = _jobs[faction.Definition.ActivistJobId!];
        var currentStaff = _agents.Values.Count(agent => agent.GetComponent<Identity>().OccupationId == activistJob.Hash);
        var targetStaff = Math.Min(3, Math.Max(1, faction.MemberCount / 12));
        foreach (var agent in _agents.Values.Where(agent =>
                     agent.GetComponent<FactionParticipation>().FactionId == faction.Definition.FactionId &&
                     agent.GetComponent<FactionParticipation>().Role == FactionMemberRole.Volunteer &&
                     agent.GetComponent<FactionParticipation>().AppliedForStaff)
                 .OrderByDescending(RecruitmentScore).ThenBy(agent => agent.Id))
        {
            if (currentStaff >= targetStaff) break;
            if (RecruitmentScore(agent) < 62) continue;
            Hire(agent, activistJob, FactionMemberRole.Activist);
            Log(_lastUpdatedDay, "activist-hired", agent.Id, faction.Definition.FactionId, activistJob.Id,
                otherAgentId: _agents.Values.FirstOrDefault(candidate => candidate.GetComponent<Identity>().OccupationId == leaderJob.Hash).Id);
            currentStaff++;
        }
    }

    private void ConsiderApplications(FactionRuntime faction, int dayNumber)
    {
        foreach (var agent in _agents.Values.Where(agent =>
                     agent.GetComponent<FactionParticipation>().FactionId == faction.Definition.FactionId &&
                     agent.GetComponent<FactionParticipation>().Role == FactionMemberRole.Volunteer))
        {
            ref var membership = ref agent.GetComponent<FactionParticipation>();
            if (membership.AppliedForStaff) continue;
            var values = agent.GetComponent<AgentAttributes>().Values;
            var interest = (values[_catalog.Politics.PoliticalEngagementAttributeIndex] +
                values[_catalog.Politics.MotivationAttributeIndex]) / 2f;
            var threshold = 55 + StableNoise(agent.Id, dayNumber, faction.Definition.FactionId) * 25;
            if (interest >= threshold)
            {
                membership.AppliedForStaff = true;
                Log(dayNumber, "staff-application", agent.Id, faction.Definition.FactionId,
                    faction.Definition.ActivistJobId);
            }
            else
                Log(dayNumber, "staff-application-declined", agent.Id, faction.Definition.FactionId,
                    faction.Definition.ActivistJobId, detail: $"interest={interest:F2};threshold={threshold:F2}");
        }
    }

    private void ReconcileRoles(FactionRuntime faction)
    {
        var leaderHash = _jobs[faction.Definition.LeaderJobId!].Hash;
        var activistHash = _jobs[faction.Definition.ActivistJobId!].Hash;
        foreach (var agent in _agents.Values)
        {
            ref var membership = ref agent.GetComponent<FactionParticipation>();
            if (membership.FactionId != faction.Definition.FactionId || membership.Role == FactionMemberRole.None) continue;
            var occupation = agent.GetComponent<Identity>().OccupationId;
            if (occupation == leaderHash) membership.Role = FactionMemberRole.Leader;
            else if (occupation == activistHash) membership.Role = FactionMemberRole.Activist;
            else if (membership.Role is FactionMemberRole.Leader or FactionMemberRole.Activist)
                membership.Role = FactionMemberRole.Volunteer;
        }
    }

    private void Hire(Entity agent, JobDefinition job, FactionMemberRole role)
    {
        ref var membership = ref agent.GetComponent<FactionParticipation>();
        ref var identity = ref agent.GetComponent<Identity>();
        if (membership.PreviousOccupationId == 0) membership.PreviousOccupationId = identity.OccupationId;
        identity.OccupationId = job.Hash;
        membership.Role = role;
        membership.AppliedForStaff = false;
        SetWorkplace(agent, job);
    }

    private void SetWorkplace(Entity agent, JobDefinition job)
    {
        var workplace = _catalog.World.GetLocationsByType(job.WorkplaceType).OrderBy(location => location.Hash).First();
        ref var location = ref agent.GetComponent<AgentLocation>();
        location.WorkLocationId = workplace.Hash;
        var route = _catalog.World.FindShortestRoute(location.HomeLocationId, workplace.Hash)!;
        agent.GetComponent<AgentCommute>().TravelMinutes = route.TravelMinutes;
        if (agent.TryGetComponent<AgentTravel>(out _))
        {
            ref var travel = ref agent.GetComponent<AgentTravel>();
            travel.RouteLocationIds = route.LocationIds.ToArray();
            travel.TotalTravelMinutes = route.TravelMinutes;
            travel.RoutePosition = 0;
            travel.RemainingTravelMinutes = 0;
            travel.DestinationLocationId = 0;
            travel.Mode = AgentTravelMode.Stationary;
        }
        _lodService?.RefreshCoarseProfile(agent);
    }

    private void ReleaseFactionOccupation(Entity agent, FactionParticipation membership)
    {
        ref var identity = ref agent.GetComponent<Identity>();
        var currentHash = identity.OccupationId;
        var current = _catalog.Jobs.FirstOrDefault(job => job.Hash == currentHash);
        if (current?.FactionRole == "leader" && agent.TryGetComponent<PoliticalParticipation>(out var politics) && politics.PreviousOccupationId != 0)
        {
            identity.OccupationId = politics.PreviousOccupationId;
            politics.PreviousOccupationId = 0;
            agent.GetComponent<PoliticalParticipation>() = politics;
        }
        else if (membership.PreviousOccupationId != 0)
            identity.OccupationId = membership.PreviousOccupationId;
        RestoreWorkplace(agent, identity.OccupationId);
    }

    private void RestoreWorkplace(Entity agent, int occupationId)
    {
        var job = _catalog.Jobs.FirstOrDefault(candidate => candidate.Hash == occupationId);
        if (job is null) return;
        var workplace = _catalog.World.GetLocationsByType(job.WorkplaceType).OrderBy(location => location.Hash).First();
        ref var location = ref agent.GetComponent<AgentLocation>();
        location.WorkLocationId = workplace.Hash;
        var route = _catalog.World.FindShortestRoute(location.HomeLocationId, workplace.Hash)!;
        agent.GetComponent<AgentCommute>().TravelMinutes = route.TravelMinutes;
        if (agent.TryGetComponent<AgentTravel>(out _))
        {
            ref var travel = ref agent.GetComponent<AgentTravel>();
            travel.RouteLocationIds = route.LocationIds.ToArray();
            travel.TotalTravelMinutes = route.TravelMinutes;
            travel.RoutePosition = 0;
            travel.RemainingTravelMinutes = 0;
            travel.DestinationLocationId = 0;
            travel.Mode = AgentTravelMode.Stationary;
        }
        _lodService?.RefreshCoarseProfile(agent);
    }

    private void SelectGoal(FactionRuntime faction)
    {
        var previousGoalId = faction.ActiveGoal?.Id;
        void SetGoal(FactionGoalDefinition? goal)
        {
            faction.ActiveGoal = goal;
            if (!string.Equals(previousGoalId, goal?.Id, StringComparison.OrdinalIgnoreCase))
                Log(_lastUpdatedDay, "goal-selected", factionId: faction.Definition.FactionId,
                    detail: goal is null ? "no-eligible-goal" : $"goal={goal.Id};action={goal.Action}");
        }

        var leaderHash = _jobs[faction.Definition.LeaderJobId!].Hash;
        if (!_agents.Values.Any(agent => agent.GetComponent<Identity>().OccupationId == leaderHash))
        {
            // Until members elect a leader, the faction can only build the
            // membership needed to hold its first internal election.
            SetGoal(faction.Definition.Goals!.FirstOrDefault(goal => goal.Action == "recruit" &&
                !faction.CompletedGoals.Contains(goal.Id)));
            return;
        }
        foreach (var goal in faction.Definition.Goals!.OrderByDescending(goal => goal.Priority).ThenBy(goal => goal.Id))
        {
            if (faction.CompletedGoals.Contains(goal.Id)) continue;
            if (goal.Prerequisites?.Any(id => !faction.CompletedGoals.Contains(id)) == true) continue;
            SetGoal(goal);
            return;
        }
        SetGoal(null);
    }

    private float Metric(FactionRuntime faction, string metric) => metric switch
    {
        "members" => faction.MemberCount,
        "organization" => faction.Organization,
        "support" => faction.PublicSupport,
        "control" => faction.InstitutionalControl,
        _ => 0
    };

    private int CountMembers(byte factionId) => _agents.Values.Count(agent =>
    {
        var member = agent.GetComponent<FactionParticipation>();
        return member.FactionId == factionId && member.Role != FactionMemberRole.None;
    });

    private int CountRole(byte factionId, FactionMemberRole role) => _agents.Values.Count(agent =>
    {
        var member = agent.GetComponent<FactionParticipation>();
        return member.FactionId == factionId && member.Role == role;
    });

    private int CountControlledOffices(byte factionId) => _agents.Values.Count(agent =>
    {
        var jobHash = agent.GetComponent<Identity>().OccupationId;
        return _jobsByHash.TryGetValue(jobHash, out var job) && job.SelectionMethod is not null && job.FactionId is null &&
            agent.GetComponent<PoliticalAlignment>().FactionId == factionId;
    });

    private float SocialPressure(int agentId)
    {
        var edges = _socialIndexes.GetOutgoingEdges(agentId);
        if (edges.Length == 0) return 0;
        var total = 0f; var count = 0;
        foreach (var edge in edges)
            if (_socialIndexes.TryGetAgent(edge.TargetAgentId, out var target) && !target.IsNull &&
                target.TryGetComponent<AgentAttributes>(out var attributes))
            { total += attributes.Values[_catalog.Politics.PoliticalEngagementAttributeIndex]; count++; }
        return count == 0 ? 0 : Math.Clamp((total / count - 50) / 50, -1, 1);
    }

    private void RefreshAgents()
    {
        _agents.Clear();
        foreach (var agent in _store.Query<Identity, PoliticalAlignment, FactionParticipation, AgentAttributes>().Entities)
            _agents[agent.Id] = agent;
    }

    private void Log(int day, string type, int? agentId = null, byte? factionId = null,
        string? officeId = null, int? otherAgentId = null, string? detail = null) =>
        // Faction strategy runs during the first simulated minute of each day.
        _events.Add(new PoliticalDiagnosticEvent(Math.Max(1, day), 1, type, agentId,
            factionId, officeId, OtherAgentId: otherAgentId, Detail: detail));

    private static float StableNoise(int agentId, int day, byte factionId)
    {
        var value = unchecked((uint)agentId * 0x9E3779B9u ^ (uint)day * 0x85EBCA6Bu ^ (uint)factionId * 0xC2B2AE35u);
        value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15;
        return (value & 0xFFFF) / 65535f;
    }

    private sealed class FactionRuntime(FactionDefinition definition)
    {
        public FactionDefinition Definition { get; } = definition;
        public HashSet<string> CompletedGoals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public FactionGoalDefinition? ActiveGoal { get; set; }
        public int MemberCount { get; set; }
        public float Organization { get; set; }
        public float PublicSupport { get; set; } = 15;
        public int InstitutionalControl { get; set; }
    }
}
