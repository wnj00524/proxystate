using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

// Mutation-owning systems use these helpers instead of duplicating knowledge
// about which decision facts an ECS field represents.
public static class DecisionInvalidation
{
    // Ordinary facts remain dirty until the owning tier's next scheduled pass.
    public static void Signal(ref DecisionState state, FactDependencyMask changed)
    {
        state.ChangedFacts |= changed;
        state.Dirty = true;
    }

    public static void SignalCritical(ref DecisionState state, FactDependencyMask changed,
        DecisionWakeReason reason)
    {
        Signal(ref state, changed);
        state.ImmediateWakeReasons |= reason;
    }

    public static void SignalAttribute(ref DecisionState state, int attributeIndex) => Signal(ref state,
        new(FactDependencyCategory.Attributes, attributeIndex is >= 0 and < 64 ? 1UL << attributeIndex : ulong.MaxValue));
    public static void SignalLocation(ref DecisionState state) => Signal(ref state,
        new(FactDependencyCategory.Location | FactDependencyCategory.Travel | FactDependencyCategory.TargetLocation));
    public static void SignalTargetAvailability(ref DecisionState state) => Signal(ref state,
        new(FactDependencyCategory.SocialTargets | FactDependencyCategory.NetworkTargets |
            FactDependencyCategory.TargetAffinity | FactDependencyCategory.TargetAttributes |
            FactDependencyCategory.TargetLocation | FactDependencyCategory.Coordination));
    public static void SignalTargetLoss(ref DecisionState state) => SignalCritical(ref state,
        new(FactDependencyCategory.SocialTargets | FactDependencyCategory.NetworkTargets |
            FactDependencyCategory.TargetAffinity | FactDependencyCategory.TargetAttributes |
            FactDependencyCategory.TargetLocation | FactDependencyCategory.Coordination),
        DecisionWakeReason.TargetLoss);
    public static void SignalCoordinationLifecycle(ref DecisionState state) => SignalCritical(ref state,
        new(FactDependencyCategory.Coordination), DecisionWakeReason.CoordinationLifecycle);
}

internal static class DecisionUtility
{
    public static float Evaluate(float baseUtility, IReadOnlyList<CompiledUtilityInput> inputs,
        IReadOnlyList<CompiledTraitModifier> modifiers, long traitMask, in DecisionFactContext facts)
    {
        var score = baseUtility;
        foreach (var input in inputs)
            score += input.Weight * Curve(input.Curve, input.Expression.Evaluate(facts));
        foreach (var modifier in modifiers)
            if ((traitMask & modifier.TraitBit) != 0) score += modifier.Modifier;
        return score;
    }

    public static float Curve(IReadOnlyList<ResponsePoint> points, float value)
    {
        if (value <= points[0].X) return points[0].Y;
        for (var index = 1; index < points.Count; index++)
        {
            if (value > points[index].X) continue;
            var previous = points[index - 1];
            var amount = (value - previous.X) / (points[index].X - previous.X);
            return previous.Y + amount * (points[index].Y - previous.Y);
        }
        return points[^1].Y;
    }
}

// Target resolution and utility scoring operate entirely from compiled content.
// The winner application consequently copies a generic result into ECS state.
public sealed class AgentDecisionSystem : QuerySystem<Identity, AgentAttributes, Psychology, AgentLocation, AgentTravel>
{
    private readonly EntityStore _store;
    private readonly Entity _clock;
    private readonly Dictionary<int, JobDefinition> _jobs;
    private readonly CandidateEvaluator?[] _candidatesByIndex;
    private readonly Dictionary<int, CandidateEvaluator> _candidatesByHash;
    private readonly IntentCandidateIndex _candidateIndex;
    private readonly CompiledIntent _fallback;
    private readonly bool _captureDiagnostics;
    private readonly SimulationWorkDiagnostics? _workDiagnostics;
    private readonly AgentSocialIndexes _socialIndexes;
    private readonly int _tier2DecisionIntervalMinutes;
    private readonly AgentLodService? _lodService;

