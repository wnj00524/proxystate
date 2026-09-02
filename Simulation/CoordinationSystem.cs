using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

/// <summary>
/// Matches data-defined mutual intentions and owns their paired lifecycle.
/// Domain behavior remains in actions.json; this system only understands
/// proposals, deterministic arbitration, reservations, timing, and release.
/// </summary>
public sealed class CoordinationSystem : QuerySystem<CoordinationState>
{
    private readonly EntityStore _store;
    private readonly Entity _clock;
    private readonly ContentCatalog _catalog;
    private readonly Dictionary<int, CompiledIntent> _intents;
    private readonly Dictionary<int, JobDefinition> _jobs;
    private readonly CompiledIntent _fallback;
    private readonly AgentSocialIndexes _socialIndexes;
    private readonly AgentLodService? _lodService;

    public CoordinationSystem(EntityStore store, ContentCatalog catalog, Entity clock,
        AgentSocialIndexes? socialIndexes = null, AgentLodService? lodService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock;
        _intents = catalog.Intents.All.ToDictionary(intent => intent.Hash);
        _jobs = catalog.Jobs.ToDictionary(job => job.Hash);
        _fallback = catalog.Intents.Fallback;
        _socialIndexes = socialIndexes ?? BuildIndexes(store);
        _lodService = lodService;
        Filter.AnyTags(Tags.Get<Tier1LodTag, Tier2LodTag>());
    }

    protected override void OnUpdate()
    {
        var time = _clock.GetComponent<WorldTime>();
        var minute = (long)Math.Floor(time.ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute);
        PromoteTier3InvitationTargets();
        var agents = _store.Query<Identity>().Entities
            .Where(IsCoordinatable).OrderBy(entity => entity.Id).ToArray();
        var byId = agents.ToDictionary(entity => entity.Id);
        var targets = new AgentDecisionSystem.TargetResolver(_socialIndexes);

        MaintainExistingPairs(agents, byId, targets, time, minute);
        MatchInvitations(agents, byId, targets, time, minute);
    }

    private void PromoteTier3InvitationTargets()
    {
        if (_lodService is null) return;
        var byId = _store.Query<Identity>().Entities.ToDictionary(entity => entity.Id);
        foreach (var initiator in _store.Query<IntentionState>().Entities.Where(entity => entity.Tags.Has<Tier1LodTag>()))
        {
            var intention = initiator.GetComponent<IntentionState>();
            if (_intents.TryGetValue(intention.ActionHash, out var intent) && intent.Participation is not null &&
                byId.TryGetValue(intention.TargetEntityId, out var target) && target.Tags.Has<Tier3LodTag>())
                _lodService.AcquireInteractionPin(target);
        }
    }

    private static AgentSocialIndexes BuildIndexes(EntityStore store)
    {
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        return indexes;
    }

    private static bool IsCoordinatable(Entity entity) =>
        (entity.Tags.Has<Tier1LodTag>() || entity.Tags.Has<Tier2LodTag>()) &&
        entity.HasComponent<CoordinationState>() && entity.HasComponent<IntentionState>() &&
        entity.HasComponent<ActivityState>() && entity.HasComponent<DecisionState>() &&
        entity.HasComponent<AgentAttributes>() && entity.HasComponent<Psychology>() &&
        entity.HasComponent<AgentLocation>() && entity.HasComponent<AgentTravel>();

