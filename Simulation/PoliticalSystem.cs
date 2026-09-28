using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

/// <summary>
/// Resolves nominations, physical polling trips, ballots, and appointed offices.
/// It operates on the persistent agent components shared by every LOD tier.
/// </summary>
public sealed class PoliticalSystem
{
    private const int NominationVisitMinutes = 10;
    private const int PollingVisitMinutes = 10;
    private readonly EntityStore _store;
    private readonly ContentCatalog _catalog;
    private readonly WorldTopology _world;
    private readonly AgentSocialIndexes _socialIndexes;
    private readonly Dictionary<int, JobDefinition> _jobsByHash;
    private readonly Dictionary<string, JobDefinition> _jobsById;
    private readonly HashSet<int> _politicalJobHashes;
    private readonly JobDefinition[] _politicalOffices;
    private readonly Dictionary<int, int> _travelToPoll;
    private readonly Dictionary<int, int> _travelFromPoll;
    private readonly Dictionary<int, Entity> _activeTrips = [];
    private readonly Entity _clock;
    private readonly AgentLodService? _lodService;
    private long _lastProcessedMinute = -1;
    private int _lastResolvedElectionId;
    private int _lastCandidateConsideredElectionId;
    private int _lastVoterDecisionElectionId;
    private readonly List<PoliticalDiagnosticEvent> _events = [];
    private readonly List<ElectionDiagnosticResult> _electionResults = [];

    private readonly ArchetypeQuery<Identity, PoliticalAlignment, AgentAttributes, AgentLocation, PoliticalParticipation> _voterCandidateQuery;
    private readonly ArchetypeQuery<Identity, PoliticalAlignment, AgentAttributes, PoliticalParticipation> _electionAgentQuery;
    private readonly ArchetypeQuery<Identity, PoliticalParticipation> _participationQuery;
    private readonly ArchetypeQuery<Identity> _identityQuery;