    public AgentDecisionSystem(EntityStore store, ContentCatalog catalog, Entity clock, bool captureDiagnostics = false,
        SimulationWorkDiagnostics? workDiagnostics = null, AgentSocialIndexes? socialIndexes = null,
        AgentLodService? lodService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(catalog);
        _clock = clock;
        _jobs = catalog.Jobs.ToDictionary(job => job.Hash);
        _fallback = catalog.Intents.Fallback;
        _captureDiagnostics = captureDiagnostics;
        _workDiagnostics = workDiagnostics;
        _socialIndexes = socialIndexes ?? BuildIndexes(store);
        _tier2DecisionIntervalMinutes = catalog.Lod.Tier2DecisionIntervalMinutes;
        _lodService = lodService;
        _candidateIndex = catalog.Intents.Candidates;
        _candidatesByIndex = new CandidateEvaluator?[catalog.Intents.Count];
        _candidatesByHash = new();
        foreach (var intent in catalog.Intents.All.Where(intent => !intent.Fallback))
        {
            var evaluator = new CandidateEvaluator(intent);
            _candidatesByIndex[intent.RuntimeIndex] = evaluator;
            _candidatesByHash.Add(intent.Hash, evaluator);
        }
        Filter.AnyTags(Tags.Get<Tier1LodTag, Tier2LodTag>());
    }