    private void MaintainExistingPairs(Entity[] agents, IReadOnlyDictionary<int, Entity> byId,
        AgentDecisionSystem.TargetResolver targets, WorldTime time, long minute)
    {
        // Initiators normally own pair maintenance. This first pass also frees
        // a participant whose initiator was deleted before the current update.
        foreach (var participant in agents)
        {
            ref var state = ref participant.GetComponent<CoordinationState>();
            if (!state.Active || state.Role != CoordinationRole.Participant) continue;
            if (!byId.TryGetValue(state.PartnerEntityId, out var initiator) ||
                !initiator.TryGetComponent<CoordinationState>(out var initiatorState) ||
                !initiatorState.Active || initiatorState.Role != CoordinationRole.Initiator ||
                initiatorState.PartnerEntityId != participant.Id || initiatorState.ActionHash != state.ActionHash)
                ReleaseSingle(participant, state.ActionHash, minute);
        }

        foreach (var initiator in agents)
        {
            ref var coordination = ref initiator.GetComponent<CoordinationState>();
            if (!coordination.Active || coordination.Role != CoordinationRole.Initiator) continue;
            if (!byId.TryGetValue(coordination.PartnerEntityId, out var participant) ||
                !participant.HasComponent<CoordinationState>())
            {
                ReleaseSingle(initiator, coordination.ActionHash, minute);
                continue;
            }

            ref var other = ref participant.GetComponent<CoordinationState>();
            if (!other.Active || other.Role != CoordinationRole.Participant ||
                other.PartnerEntityId != initiator.Id || other.ActionHash != coordination.ActionHash ||
                !_intents.TryGetValue(coordination.ActionHash, out var intent) || intent.Participation is null ||
                intent.Target.Query is null || !targets.IsRelated(initiator.Id, participant.Id, intent.Target.Query))
            {
                ReleasePair(initiator, participant, coordination.ActionHash, minute);
                continue;
            }

            var started = coordination.StartedAtMinute >= 0;
            var maximumReached = started && minute - coordination.StartedAtMinute >= coordination.MaximumDurationMinutes;
            if (coordination.ReleaseRequested || other.ReleaseRequested || maximumReached)
            {
                ReleasePair(initiator, participant, coordination.ActionHash, minute);
                continue;
            }

            var scoreIndex = intent.RuntimeIndex;
            var decision = initiator.GetComponent<DecisionState>();
            if (scoreIndex < 32)
                coordination.Utility = decision.CachedScores[scoreIndex];
            var participantOffer = EvaluateParticipantOffer(initiator, participant, intent, targets, time);
            if (!participantOffer.Eligible)
            {
                ReleasePair(initiator, participant, coordination.ActionHash, minute);
                continue;
            }
            other.Utility = participantOffer.Utility;

            var initiatorLocation = initiator.GetComponent<AgentLocation>().CurrentLocationId;
            var participantLocation = participant.GetComponent<AgentLocation>().CurrentLocationId;
            if (initiatorLocation != participantLocation && !CanReach(initiatorLocation, participantLocation))
            {
                ReleasePair(initiator, participant, coordination.ActionHash, minute);
                continue;
            }

            if (initiatorLocation == participantLocation)
            {
                if (!started)
                {
                    coordination.StartedAtMinute = minute;
                    other.StartedAtMinute = minute;
                }
                coordination.Status = CoordinationStatus.Performing;
                other.Status = CoordinationStatus.Performing;
            }
            else
            {
                coordination.Status = CoordinationStatus.Travelling;
                other.Status = CoordinationStatus.Waiting;
            }
        }
    }

    private bool CanReach(int fromLocationId, int toLocationId)
    {
        try { return _catalog.World.FindShortestRoute(fromLocationId, toLocationId) is not null; }
        catch (KeyNotFoundException) { return false; }
    }