    public PoliticalSystem(EntityStore store, ContentCatalog catalog, Entity clock,
        AgentSocialIndexes socialIndexes, AgentLodService? lodService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _voterCandidateQuery = _store.Query<Identity, PoliticalAlignment, AgentAttributes, AgentLocation, PoliticalParticipation>();
        _electionAgentQuery = _store.Query<Identity, PoliticalAlignment, AgentAttributes, PoliticalParticipation>();
        _participationQuery = _store.Query<Identity, PoliticalParticipation>();
        _identityQuery = _store.Query<Identity>();
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _world = catalog.World;
        _clock = clock;
        _socialIndexes = socialIndexes ?? throw new ArgumentNullException(nameof(socialIndexes));
        _lodService = lodService;
        _jobsByHash = catalog.Jobs.ToDictionary(job => job.Hash);
        _jobsById = catalog.Jobs.ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);
        _politicalOffices = catalog.Jobs.Where(job => job.SelectionMethod is not null).OrderBy(job => job.Hash).ToArray();
        _politicalJobHashes = _politicalOffices
            .Select(job => job.Hash).ToHashSet();
        _travelToPoll = catalog.World.Locations.ToDictionary(location => location.Hash,
            location => catalog.World.FindShortestRoute(location.Hash, catalog.Politics.PollingLocationId)!.TravelMinutes);
        _travelFromPoll = catalog.World.Locations.ToDictionary(location => location.Hash,
            location => catalog.World.FindShortestRoute(catalog.Politics.PollingLocationId, location.Hash)!.TravelMinutes);
    }

    public int ResolvedElectionCount => _lastResolvedElectionId;
    public int LastElectionVoteCount { get; private set; }
    public int LastElectionCandidateCount { get; private set; }
    public IReadOnlyList<PoliticalDiagnosticEvent> Events => _events;
    public IReadOnlyList<ElectionDiagnosticResult> ElectionResults => _electionResults;

    public void Update()
    {
        var time = _clock.GetComponent<WorldTime>();
        var minute = (long)Math.Floor(time.ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute);
        if (minute <= _lastProcessedMinute) return;
        _lastProcessedMinute = minute;
        ProcessTrips(minute);

        var dayNumber = time.DayIndex + 1;
        var interval = _catalog.Politics.ElectionIntervalDays;
        var cycleId = ((dayNumber - 1) / interval) + 1;
        var dayInCycle = ((dayNumber - 1) % interval) + 1;
        if (dayInCycle >= interval - _catalog.Politics.NominationDays && dayInCycle < interval &&
            cycleId > _lastCandidateConsideredElectionId)
        {
            _lastCandidateConsideredElectionId = cycleId;
            ConsiderCandidates(minute, cycleId);
        }

        if (dayNumber % interval != 0) return;
        var minuteOfDay = (int)(minute % SimulationDefaults.SimulationMinutesPerDay);
        if (minuteOfDay >= _catalog.Politics.PollOpeningMinute && cycleId > _lastVoterDecisionElectionId)
        {
            _lastVoterDecisionElectionId = cycleId;
            ConsiderVoters(minute, cycleId, time.DayOfWeek);
        }
        if (minuteOfDay >= _catalog.Politics.PollClosingMinute && cycleId > _lastResolvedElectionId)
            ResolveElection(cycleId);
    }

    private void ConsiderCandidates(long minute, int electionId)
    {
        foreach (var agent in _voterCandidateQuery.Entities.OrderBy(entity => entity.Id))
        {
            ref var participation = ref agent.GetComponent<PoliticalParticipation>();
            if (participation.CandidateElectionId == electionId || participation.TripKind != PoliticalTripKind.None)
                continue;
            participation.CandidateElectionId = electionId;

            var attributes = agent.GetComponent<AgentAttributes>().Values;
            var engagement = Attribute(attributes, _catalog.Politics.PoliticalEngagementAttributeIndex);
            var motivation = Attribute(attributes, _catalog.Politics.MotivationAttributeIndex);
            var settings = _catalog.Politics;
            var candidacyScore = engagement * settings.CandidateEngagementWeight +
                motivation * settings.CandidateMotivationWeight;
            if (candidacyScore < settings.CandidateThreshold +
                StableUnit(agent.Id, electionId, 11) * settings.CandidateThresholdVariation)
            {
                Log(minute, "candidacy-declined", agent.Id, electionId: electionId,
                    detail: $"score={candidacyScore:F2}");
                continue;
            }

            var membership = agent.TryGetComponent<FactionParticipation>(out var factionState)
                ? factionState : new FactionParticipation { FactionId = byte.MaxValue };
            var offices = _politicalOffices.Where(office => office.FactionId is null ||
                (membership.FactionId == office.FactionId && membership.Role != FactionMemberRole.None)).ToArray();
            if (offices.Length == 0)
            {
                Log(minute, "candidacy-ineligible", agent.Id, electionId: electionId,
                    detail: "No available office matches faction membership.");
                continue;
            }
            var office = offices[(int)(StableUnit(agent.Id, electionId, 23) * offices.Length) % offices.Length];
            Log(minute, "nomination-trip-started", agent.Id, office.FactionId, office.Id, electionId);
            BeginTrip(agent, ref participation, PoliticalTripKind.Nomination, electionId,
                office.Hash, minute, NominationVisitMinutes);
        }
    }

    private void ConsiderVoters(long minute, int electionId, int dayOfWeek)
    {
        var dayStart = minute - minute % SimulationDefaults.SimulationMinutesPerDay;
        var pollOpen = dayStart + _catalog.Politics.PollOpeningMinute;
        var pollClose = dayStart + _catalog.Politics.PollClosingMinute;
        foreach (var agent in _voterCandidateQuery.Entities.OrderBy(entity => entity.Id))
        {
            ref var participation = ref agent.GetComponent<PoliticalParticipation>();
            if (participation.VotingDecisionElectionId == electionId ||
                participation.TripKind != PoliticalTripKind.None) continue;
            participation.VotingDecisionElectionId = electionId;

            var identity = agent.GetComponent<Identity>();
            var job = _jobsByHash[identity.OccupationId];
            var location = agent.GetComponent<AgentLocation>();
            if (!_travelToPoll.TryGetValue(location.CurrentLocationId, out var outboundMinutes) ||
                !_travelFromPoll.TryGetValue(location.CurrentLocationId, out var inboundMinutes))
            {
                Log(minute, "turnout-inaccessible", agent.Id, electionId: electionId);
                continue;
            }

            var workday = job.WorkDays.Contains(dayOfWeek);
            var departure = Math.Max(minute, pollOpen);
            var workOverlap = 0;
            if (workday)
            {
                var shiftStart = dayStart + job.WorkStartMinute;
                var shiftEnd = dayStart + job.WorkEndMinute;
                workOverlap = Math.Max(0, (int)(Math.Min(shiftEnd, pollClose) - Math.Max(shiftStart, pollOpen)));
                var beforeShift = Math.Max(minute, pollOpen);
                var canVoteBeforeWork = beforeShift + outboundMinutes + PollingVisitMinutes +
                    inboundMinutes <= shiftStart;
                if (canVoteBeforeWork)
                    departure = beforeShift;
                else
                    departure = Math.Max(minute, shiftEnd);
            }

            var arrival = departure + outboundMinutes;
            if (arrival + PollingVisitMinutes > pollClose)
            {
                Log(minute, "turnout-work-conflict", agent.Id, electionId: electionId);
                continue;
            }
            var attributes = agent.GetComponent<AgentAttributes>().Values;
            var engagement = Attribute(attributes, _catalog.Politics.PoliticalEngagementAttributeIndex);
            var motivation = Attribute(attributes, _catalog.Politics.MotivationAttributeIndex);
            var social = SocialPressure(agent.Id);
            var settings = _catalog.Politics;
            var travelPenalty = Math.Min(settings.MaximumTravelPenalty,
                (outboundMinutes + inboundMinutes) * settings.TravelPenaltyPerMinute);
            var workPenalty = workday ? workOverlap /
                (float)(settings.PollClosingMinute - settings.PollOpeningMinute) * settings.WorkOverlapPenalty : 0f;
            var turnoutScore = engagement * settings.VoteEngagementWeight +
                motivation * settings.VoteMotivationWeight + (50f + social * 50f) * settings.VoteSocialPressureWeight +
                settings.VoteBaseUtility - travelPenalty - workPenalty;
            if (turnoutScore < settings.VoteThreshold +
                StableUnit(agent.Id, electionId, 37) * settings.VoteThresholdVariation)
            {
                Log(minute, "turnout-abstained", agent.Id, electionId: electionId,
                    detail: $"score={turnoutScore:F2};workOverlap={workOverlap};travel={outboundMinutes + inboundMinutes}");
                continue;
            }

            Log(minute, "polling-trip-started", agent.Id, electionId: electionId,
                detail: $"departureMinute={departure};arrivalMinute={arrival}");
            BeginTrip(agent, ref participation, PoliticalTripKind.Polling, electionId,
                0, departure, PollingVisitMinutes);
        }
    }

    private void BeginTrip(Entity agent, ref PoliticalParticipation participation,
        PoliticalTripKind kind, int electionId, int officeHash, long departureMinute, int visitMinutes)
    {
        var location = agent.GetComponent<AgentLocation>();
        if (!_travelToPoll.TryGetValue(location.CurrentLocationId, out var outboundMinutes) ||
            !_travelFromPoll.TryGetValue(location.CurrentLocationId, out var inboundMinutes)) return;
        participation.TripKind = kind;
        participation.TripInProgress = false;
        participation.TripArrived = false;
        participation.TripElectionId = electionId;
        participation.TripOfficeHash = officeHash;
        participation.TripOriginLocationId = location.CurrentLocationId;
        participation.TripDepartureMinute = departureMinute;
        participation.TripArrivalMinute = departureMinute + outboundMinutes;
        participation.TripReturnMinute = participation.TripArrivalMinute + visitMinutes + inboundMinutes;
        _activeTrips[agent.Id] = agent;
    }

    private void ProcessTrips(long minute)
    {
        List<int>? completedTrips = null;
        foreach (var pair in _activeTrips)
        {
            var agent = pair.Value;
            ref var participation = ref agent.GetComponent<PoliticalParticipation>();
            if (participation.TripKind == PoliticalTripKind.None) continue;
            if (!participation.TripInProgress && minute >= participation.TripDepartureMinute)
                participation.TripInProgress = true;
            if (!participation.TripInProgress) continue;

            if (!participation.TripArrived && minute >= participation.TripArrivalMinute)
            {
                participation.TripArrived = true;
                agent.GetComponent<AgentLocation>().CurrentLocationId = _catalog.Politics.PollingLocationId;
                if (participation.TripKind == PoliticalTripKind.Nomination)
                {
                    participation.CandidateJobHash = participation.TripOfficeHash;
                    participation.CandidateElectionId = participation.TripElectionId;
                    var officeId = _jobsByHash[participation.TripOfficeHash].Id;
                    Log(minute, "candidate-registered", agent.Id, _jobsByHash[participation.TripOfficeHash].FactionId,
                        officeId, participation.TripElectionId);
                }
                else
                {
                    participation.VotedElectionId = participation.TripElectionId;
                    Log(minute, "vote-cast", agent.Id, electionId: participation.TripElectionId);
                }
                SignalLocationChanged(agent);
            }

            if (minute < participation.TripReturnMinute) continue;
            agent.GetComponent<AgentLocation>().CurrentLocationId = participation.TripOriginLocationId;
            SignalLocationChanged(agent);
            participation.TripKind = PoliticalTripKind.None;
            participation.TripInProgress = false;
            participation.TripArrived = false;
            (completedTrips ??= []).Add(agent.Id);
        }
        if (completedTrips is not null)
            foreach (var agentId in completedTrips) _activeTrips.Remove(agentId);
    }

    private void ResolveElection(int electionId)
    {
        RestoreFormerOfficeholders();
        var winners = new Dictionary<int, Entity>();
        var candidates = _electionAgentQuery.Entities
            .Where(agent => agent.GetComponent<PoliticalParticipation>().CandidateElectionId == electionId &&
                agent.GetComponent<PoliticalParticipation>().CandidateJobHash != 0)
            .OrderBy(agent => agent.Id).ToArray();
        var voters = _electionAgentQuery.Entities
            .Where(agent => agent.GetComponent<PoliticalParticipation>().VotedElectionId == electionId)
            .OrderBy(agent => agent.Id).ToArray();
        LastElectionCandidateCount = candidates.Length;
        LastElectionVoteCount = voters.Length;
        var officeResults = new List<ElectionOfficeResult>();

        foreach (var office in _catalog.Jobs.Where(job => job.SelectionMethod == "elected").OrderBy(job => job.Hash))
        {
            var contenders = candidates.Where(agent =>
                agent.GetComponent<PoliticalParticipation>().CandidateJobHash == office.Hash &&
                (office.FactionId is null || IsFactionMember(agent, office.FactionId.Value))).ToArray();
            if (contenders.Length == 0)
            {
                officeResults.Add(new ElectionOfficeResult(office.Id, office.FactionId,
                    null, null, 0, 0, []));
                Log(CurrentMinute(), "office-vacant", officeId: office.Id, electionId: electionId);
                continue;
            }
            var tallies = contenders.ToDictionary(agent => agent.Id, _ => 0);
            foreach (var voter in voters.Where(voter => office.FactionId is null ||
                         IsFactionMember(voter, office.FactionId.Value)))
            {
                var voterFaction = office.FactionId ?? voter.GetComponent<PoliticalAlignment>().FactionId;
                var aligned = contenders.Where(candidate => candidate.GetComponent<PoliticalAlignment>().FactionId == voterFaction).ToArray();
                var ballotOptions = aligned.Length > 0 ? aligned : contenders;
                var choice = ballotOptions.OrderByDescending(candidate => Charisma(candidate))
                    .ThenBy(candidate => candidate.Id).First();
                tallies[choice.Id]++;
                Log(CurrentMinute(), "ballot", voter.Id, office.FactionId,
                    office.Id, electionId, choice.Id);
            }
            var winner = contenders.OrderByDescending(candidate => tallies[candidate.Id])
                .ThenByDescending(Charisma).ThenBy(candidate => candidate.Id).First();
            winners[office.Hash] = winner;
            AssignOffice(winner, office);
            var candidateTallies = contenders.Select(candidate => new ElectionCandidateTally(
                    candidate.Id, candidate.GetComponent<PoliticalAlignment>().FactionId, tallies[candidate.Id]))
                .OrderByDescending(tally => tally.Votes).ThenBy(tally => tally.AgentId).ToArray();
            officeResults.Add(new ElectionOfficeResult(office.Id, office.FactionId, winner.Id,
                winner.GetComponent<PoliticalAlignment>().FactionId, contenders.Length,
                candidateTallies.Sum(tally => tally.Votes), candidateTallies));
            Log(CurrentMinute(), "elected", winner.Id, office.FactionId, office.Id, electionId,
                detail: $"votes={tallies[winner.Id]};candidates={contenders.Length}");
        }

        var currentOfficeholders = _identityQuery.Entities
            .Where(agent => _politicalJobHashes.Contains(agent.GetComponent<Identity>().OccupationId))
            .GroupBy(agent => agent.GetComponent<Identity>().OccupationId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var office in _catalog.Jobs.Where(job => job.SelectionMethod == "appointed").OrderBy(job => job.Hash))
        {
            if (office.AppointedByJobId is null || !_jobsById.TryGetValue(office.AppointedByJobId, out var appointingOffice))
                continue;

            var appointingOfficial = winners.TryGetValue(appointingOffice.Hash, out var newWinner)
                ? newWinner
                : (currentOfficeholders.TryGetValue(appointingOffice.Hash, out var current) ? current : default);

            if (appointingOfficial.IsNull) continue;
            var applicants = candidates.Where(agent =>
                agent.GetComponent<PoliticalParticipation>().CandidateJobHash == office.Hash).ToArray();
            if (applicants.Length == 0) continue;
            var faction = appointingOfficial.GetComponent<PoliticalAlignment>().FactionId;
            var appointee = applicants.OrderByDescending(agent =>
                    agent.GetComponent<PoliticalAlignment>().FactionId == faction)
                .ThenByDescending(agent => Attribute(agent.GetComponent<AgentAttributes>().Values,
                    _catalog.Politics.PoliticalEngagementAttributeIndex))
                .ThenByDescending(Charisma).ThenBy(agent => agent.Id).First();
            AssignOffice(appointee, office);
            Log(CurrentMinute(), "appointed", appointee.Id,
                appointee.GetComponent<PoliticalAlignment>().FactionId, office.Id, electionId,
                appointingOfficial.Id, $"appointedBy={appointingOffice.Id}");
        }

        _electionResults.Add(new ElectionDiagnosticResult(electionId,
            (int)(CurrentMinute() / SimulationDefaults.SimulationMinutesPerDay) + 1,
            LastElectionCandidateCount, LastElectionVoteCount, officeResults));
        Log(CurrentMinute(), "election-resolved", electionId: electionId,
            detail: $"candidates={LastElectionCandidateCount};voters={LastElectionVoteCount}");
        _lastResolvedElectionId = electionId;
    }

    private void RestoreFormerOfficeholders()
    {
        foreach (var agent in _participationQuery.Entities)
        {
            ref var identity = ref agent.GetComponent<Identity>();
            ref var participation = ref agent.GetComponent<PoliticalParticipation>();
            if (!_politicalJobHashes.Contains(identity.OccupationId) || participation.PreviousOccupationId == 0)
                continue;
            identity.OccupationId = participation.PreviousOccupationId;
            participation.PreviousOccupationId = 0;
            if (_jobsByHash.TryGetValue(identity.OccupationId, out var previousJob))
                MoveToJob(agent, previousJob);
        }
    }

    private void AssignOffice(Entity agent, JobDefinition office)
    {
        ref var identity = ref agent.GetComponent<Identity>();
        ref var participation = ref agent.GetComponent<PoliticalParticipation>();
        if (!_politicalJobHashes.Contains(identity.OccupationId))
            participation.PreviousOccupationId = identity.OccupationId;
        identity.OccupationId = office.Hash;
        MoveToJob(agent, office);
    }

    private void MoveToJob(Entity agent, JobDefinition job)
    {
        var workplace = _world.GetLocationsByType(job.WorkplaceType)
            .OrderBy(location => location.Hash).First();
        ref var location = ref agent.GetComponent<AgentLocation>();
        location.WorkLocationId = workplace.Hash;
        var route = _world.FindShortestRoute(location.HomeLocationId, workplace.Hash)
            ?? throw new InvalidDataException($"No route exists from agent {agent.Id}'s home to appointed workplace '{workplace.Id}'.");
        ref var commute = ref agent.GetComponent<AgentCommute>();
        commute.TravelMinutes = route.TravelMinutes;
        if (agent.TryGetComponent<AgentTravel>(out _))
        {
            ref var travel = ref agent.GetComponent<AgentTravel>();
            travel.RouteLocationIds = route.LocationIds.ToArray();
            travel.TotalTravelMinutes = route.TravelMinutes;
            travel.RoutePosition = 0;
            travel.RemainingTravelMinutes = 0f;
            travel.DestinationLocationId = 0;
            travel.Mode = AgentTravelMode.Stationary;
        }
        _lodService?.RefreshCoarseProfile(agent);
    }

    private static bool IsFactionMember(Entity agent, byte factionId) =>
        agent.TryGetComponent<FactionParticipation>(out var membership) &&
        membership.FactionId == factionId && membership.Role != FactionMemberRole.None;

    private float SocialPressure(int agentId)
    {
        var edges = _socialIndexes.GetOutgoingEdges(agentId);
        if (edges.Length == 0) return 0f;
        var sum = 0f;
        var count = 0;
        foreach (var edge in edges)
        {
            if (!_socialIndexes.TryGetAgent(edge.TargetAgentId, out var target) || target.IsNull ||
                !target.TryGetComponent<AgentAttributes>(out var attributes)) continue;
            sum += Attribute(attributes.Values, _catalog.Politics.PoliticalEngagementAttributeIndex);
            count++;
        }
        return count == 0 ? 0f : Math.Clamp((sum / count - 50f) / 50f, -1f, 1f);
    }

    private float Charisma(Entity agent) => Attribute(agent.GetComponent<AgentAttributes>().Values,
        _catalog.AgentAttributes.GetIndex("charisma"));

    private static float Attribute(AgentAttributeValues values, int index) => values[index];

    private long CurrentMinute() => (long)Math.Floor(_clock.GetComponent<WorldTime>().ElapsedSimulationSeconds /
        SimulationDefaults.SimulationSecondsPerMinute);

    private void Log(long minute, string type, int? agentId = null, byte? factionId = null,
        string? officeId = null, int? electionId = null, int? otherAgentId = null, string? detail = null)
    {
        _events.Add(new PoliticalDiagnosticEvent(
            (int)(minute / SimulationDefaults.SimulationMinutesPerDay) + 1,
            (int)(minute % SimulationDefaults.SimulationMinutesPerDay), type, agentId, factionId,
            officeId, electionId, otherAgentId, detail));
    }

    private static float StableUnit(int agentId, int electionId, int salt)
    {
        var value = unchecked((uint)agentId * 0x9E3779B9u ^ (uint)electionId * 0x85EBCA6Bu ^ (uint)salt * 0xC2B2AE35u);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value / (float)uint.MaxValue;
    }

    private static void SignalLocationChanged(Entity agent)
    {
        if (agent.TryGetComponent<DecisionState>(out var decision))
        {
            DecisionInvalidation.SignalLocation(ref decision);
            agent.GetComponent<DecisionState>() = decision;
        }
    }
}