    protected override void OnUpdate()
    {
        // Due reductions must change query membership before this decision pass.
        _lodService?.ProcessScheduledDemotions();
        var time = _clock.GetComponent<WorldTime>();
        var minute = (long)Math.Floor(time.ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute);
        var targets = new TargetResolver(_socialIndexes);

        Query.ForEachEntity((ref Identity identity, ref AgentAttributes attributes, ref Psychology psychology,
            ref AgentLocation location, ref AgentTravel travel, Entity entity) =>
        {
            if (!_jobs.TryGetValue(identity.OccupationId, out var job)) return;
            ref var intention = ref entity.GetComponent<IntentionState>();
            ref var decision = ref entity.GetComponent<DecisionState>();
            var currentActionHash = intention.ActionHash;
            var context = new DecisionContext(time, job, attributes.Values, psychology.TraitMask, location, travel);

            // Re-resolving only the active definition makes target loss an immediate,
            // data-driven invalidation rather than a special case for an action ID.
            _candidatesByHash.TryGetValue(currentActionHash, out var active);
            if (active is not null)
            {
                var selected = targets.Resolve(entity.Id, active.Definition.Target, context);
                if (selected.EntityId != intention.TargetEntityId || selected.LocationId != intention.TargetLocationId)
                    DecisionInvalidation.SignalTargetLoss(ref decision);
            }
            var tier2 = entity.Tags.Has<Tier2LodTag>();
            var cadenceDue = decision.LastConsideredMinute < 0 ||
                minute - decision.LastConsideredMinute >= _tier2DecisionIntervalMinutes;
            var immediateWake = decision.ImmediateWakeReasons != DecisionWakeReason.None;
            if (tier2)
            {
                if (!cadenceDue && !immediateWake) return;
            }
            else if (!decision.Dirty && decision.LastConsideredMinute >= minute) return;

            EnsureCache(ref decision);
            if (_captureDiagnostics) EnsureDiagnosticCache(ref decision);
            // Tier 2 always refreshes the complete cache: both an hourly pass and
            // a critical lifecycle wake are authoritative reconsiderations.
            var fullPass = tier2 || decision.LastConsideredMinute < minute ||
                decision.ChangedFacts == FactDependencyMask.None;
            var changed = fullPass ? FactDependencyMask.All : decision.ChangedFacts;
            _workDiagnostics?.RecordDecisionPass();
            var candidateContext = new IntentCandidateContext(true, location.HomeLocationId != 0,
                location.WorkLocationId != 0, targets.HasSocialRelations(entity.Id),
                targets.HasNetworkRelations(entity.Id));
            decision.Dirty = false;
            decision.ChangedFacts = FactDependencyMask.None;
            decision.ImmediateWakeReasons = DecisionWakeReason.None;
            decision.LastConsideredMinute = minute;
            foreach (var runtimeIndex in _candidateIndex.EnumerateCandidates(candidateContext))
            {
                var candidate = _candidatesByIndex[runtimeIndex]!;
                if (!fullPass && !candidate.Definition.Dependencies.Intersects(changed)) continue;
                var result = candidate.Evaluate(context, targets.Resolve(entity.Id, candidate.Definition.Target, context),
                    _captureDiagnostics ? decision.CachedUtilityContributions[candidate.Definition.RuntimeIndex] : null,
                    _captureDiagnostics ? decision.CachedTraitContributions[candidate.Definition.RuntimeIndex] : null);
                _workDiagnostics?.RecordCandidateEvaluation();
                var index = candidate.Definition.RuntimeIndex;
                decision.CachedScores[index] = result.Score;
                decision.CachedEligibility[index] = result.Eligible;
                decision.CachedTargetEntityIds[index] = result.TargetEntityId;
                decision.CachedTargetLocationIds[index] = result.TargetLocationId;
                if (_captureDiagnostics)
                    decision.CachedRejectedPredicates[index] = result.Eligible
                        ? string.Empty : $"actions.json:intent '{candidate.Definition.Id}':eligibility";
                decision.EvaluationCount++;
            }
            var winner = new DecisionResult(_fallback.RuntimeIndex, _fallback, true, _fallback.BaseUtility, 0, 0);
            DecisionResult current = default;
            foreach (var runtimeIndex in _candidateIndex.EnumerateCandidates(candidateContext))
            {
                var result = CachedResult(_candidatesByIndex[runtimeIndex]!.Definition, decision);
                if (!result.Eligible || IsCoolingDown(result.Action.Hash, minute, decision)) continue;
                if (result.Action.Hash == currentActionHash) current = result;
                if (winner.Action.Fallback || result.Score > winner.Score ||
                    result.Score == winner.Score && result.Action.Hash < winner.Action.Hash) winner = result;
            }

            if (entity.HasComponent<CoordinationState>())
            {
                ref var coordination = ref entity.GetComponent<CoordinationState>();
                if (coordination.Active)
                {
                    var elapsed = coordination.StartedAtMinute < 0 ? 0 : minute - coordination.StartedAtMinute;
                    var minimumElapsed = coordination.StartedAtMinute >= 0 &&
                        elapsed >= coordination.MinimumDurationMinutes;
                    var alternative = winner.Action.Hash != coordination.ActionHash;
                    var urgent = winner.Score >= winner.Action.Controls.UrgentPreemptionThreshold;
                    var beatsCoordination = winner.Score >= coordination.Utility +
                        winner.Action.Controls.SwitchingThreshold;
                    if (elapsed >= coordination.MaximumDurationMinutes ||
                        minimumElapsed && alternative && (urgent || beatsCoordination))
                        coordination.ReleaseRequested = true;
                    return;
                }
            }
            if (currentActionHash != 0 && winner.Action.Hash != currentActionHash)
            {
                var currentDefinition = active?.Definition;
                var committed = currentDefinition is not null && minute - intention.SelectedAtMinute < currentDefinition.Controls.MinimumCommitmentMinutes;
                var currentScore = current.Action is null ? float.NegativeInfinity : current.Score;
                var urgent = winner.Score >= winner.Action.Controls.UrgentPreemptionThreshold;
                var switchingMargin = currentDefinition?.Controls.SwitchingThreshold ?? winner.Action.Controls.SwitchingThreshold;
                if (!urgent && (committed || winner.Score < currentScore + switchingMargin)) return;
                if (currentDefinition?.Controls.CooldownOnExit == true) SetCooldown(currentDefinition, minute, ref decision);
            }

            if (winner.Action.Hash == intention.ActionHash &&
                winner.TargetEntityId == intention.TargetEntityId && winner.TargetLocationId == intention.TargetLocationId) return;
            intention.ActionHash = winner.Action.Hash;
            intention.TargetEntityId = winner.TargetEntityId;
            intention.TargetLocationId = winner.TargetLocationId;
            intention.SelectedAtMinute = minute;
            intention.Utility = winner.Score;
        });
    }

    private static AgentSocialIndexes BuildIndexes(EntityStore store)
    {
        var indexes = new AgentSocialIndexes();
        indexes.Rebuild(store);
        return indexes;
    }

    private void EnsureCache(ref DecisionState state)
    {
        // Inline arrays are structurally allocated.
    }