    private void MatchInvitations(Entity[] agents, IReadOnlyDictionary<int, Entity> byId,
        AgentDecisionSystem.TargetResolver targets, WorldTime time, long minute)
    {
        var proposals = new List<Proposal>();
        foreach (var initiator in agents)
        {
            if (initiator.GetComponent<CoordinationState>().Active) continue;
            var intention = initiator.GetComponent<IntentionState>();
            if (!_intents.TryGetValue(intention.ActionHash, out var intent) || intent.Participation is null ||
                intention.TargetEntityId == 0 || !byId.TryGetValue(intention.TargetEntityId, out var participant) ||
                participant.GetComponent<CoordinationState>().Active || intent.Target.Query is null ||
                !targets.IsRelated(initiator.Id, participant.Id, intent.Target.Query)) continue;

            var offer = EvaluateParticipantOffer(initiator, participant, intent, targets, time);
            if (!offer.Eligible || !CanInterrupt(participant, initiator.Id, intent, offer.Utility, minute)) continue;
            proposals.Add(new Proposal(initiator, participant, intent, intention.Utility, offer.Utility));
        }

        proposals.Sort(static (left, right) =>
        {
            var comparison = right.ParticipantUtility.CompareTo(left.ParticipantUtility);
            if (comparison != 0) return comparison;
            comparison = right.InitiatorUtility.CompareTo(left.InitiatorUtility);
            if (comparison != 0) return comparison;
            comparison = left.Intent.Hash.CompareTo(right.Intent.Hash);
            if (comparison != 0) return comparison;
            comparison = left.Initiator.Id.CompareTo(right.Initiator.Id);
            return comparison != 0 ? comparison : left.Participant.Id.CompareTo(right.Participant.Id);
        });

        var used = new HashSet<int>();
        foreach (var proposal in proposals)
        {
            if (used.Contains(proposal.Initiator.Id) || used.Contains(proposal.Participant.Id)) continue;
            used.Add(proposal.Initiator.Id);
            used.Add(proposal.Participant.Id);
            Accept(proposal, minute);
        }

        foreach (var initiator in agents)
        {
            if (initiator.GetComponent<CoordinationState>().Active || used.Contains(initiator.Id)) continue;
            var intention = initiator.GetComponent<IntentionState>();
            if (_intents.TryGetValue(intention.ActionHash, out var intent) && intent.Participation is not null)
                Reject(initiator, intent, minute);
        }
    }

    private (bool Eligible, float Utility) EvaluateParticipantOffer(Entity initiator, Entity participant,
        CompiledIntent intent, AgentDecisionSystem.TargetResolver targets, WorldTime time)
    {
        var identity = participant.GetComponent<Identity>();
        if (!_jobs.TryGetValue(identity.OccupationId, out var job)) return default;
        var attributes = participant.GetComponent<AgentAttributes>().Values;
        var psychology = participant.GetComponent<Psychology>();
        var location = participant.GetComponent<AgentLocation>();
        var travel = participant.GetComponent<AgentTravel>();
        var target = targets.ResolveSpecific(participant.Id, initiator.Id);
        if (target.EntityId == 0) return default;
        var facts = new DecisionFactContext(time, job, attributes, location, travel,
            initiator.Id, target.Affinity, target.LocationId, target.Attributes);
        var acceptance = intent.Participation!.Acceptance;
        if (!acceptance.Eligibility.Evaluate(facts)) return default;
        return (true, DecisionUtility.Evaluate(acceptance.BaseUtility, acceptance.UtilityInputs,
            acceptance.TraitModifiers, psychology.TraitMask, facts));
    }

    private bool CanInterrupt(Entity participant, int initiatorId, CompiledIntent invitation, float offer, long minute)
    {
        var current = participant.GetComponent<IntentionState>();
        if (current.ActionHash == invitation.Hash && current.TargetEntityId == initiatorId) return true;
        if (!_intents.TryGetValue(current.ActionHash, out var currentIntent)) return true;
        var committed = current.SelectedAtMinute < minute &&
            minute - current.SelectedAtMinute < currentIntent.Controls.MinimumCommitmentMinutes;
        var urgent = offer >= invitation.Controls.UrgentPreemptionThreshold;
        return (urgent || !committed) &&
            (urgent || offer >= current.Utility + invitation.Controls.SwitchingThreshold);
    }