    private void EnsureDiagnosticCache(ref DecisionState state)
    {
        var count = _candidatesByIndex.Length;
        if (state.CachedUtilityContributions?.Length == count &&
            state.CachedTraitContributions?.Length == count && state.CachedRejectedPredicates?.Length == count) return;
        state.CachedUtilityContributions = new float[count][];
        state.CachedTraitContributions = new float[count][];
        state.CachedRejectedPredicates = new string[count];
        foreach (var candidate in _candidatesByIndex)
        {
            if (candidate is null) continue;
            var index = candidate.Definition.RuntimeIndex;
            state.CachedUtilityContributions[index] = new float[candidate.Definition.UtilityInputs.Length];
            state.CachedTraitContributions[index] = new float[candidate.Definition.TraitModifiers.Length];
        }
    }

    private static DecisionResult CachedResult(CompiledIntent intent, DecisionState state)
    {
        var index = intent.RuntimeIndex;
        return new(index, intent, state.CachedEligibility[index], state.CachedScores[index],
            state.CachedTargetEntityIds[index], state.CachedTargetLocationIds[index]);
    }

    private static bool IsCoolingDown(int hash, long minute, DecisionState state)
    {
        for (var index = 0; index < 32; index++)
            if (state.CooldownActionHashes[index] == hash && state.CooldownUntilMinutes[index] > minute) return true;
        return false;
    }

    private static void SetCooldown(CompiledIntent action, long minute, ref DecisionState state)
    {
        if (action.Controls.CooldownMinutes == 0) return;
        var index = ((ReadOnlySpan<int>)state.CooldownActionHashes).IndexOf(action.Hash);
        if (index < 0) index = ((ReadOnlySpan<int>)state.CooldownActionHashes).IndexOf(0);
        if (index < 0) return;
        state.CooldownActionHashes[index] = action.Hash;
        state.CooldownUntilMinutes[index] = minute + action.Controls.CooldownMinutes;
    }

    internal readonly record struct DecisionResult(int IntentIndex, CompiledIntent Action, bool Eligible,
        float Score, int TargetEntityId, int TargetLocationId);
    internal readonly record struct TargetSelection(int EntityId, int LocationId, float Affinity,
        AgentAttributeValues? Attributes = null);
    internal readonly record struct DecisionContext(WorldTime Time, JobDefinition Job, AgentAttributeValues Attributes,
        long TraitMask, AgentLocation Location, AgentTravel Travel);

    internal sealed class TargetResolver
    {
        private readonly AgentSocialIndexes _indexes;

        public TargetResolver(AgentSocialIndexes indexes)
        {
            _indexes = indexes ?? throw new ArgumentNullException(nameof(indexes));
        }

        public TargetSelection Resolve(int actorId, CompiledTargetSelector definition, DecisionContext context)
        {
            if (definition.Kind == TargetKind.None) return default;
            if (definition.Kind == TargetKind.Location)
                return new TargetSelection(0, definition.Location switch
                {
                    LocationValue.Home => context.Location.HomeLocationId,
                    LocationValue.Work => context.Location.WorkLocationId,
                    LocationValue.Current => context.Location.CurrentLocationId,
                    _ => 0
                }, 0);
            var query = definition.Query!;
            var best = default(TargetSelection);
            if (query.Relation == TargetRelationKind.Social)
            {
                foreach (var edge in _indexes.GetOutgoingEdges(actorId))
                    Consider(actorId, edge.TargetAgentId, GetAffinity(edge), query, context, ref best);
            }
            else if (_indexes.TryGetAgent(actorId, out var actor))
            {
                foreach (var membership in actor.GetRelations<AgentNetworkMembership>())
                {
                    if (membership.Network.IsNull ||
                        !membership.Network.TryGetComponent<AgentNetworkData>(out var network) ||
                        network.TypeHash != query.NetworkTypeHash) continue;
                    if (query.Relation == TargetRelationKind.NetworkSupervisor && !membership.Supervisor.IsNull)
                        Consider(actorId, membership.Supervisor.Id, GetAffinity(actorId, membership.Supervisor.Id), query, context, ref best);
                    else if (query.Relation is TargetRelationKind.NetworkMember or TargetRelationKind.NetworkDirectReport)
                        foreach (var link in membership.Network.GetIncomingLinks<AgentNetworkMembership>())
                        {
                            var candidate = link.Entity;
                            if (candidate.Id == actorId || query.Relation == TargetRelationKind.NetworkDirectReport &&
                                (!candidate.TryGetRelation<AgentNetworkMembership, Entity>(membership.Network, out var candidateMembership) ||
                                 candidateMembership.Supervisor.IsNull || candidateMembership.Supervisor.Id != actorId)) continue;
                            Consider(actorId, candidate.Id, GetAffinity(actorId, candidate.Id), query, context, ref best);
                        }
                }
            }
            return best;
        }

        public bool HasSocialRelations(int actorId) => _indexes.GetOutgoingRelationshipCount(actorId) != 0;
        public bool HasNetworkRelations(int actorId)
        {
            if (!_indexes.TryGetAgent(actorId, out var actor)) return false;
            foreach (var _ in actor.GetRelations<AgentNetworkMembership>()) return true;
            return false;
        }

        public bool IsRelated(int actorId, int targetId, CompiledTargetQuery query)
        {
            if (query.Relation == TargetRelationKind.Social)
                return _indexes.TryGetDirectedEdge(actorId, targetId, out _);
            if (!_indexes.TryGetAgent(actorId, out var actor)) return false;
            foreach (var membership in actor.GetRelations<AgentNetworkMembership>())
            {
                if (membership.Network.IsNull || !membership.Network.TryGetComponent<AgentNetworkData>(out var network) ||
                    network.TypeHash != query.NetworkTypeHash) continue;
                if (query.Relation == TargetRelationKind.NetworkSupervisor)
                    return !membership.Supervisor.IsNull && membership.Supervisor.Id == targetId;
                if (!_indexes.TryGetAgent(targetId, out var target) ||
                    !target.TryGetRelation<AgentNetworkMembership, Entity>(membership.Network, out var targetMembership)) continue;
                if (query.Relation == TargetRelationKind.NetworkMember ||
                    query.Relation == TargetRelationKind.NetworkDirectReport && !targetMembership.Supervisor.IsNull &&
                    targetMembership.Supervisor.Id == actorId) return true;
            }
            return false;
        }

        public TargetSelection ResolveSpecific(int actorId, int targetId)
        {
            if (!TryReadTarget(targetId, out var location, out var attributes)) return default;
            return new TargetSelection(targetId, location, GetAffinity(actorId, targetId), attributes);
        }

        private void Consider(int actorId, int candidateId, float affinity, CompiledTargetQuery query,
            DecisionContext context, ref TargetSelection best)
        {
            if (!TryReadTarget(candidateId, out var location, out var attributes)) return;
            var facts = new DecisionFactContext(context.Time, context.Job, context.Attributes,
                context.Location, context.Travel, candidateId, affinity, location, attributes);
            foreach (var requirement in query.Requirements) if (!requirement.Evaluate(facts)) return;
            var candidate = new TargetSelection(candidateId, location, affinity, attributes);
            if (best.EntityId == 0 || Compare(candidate, best, query.RankBy, context) < 0) best = candidate;
        }

        private static int Compare(TargetSelection left, TargetSelection right,
            IReadOnlyList<CompiledTargetRank> ranks, DecisionContext context)
        {
            var leftFacts = new DecisionFactContext(context.Time, context.Job, context.Attributes, context.Location,
                context.Travel, left.EntityId, left.Affinity, left.LocationId, left.Attributes);
            var rightFacts = new DecisionFactContext(context.Time, context.Job, context.Attributes, context.Location,
                context.Travel, right.EntityId, right.Affinity, right.LocationId, right.Attributes);
            foreach (var rank in ranks)
            {
                var comparison = rank.Value.Evaluate(leftFacts).CompareTo(rank.Value.Evaluate(rightFacts));
                if (comparison != 0) return rank.Order == SortOrder.Descending ? -comparison : comparison;
            }
            return left.EntityId.CompareTo(right.EntityId);
        }

        private bool TryReadTarget(int id, out int location, out AgentAttributeValues attributes)
        {
            location = 0; attributes = default;
            if (!_indexes.TryGetAgent(id, out var entity) ||
                !entity.TryGetComponent<AgentLocation>(out var agentLocation) ||
                !entity.TryGetComponent<AgentAttributes>(out var agentAttributes)) return false;
            location = agentLocation.CurrentLocationId; attributes = agentAttributes.Values; return true;
        }

        private float GetAffinity(int sourceId, int targetId) =>
            _indexes.TryGetDirectedEdge(sourceId, targetId, out var edge) ? GetAffinity(edge) : 0.5f;