    private void Accept(Proposal proposal, long minute)
    {
        var participation = proposal.Intent.Participation!;
        ref var initiatorCoordination = ref proposal.Initiator.GetComponent<CoordinationState>();
        ref var participantCoordination = ref proposal.Participant.GetComponent<CoordinationState>();
        initiatorCoordination = CreateState(proposal.Participant.Id, proposal.Intent.Hash,
            CoordinationRole.Initiator, proposal.InitiatorUtility, participation, minute);
        participantCoordination = CreateState(proposal.Initiator.Id, proposal.Intent.Hash,
            CoordinationRole.Participant, proposal.ParticipantUtility, participation, minute);
        _lodService?.AcquireInteractionPin(proposal.Initiator);
        _lodService?.AcquireInteractionPin(proposal.Participant);

        ref var participantIntention = ref proposal.Participant.GetComponent<IntentionState>();
        participantIntention.ActionHash = proposal.Intent.Hash;
        participantIntention.TargetEntityId = proposal.Initiator.Id;
        participantIntention.TargetLocationId = proposal.Initiator.GetComponent<AgentLocation>().CurrentLocationId;
        participantIntention.SelectedAtMinute = minute;
        participantIntention.Utility = proposal.ParticipantUtility;
        DecisionInvalidation.SignalCoordinationLifecycle(
            ref proposal.Initiator.GetComponent<DecisionState>());
        DecisionInvalidation.SignalCoordinationLifecycle(
            ref proposal.Participant.GetComponent<DecisionState>());
    }

    private static CoordinationState CreateState(int partnerId, int actionHash, CoordinationRole role,
        float utility, CompiledParticipation participation, long minute) => new()
    {
        PartnerEntityId = partnerId,
        ActionHash = actionHash,
        Role = role,
        Status = CoordinationStatus.Reserved,
        AcceptedAtMinute = minute,
        StartedAtMinute = -1,
        MinimumDurationMinutes = participation.MinimumDurationMinutes,
        MaximumDurationMinutes = participation.MaximumDurationMinutes,
        Utility = utility
    };

    private void Reject(Entity initiator, CompiledIntent intent, long minute)
    {
        SetCooldown(initiator, intent.Hash, minute + intent.Participation!.RejectionCooldownMinutes);
        ResetToFallback(initiator);
    }

    private void ReleasePair(Entity initiator, Entity participant, int actionHash, long minute)
    {
        var cooldown = _intents.TryGetValue(actionHash, out var intent) ? intent.Controls.CooldownMinutes : 0;
        SetCooldown(initiator, actionHash, minute + cooldown);
        SetCooldown(participant, actionHash, minute + cooldown);
        ResetToFallback(initiator);
        ResetToFallback(participant);
    }

    private void ReleaseSingle(Entity agent, int actionHash, long minute)
    {
        var cooldown = _intents.TryGetValue(actionHash, out var intent) ? intent.Controls.CooldownMinutes : 0;
        SetCooldown(agent, actionHash, minute + cooldown);
        ResetToFallback(agent);
    }

    private void ResetToFallback(Entity agent)
    {
        if (agent.GetComponent<CoordinationState>().Active)
            _lodService?.ReleaseInteractionPin(agent);
        agent.GetComponent<CoordinationState>() = default;
        ref var intention = ref agent.GetComponent<IntentionState>();
        intention.ActionHash = _fallback.Hash;
        intention.TargetEntityId = 0;
        intention.TargetLocationId = 0;
        intention.Utility = _fallback.BaseUtility;
        ref var decision = ref agent.GetComponent<DecisionState>();
        DecisionInvalidation.SignalCritical(ref decision, FactDependencyMask.All,
            DecisionWakeReason.CoordinationLifecycle);
    }

    private static void SetCooldown(Entity agent, int actionHash, long untilMinute)
    {
        if (untilMinute <= 0) return;
        ref var state = ref agent.GetComponent<DecisionState>();
        var index = ((ReadOnlySpan<int>)state.CooldownActionHashes).IndexOf(actionHash);
        if (index < 0) index = ((ReadOnlySpan<int>)state.CooldownActionHashes).IndexOf(0);
        if (index < 0) return;
        state.CooldownActionHashes[index] = actionHash;
        state.CooldownUntilMinutes[index] = untilMinute;
    }

    private readonly record struct Proposal(Entity Initiator, Entity Participant, CompiledIntent Intent,
        float InitiatorUtility, float ParticipantUtility);
}