        private float GetAffinity(SocialEdgeIndexEntry edge) =>
            _indexes.TryGetEdge(edge.EdgeEntityId, out var entity) && entity.TryGetComponent<EdgeData>(out var data)
                ? Math.Clamp((data.Affinity + 100f) / 200f, 0f, 1f) : 0.5f;
    }

    private sealed class CandidateEvaluator
    {
        public CandidateEvaluator(CompiledIntent definition) { Definition = definition; }
        public CompiledIntent Definition { get; }

        public DecisionResult Evaluate(DecisionContext context, TargetSelection target,
            float[]? utilityDiagnostics = null, float[]? traitDiagnostics = null)
        {
            var facts = new DecisionFactContext(context.Time, context.Job, context.Attributes, context.Location,
                context.Travel, target.EntityId, target.Affinity, target.LocationId, target.Attributes);
            if (!Definition.Eligibility.Evaluate(facts))
            {
                if (utilityDiagnostics is not null) Array.Clear(utilityDiagnostics);
                if (traitDiagnostics is not null) Array.Clear(traitDiagnostics);
                return new DecisionResult(Definition.RuntimeIndex, Definition, false, float.NegativeInfinity, target.EntityId, target.LocationId);
            }
            var score = Definition.BaseUtility;
            for (var index = 0; index < Definition.UtilityInputs.Length; index++)
            {
                var input = Definition.UtilityInputs[index];
                var contribution = input.Weight * DecisionUtility.Curve(input.Curve, input.Expression.Evaluate(facts));
                score += contribution;
                if (utilityDiagnostics is not null) utilityDiagnostics[index] = contribution;
            }
            for (var index = 0; index < Definition.TraitModifiers.Length; index++)
            {
                var modifier = Definition.TraitModifiers[index];
                var contribution = (context.TraitMask & modifier.TraitBit) != 0 ? modifier.Modifier : 0;
                score += contribution;
                if (traitDiagnostics is not null) traitDiagnostics[index] = contribution;
            }
            return new DecisionResult(Definition.RuntimeIndex, Definition, true, score, target.EntityId, target.LocationId);
        }
    }
}

// Effects are rates, not decisions. They consume simulation time and the
// currently performed public activity, never rendering-frame count.
public sealed class ActivityEffectsSystem : QuerySystem<AgentAttributes, ActivityState, DecisionState>
{
    private readonly Entity _clock;
    private readonly AgentAttributeSchema _schema;
    private readonly Dictionary<(int ActionHash, int ActivityTypeHash), (int Index, float Rate, EffectSubject Subject)[]> _effects;

    public ActivityEffectsSystem(ContentCatalog catalog, Entity clock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _clock = clock;
        _schema = catalog.AgentAttributes;
        _effects = catalog.Intents.All.ToDictionary(intent => (intent.Hash, intent.Activity.Hash), intent => intent.Effects
            .Select(effect => (effect.AttributeIndex, effect.PerMinute, effect.Subject)).ToArray());
        Filter.AnyTags(Tags.Get<Tier1LodTag, Tier2LodTag>());
    }

    protected override void OnUpdate()
    {
        var minutes = (float)(_clock.GetComponent<WorldTime>().DeltaSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute);
        if (minutes <= 0f) return;
        Query.ForEachEntity((ref AgentAttributes attributes, ref ActivityState activity, ref DecisionState decision, Entity entity) =>
        {
            if (activity.Phase != ActivityPhase.Performing) return;
            if (!_effects.TryGetValue((activity.ActionHash, activity.ActivityTypeHash), out var effects)) return;
            var role = entity.HasComponent<CoordinationState>()
                ? entity.GetComponent<CoordinationState>().Role : CoordinationRole.None;
            foreach (var (index, rate, subject) in effects)
            {
                if (subject == EffectSubject.Participant && role != CoordinationRole.Participant ||
                    subject == EffectSubject.Initiator && role == CoordinationRole.Participant) continue;
                var definition = _schema.Definitions[index];
                var previous = attributes.Values[index];
                attributes.Values[index] = Math.Clamp(previous + rate * minutes, definition.Min, definition.Max);
                if (attributes.Values[index] != previous) DecisionInvalidation.SignalAttribute(ref decision, index);
            }
        });
    }
}
