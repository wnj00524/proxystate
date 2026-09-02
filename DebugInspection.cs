using System.Collections.ObjectModel;
using System.Numerics;
using Friflo.Engine.ECS;
using ImGuiNET;
using ProxyState.Simulation;

namespace ProxyState;

public static class DebugMode
{
    public static bool IsEnabled(IEnumerable<string>? arguments)
    {
        return arguments?.Any(argument =>
            string.Equals(argument, "-debug", StringComparison.OrdinalIgnoreCase)) == true;
    }
}

public sealed record DebugAttributeSnapshot(string Id, float Value);

public sealed record DebugTraitSnapshot(string Id, string Name, long Bit, bool IsPresent);

public sealed record DebugLocationSnapshot(int Id, string Name);

public sealed record DebugTravelSnapshot(
    IReadOnlyList<DebugLocationSnapshot> Route,
    int TotalTravelMinutes,
    int RoutePosition,
    float RemainingTravelMinutes,
    AgentTravelMode Mode);

public sealed record DebugNetworkMembershipSnapshot(
    int NetworkEntityId,
    string NetworkDisplayName,
    string NetworkTypeName,
    int RoleHash,
    string RoleName,
    int? SupervisorEntityId,
    string? SupervisorDisplayName);

public sealed record DebugNetworkSnapshot(
    int EntityId,
    string DisplayName,
    int TypeHash,
    string TypeName,
    DebugLocationSnapshot? Anchor,
    int MemberCount);

public sealed record DebugCoordinationSnapshot(
    int PartnerEntityId,
    CoordinationRole Role,
    CoordinationStatus Status,
    long AcceptedAtMinute,
    long StartedAtMinute,
    int MinimumDurationMinutes,
    int MaximumDurationMinutes,
    float Utility,
    bool ReleaseRequested);

public sealed record DebugDecisionContributionSnapshot(string Label, float Value);

public sealed record DebugDecisionCandidateSnapshot(
    string IntentId,
    string IntentName,
    bool Eligible,
    string? RejectedPredicate,
    int TargetEntityId,
    int TargetLocationId,
    float BaseUtility,
    IReadOnlyList<DebugDecisionContributionSnapshot> UtilityContributions,
    IReadOnlyList<DebugDecisionContributionSnapshot> TraitModifiers,
    long CooldownUntilMinute,
    bool CommitmentBlocked,
    float FinalScore,
    bool SelectedWinner);

public sealed record DebugInspectionSnapshot(
    IReadOnlyList<DebugAgentSnapshot> Agents,
    IReadOnlyList<DebugNetworkSnapshot> Networks);

public sealed record DebugAgentIdentitySnapshot(int EntityId, int NameId)
{
    public string DisplayName => $"Agent {EntityId} (Name ID {NameId})";
}

/// <summary>Presentation copy containing cheap rows and at most one full agent.</summary>
public sealed record DebugInspectionView(
    IReadOnlyList<DebugAgentIdentitySnapshot> AgentIdentities,
    IReadOnlyList<DebugNetworkSnapshot> Networks,
    DebugAgentSnapshot? SelectedAgent);

public sealed class DebugAgentIdentitySearchIndex
{
    private string _search = string.Empty;
    private IReadOnlyList<DebugAgentIdentitySnapshot>? _identities;
    private int[] _matches = [];
    public int RebuildCount { get; private set; }

    public IReadOnlyList<int> Update(IReadOnlyList<DebugAgentIdentitySnapshot> identities, string? search)
    {
        search = search?.Trim() ?? string.Empty;
        if (ReferenceEquals(identities, _identities) && search == _search) return _matches;
        _identities = identities;
        _search = search;
        _matches = identities.Select((identity, index) => (identity, index))
            .Where(item => search.Length == 0 || item.identity.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index).ToArray();
        RebuildCount++;
        return _matches;
    }
}

// These records intentionally contain only copied values. The UI can inspect
// them freely without retaining an ECS Entity or a mutable component reference.
public sealed record DebugAgentSnapshot(
    int EntityId,
    int NameId,
    int OccupationId,
    string OccupationName,
    IntelligenceRole IntelligenceRole,
    byte FactionId,
    string FactionName,
    IReadOnlyList<DebugAttributeSnapshot> Attributes,
    IReadOnlyList<DebugTraitSnapshot> Traits,
    long TraitMask,
    int CurrentActionHash,
    string CurrentActionName,
    int ActivityTypeHash,
    string ActivityTypeName,
    ActivityPhase ActivityPhase,
    int SecretStateHash,
    string SecretStateName,
    DebugLocationSnapshot Home,
    DebugLocationSnapshot Workplace,
    DebugLocationSnapshot CurrentLocation,
    DebugTravelSnapshot Travel,
    DebugCoordinationSnapshot? Coordination,
    IReadOnlyList<DebugDecisionCandidateSnapshot> Decisions,
    IReadOnlyList<DebugNetworkMembershipSnapshot> Networks,
    AgentLodTier LodTier,
    AgentLodTier DesiredLodTier,
    long? PendingDemotionMinute,
    int CoarseProfileId,
    ulong CoarseProfileFingerprint)
{
    public string DisplayName => $"Agent {EntityId} (Name ID {NameId})";
}

public static class DebugSnapshotBuilder
{
    public static IReadOnlyList<DebugAgentSnapshot> Capture(EntityStore store, ContentCatalog catalog)
        => CaptureInspection(store, catalog).Agents;

    /// <summary>Copies detail for one stable ID; no other agent detail is built.</summary>
    public static DebugAgentSnapshot? CaptureSelectedAgent(EntityStore store, ContentCatalog catalog, int entityId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        var entity = store.Query<Identity>().Entities.FirstOrDefault(candidate => candidate.Id == entityId);
        if (entity.IsNull) return null;

        var memberships = new List<DebugNetworkMembershipSnapshot>();
        foreach (var network in store.Query<AgentNetworkData>().Entities)
        {
            if (!network.GetIncomingLinks<AgentNetworkMembership>().Any(link => link.Entity.Id == entityId)) continue;
            var data = network.GetComponent<AgentNetworkData>();
            var type = catalog.Networks.GetType(data.TypeHash);
            var membership = entity.GetRelation<AgentNetworkMembership, Entity>(network);
            var role = catalog.Networks.GetRole(membership.RoleHash);
            var supervisorId = membership.Supervisor.IsNull ? (int?)null : membership.Supervisor.Id;
            memberships.Add(new DebugNetworkMembershipSnapshot(
                network.Id, $"{type.Name} {data.Ordinal + 1}", type.Name,
                role.Hash, role.Name, supervisorId,
                supervisorId is null ? null : DescribeAgent(membership.Supervisor)));
        }

        var identity = entity.GetComponent<Identity>();
        var faction = entity.GetComponent<PoliticalAlignment>();
        var attributes = entity.GetComponent<AgentAttributes>();
        var psychology = entity.GetComponent<Psychology>();
        var state = entity.GetComponent<AgentState>();
        // Tier 3 agents deliberately shed detailed activity and travel state. The
        // debug viewer must inspect that coarse representation without promoting
        // the entity or assuming those components are still materialized.
        var activity = entity.HasComponent<ActivityState>() ? entity.GetComponent<ActivityState>() : default;
        var location = entity.GetComponent<AgentLocation>();
        var travel = entity.HasComponent<AgentTravel>() ? entity.GetComponent<AgentTravel>() : default;
        var intention = entity.HasComponent<IntentionState>() ? entity.GetComponent<IntentionState>() : default;
        var decision = entity.HasComponent<DecisionState>() ? entity.GetComponent<DecisionState>() : default;
        var lod = entity.HasComponent<AgentLodState>() ? entity.GetComponent<AgentLodState>() : new AgentLodState
        {
            DesiredTier = AgentLodTier.Tier1,
            ScheduledDemotionMinute = -1
        };
        var jobName = catalog.Jobs.FirstOrDefault(job => job.Hash == identity.OccupationId)?.Name
            ?? $"Unknown ({identity.OccupationId})";
        var factionName = catalog.Factions.FirstOrDefault(item => item.FactionId == faction.FactionId)?.Name
            ?? $"Unknown ({faction.FactionId})";
        var actionName = catalog.Actions.FirstOrDefault(action => action.Hash == activity.ActionHash)?.Name
            ?? $"Unknown ({activity.ActionHash})";
        string activityName;
        try { activityName = catalog.GetActivity(activity.ActivityTypeHash).Name; }
        catch (KeyNotFoundException) { activityName = $"Unknown ({activity.ActivityTypeHash})"; }
        var secretName = catalog.SecretStates.FirstOrDefault(secret => secret.Hash == state.SecretStateHash)?.Name
            ?? $"Unknown ({state.SecretStateHash})";

        return new DebugAgentSnapshot(
            entity.Id, identity.NameId, identity.OccupationId, jobName, identity.IntelligenceRole,
            faction.FactionId, factionName, CopyAttributes(attributes, catalog.AgentAttributes),
            CopyTraits(psychology.TraitMask, catalog.Traits), psychology.TraitMask,
            activity.ActionHash, actionName, activity.ActivityTypeHash, activityName, activity.Phase,
            state.SecretStateHash, secretName,
            DescribeLocation(location.HomeLocationId, catalog.World),
            DescribeLocation(location.WorkLocationId, catalog.World),
            DescribeLocation(location.CurrentLocationId, catalog.World),
            new DebugTravelSnapshot(
                (travel.RouteLocationIds ?? []).Select(id => DescribeLocation(id, catalog.World)).ToArray().AsReadOnly(),
                travel.TotalTravelMinutes, travel.RoutePosition, travel.RemainingTravelMinutes, travel.Mode),
            CopyCoordination(entity), CopyDecisions(catalog, intention, decision),
            memberships.OrderBy(item => item.NetworkEntityId).ToArray().AsReadOnly(),
            GetMaterializedTier(entity), lod.DesiredTier,
            lod.ScheduledDemotionMinute >= 0 ? lod.ScheduledDemotionMinute : null,
            lod.CoarseProfileId, lod.CoarseProfileFingerprint);
    }

    public static DebugInspectionSnapshot CaptureInspection(EntityStore store, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        var jobsByHash = catalog.Jobs.ToDictionary(job => job.Hash);
        var factionsById = catalog.Factions.ToDictionary(faction => faction.FactionId);
        var actionsByHash = catalog.Actions.ToDictionary(action => action.Hash);
        var secretStatesByHash = catalog.SecretStates.ToDictionary(secretState => secretState.Hash);
        var networkMembershipsByAgent = new Dictionary<int, List<DebugNetworkMembershipSnapshot>>();
        var networkSnapshots = new List<DebugNetworkSnapshot>();
        var snapshots = new List<DebugAgentSnapshot>();

        // Incoming links constrain each network-wide pass to that network's packed
        // relation pairs. Every membership is copied once, without a population scan.
        foreach (var network in store.Query<AgentNetworkData>().Entities)
        {
            var data = network.GetComponent<AgentNetworkData>();
            var type = catalog.Networks.GetType(data.TypeHash);
            var displayName = $"{type.Name} {data.Ordinal + 1}";
            var memberCount = 0;
            foreach (var link in network.GetIncomingLinks<AgentNetworkMembership>())
            {
                var agent = link.Entity;
                var membership = agent.GetRelation<AgentNetworkMembership, Entity>(network);
                var role = catalog.Networks.GetRole(membership.RoleHash);
                var supervisorId = membership.Supervisor.IsNull ? (int?)null : membership.Supervisor.Id;
                var copied = new DebugNetworkMembershipSnapshot(
                    network.Id, displayName, type.Name, role.Hash, role.Name, supervisorId,
                    supervisorId is null ? null : DescribeAgent(membership.Supervisor));
                if (!networkMembershipsByAgent.TryGetValue(agent.Id, out var memberships))
                    networkMembershipsByAgent.Add(agent.Id, memberships = new());
                memberships.Add(copied);
                memberCount++;
            }

            networkSnapshots.Add(new DebugNetworkSnapshot(
                network.Id, displayName, data.TypeHash, type.Name,
                data.AnchorLocationId == 0 ? null : DescribeLocation(data.AnchorLocationId, catalog.World),
                memberCount));
        }

        foreach (var entity in store.Query<Identity>().Entities)
        {
            var identity = entity.GetComponent<Identity>();
            var faction = entity.GetComponent<PoliticalAlignment>();
            var attributes = entity.GetComponent<AgentAttributes>();
            var psychology = entity.GetComponent<Psychology>();
            var state = entity.GetComponent<AgentState>();
            var activity = entity.GetComponent<ActivityState>();
            var location = entity.GetComponent<AgentLocation>();
            var travel = entity.GetComponent<AgentTravel>();
            var intention = entity.HasComponent<IntentionState>()
                ? entity.GetComponent<IntentionState>() : default;
            var decision = entity.HasComponent<DecisionState>()
                ? entity.GetComponent<DecisionState>() : default;

            var jobName = jobsByHash.TryGetValue(identity.OccupationId, out var job)
                ? job.Name
                : $"Unknown ({identity.OccupationId})";
            var factionName = factionsById.TryGetValue(faction.FactionId, out var factionDefinition)
                ? factionDefinition.Name
                : $"Unknown ({faction.FactionId})";
            var actionName = actionsByHash.TryGetValue(activity.ActionHash, out var action)
                ? action.Name
                : $"Unknown ({activity.ActionHash})";
            string activityName;
            try { activityName = catalog.GetActivity(activity.ActivityTypeHash).Name; }
            catch (KeyNotFoundException) { activityName = $"Unknown ({activity.ActivityTypeHash})"; }
            var secretStateName = secretStatesByHash.TryGetValue(state.SecretStateHash, out var secretState)
                ? secretState.Name
                : $"Unknown ({state.SecretStateHash})";

            snapshots.Add(new DebugAgentSnapshot(
                entity.Id,
                identity.NameId,
                identity.OccupationId,
                jobName,
                identity.IntelligenceRole,
                faction.FactionId,
                factionName,
                CopyAttributes(attributes, catalog.AgentAttributes),
                CopyTraits(psychology.TraitMask, catalog.Traits),
                psychology.TraitMask,
                activity.ActionHash,
                actionName,
                activity.ActivityTypeHash,
                activityName,
                activity.Phase,
                state.SecretStateHash,
                secretStateName,
                DescribeLocation(location.HomeLocationId, catalog.World),
                DescribeLocation(location.WorkLocationId, catalog.World),
                DescribeLocation(location.CurrentLocationId, catalog.World),
                new DebugTravelSnapshot(
                    travel.RouteLocationIds
                        .Select(routeLocationId => DescribeLocation(routeLocationId, catalog.World))
                        .ToArray()
                        .AsReadOnly(),
                    travel.TotalTravelMinutes,
                    travel.RoutePosition,
                    travel.RemainingTravelMinutes,
                    travel.Mode),
                CopyCoordination(entity),
                CopyDecisions(catalog, intention, decision),
                (networkMembershipsByAgent.TryGetValue(entity.Id, out var memberships)
                    ? memberships.OrderBy(item => item.NetworkEntityId).ToArray()
                    : Array.Empty<DebugNetworkMembershipSnapshot>()).AsReadOnly(),
                GetMaterializedTier(entity),
                entity.HasComponent<AgentLodState>() ? entity.GetComponent<AgentLodState>().DesiredTier : AgentLodTier.Tier1,
                entity.HasComponent<AgentLodState>() && entity.GetComponent<AgentLodState>().ScheduledDemotionMinute >= 0
                    ? entity.GetComponent<AgentLodState>().ScheduledDemotionMinute : null,
                entity.HasComponent<AgentLodState>() ? entity.GetComponent<AgentLodState>().CoarseProfileId : 0,
                entity.HasComponent<AgentLodState>() ? entity.GetComponent<AgentLodState>().CoarseProfileFingerprint : 0));
        }

        return new DebugInspectionSnapshot(
            snapshots.AsReadOnly(),
            networkSnapshots.OrderBy(network => network.EntityId).ToArray().AsReadOnly());
    }

    private static AgentLodTier GetMaterializedTier(Entity entity) =>
        entity.Tags.Has<Tier1LodTag>() ? AgentLodTier.Tier1 :
        entity.Tags.Has<Tier2LodTag>() ? AgentLodTier.Tier2 : AgentLodTier.Tier3;

    private static DebugCoordinationSnapshot? CopyCoordination(Entity entity)
    {
        if (!entity.HasComponent<CoordinationState>()) return null;
        var coordination = entity.GetComponent<CoordinationState>();
        return !coordination.Active ? null : new DebugCoordinationSnapshot(
            coordination.PartnerEntityId, coordination.Role, coordination.Status,
            coordination.AcceptedAtMinute, coordination.StartedAtMinute,
            coordination.MinimumDurationMinutes, coordination.MaximumDurationMinutes,
            coordination.Utility, coordination.ReleaseRequested);
    }

    private static IReadOnlyList<DebugDecisionCandidateSnapshot> CopyDecisions(
        ContentCatalog catalog, IntentionState intention, DecisionState decision)
    {
        if (decision.CachedScores is null) return Array.Empty<DebugDecisionCandidateSnapshot>();
        var snapshots = new List<DebugDecisionCandidateSnapshot>();
        foreach (var intent in catalog.Intents.All.Where(intent => !intent.Fallback))
        {
            var index = intent.RuntimeIndex;
            var utility = decision.CachedUtilityContributions?.ElementAtOrDefault(index) ?? Array.Empty<float>();
            var traits = decision.CachedTraitContributions?.ElementAtOrDefault(index) ?? Array.Empty<float>();
            var cooldownUntil = 0L;
            if (decision.CooldownActionHashes is not null && decision.CooldownUntilMinutes is not null)
            {
                var cooldownIndex = Array.IndexOf(decision.CooldownActionHashes, intent.Hash);
                if (cooldownIndex >= 0) cooldownUntil = decision.CooldownUntilMinutes[cooldownIndex];
            }
            snapshots.Add(new DebugDecisionCandidateSnapshot(
                intent.Id, intent.Name, decision.CachedEligibility[index],
                string.IsNullOrEmpty(decision.CachedRejectedPredicates?.ElementAtOrDefault(index))
                    ? null : decision.CachedRejectedPredicates[index],
                decision.CachedTargetEntityIds[index], decision.CachedTargetLocationIds[index], intent.BaseUtility,
                utility.Select((value, i) => new DebugDecisionContributionSnapshot($"utilityInputs[{i}]", value)).ToArray(),
                traits.Select((value, i) => new DebugDecisionContributionSnapshot(
                    catalog.Traits.FirstOrDefault(trait => trait.Bit == intent.TraitModifiers[i].TraitBit)?.Id ?? $"traitModifiers[{i}]", value)).ToArray(),
                cooldownUntil,
                intention.ActionHash == intent.Hash && decision.LastConsideredMinute - intention.SelectedAtMinute < intent.Controls.MinimumCommitmentMinutes,
                decision.CachedScores[index], intention.ActionHash == intent.Hash));
        }
        return snapshots.AsReadOnly();
    }

    private static IReadOnlyList<DebugAttributeSnapshot> CopyAttributes(
        AgentAttributes attributes,
        AgentAttributeSchema schema)
    {
        var copied = new List<DebugAttributeSnapshot>(schema.Count);
        for (var index = 0; index < schema.Count; index++)
        {
            copied.Add(new DebugAttributeSnapshot(schema.Definitions[index].Id, attributes.Values[index]));
        }

        return new ReadOnlyCollection<DebugAttributeSnapshot>(copied);
    }

    private static IReadOnlyList<DebugTraitSnapshot> CopyTraits(
        long traitMask,
        IReadOnlyList<TraitDefinition> traits)
    {
        return traits
            .Select(trait => new DebugTraitSnapshot(
                trait.Id,
                trait.Name,
                trait.Bit,
                (traitMask & trait.Bit) != 0))
            .ToArray()
            .AsReadOnly();
    }

    private static DebugLocationSnapshot DescribeLocation(int locationId, WorldTopology world)
    {
        try
        {
            var location = world.GetLocation(locationId);
            return new DebugLocationSnapshot(locationId, location.Name);
        }
        catch (KeyNotFoundException)
        {
            return new DebugLocationSnapshot(locationId, $"Unknown ({locationId})");
        }
    }

    private static string DescribeAgent(Entity agent)
    {
        var identity = agent.GetComponent<Identity>();
        return $"Agent {agent.Id} (Name ID {identity.NameId})";
    }
}

/// <summary>
/// Simulation-side owner of the debug projection. Stable identity rows and
/// network summaries are copied once; selected detail is copied only when the
/// requested stable ID changes.
/// </summary>
public sealed class DebugInspectionProjection
{
    private readonly EntityStore _store;
    private readonly ContentCatalog _catalog;
    private readonly ReadOnlyCollection<DebugAgentIdentitySnapshot> _identities;
    private readonly ReadOnlyCollection<DebugNetworkSnapshot> _networks;
    private int? _capturedAgentId;
    private DebugAgentSnapshot? _selectedAgent;

    private DebugInspectionProjection(EntityStore store, ContentCatalog catalog,
        DebugAgentIdentitySnapshot[] identities, DebugNetworkSnapshot[] networks)
    {
        _store = store;
        _catalog = catalog;
        _identities = Array.AsReadOnly(identities);
        _networks = Array.AsReadOnly(networks);
    }

    public int SelectedDetailCaptureCount { get; private set; }
    public DebugInspectionView View => new(_identities, _networks, _selectedAgent);

    public static DebugInspectionProjection Create(EntityStore store, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        var identities = store.Query<Identity>().Entities
            .Select(entity => new DebugAgentIdentitySnapshot(entity.Id, entity.GetComponent<Identity>().NameId))
            .OrderBy(identity => identity.EntityId).ToArray();
        var networks = store.Query<AgentNetworkData>().Entities.Select(network =>
        {
            var data = network.GetComponent<AgentNetworkData>();
            var type = catalog.Networks.GetType(data.TypeHash);
            var count = network.GetIncomingLinks<AgentNetworkMembership>().Count();
            return new DebugNetworkSnapshot(network.Id, $"{type.Name} {data.Ordinal + 1}", data.TypeHash,
                type.Name, data.AnchorLocationId == 0 ? null : DescribeDebugLocation(data.AnchorLocationId, catalog.World), count);
        }).OrderBy(network => network.EntityId).ToArray();
        return new DebugInspectionProjection(store, catalog, identities, networks);
    }

    public void Select(int? entityId)
    {
        if (_capturedAgentId == entityId) return;
        _capturedAgentId = entityId;
        _selectedAgent = entityId is null ? null : DebugSnapshotBuilder.CaptureSelectedAgent(_store, _catalog, entityId.Value);
        if (entityId is not null) SelectedDetailCaptureCount++;
    }

    private static DebugLocationSnapshot DescribeDebugLocation(int id, WorldTopology world)
    {
        try { return new DebugLocationSnapshot(id, world.GetLocation(id).Name); }
        catch (KeyNotFoundException) { return new DebugLocationSnapshot(id, $"Unknown ({id})"); }
    }
}

public sealed class DebugWindow
{
    private int? _selectedAgentId;
    private string _search = string.Empty;
    private readonly DebugAgentIdentitySearchIndex _searchIndex = new();

    public int? SelectedAgentId => _selectedAgentId;

    public unsafe void Draw(DebugInspectionView inspection, ref bool isOpen)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var agents = inspection.AgentIdentities;

        if (_selectedAgentId is not null && agents.All(agent => agent.EntityId != _selectedAgentId.Value))
        {
            _selectedAgentId = null;
        }

        if (!ImGui.Begin(ApplicationShell.DebugWindowTitle, ref isOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.Text("Debug mode: ON");
        ImGui.Text($"Agents: {agents.Count}");
        ImGui.Text($"Networks: {inspection.Networks.Count}");
        if (ImGui.CollapsingHeader("Network summary"))
        {
            foreach (var network in inspection.Networks)
            {
                var anchor = network.Anchor is null ? "Unanchored" : FormatLocation(network.Anchor);
                ImGui.BulletText($"{network.DisplayName}: {anchor}; {network.MemberCount} members");
            }
        }
        ImGui.Separator();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##debug-search", "Search agent ID or name", ref _search, 128);

        ImGui.BeginChild("debug-agent-list", new Vector2(280, 0), ImGuiChildFlags.Borders);
        var matches = _searchIndex.Update(agents, _search);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(matches.Count);
        while (clipper.Step())
        {
            VisibleRowRange.Visit(matches.Count, clipper.DisplayStart, clipper.DisplayEnd, row =>
            {
                var agent = agents[matches[row]];
                if (ImGui.Selectable(agent.DisplayName, agent.EntityId == _selectedAgentId))
                    _selectedAgentId = agent.EntityId;
            });
        }
        clipper.End();
        clipper.Destroy();

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("debug-agent-details", new Vector2(0, 0), ImGuiChildFlags.Borders);

        var selectedAgent = inspection.SelectedAgent?.EntityId == _selectedAgentId ? inspection.SelectedAgent : null;
        if (selectedAgent is null)
        {
            ImGui.Text("Select an agent to inspect its details.");
        }
        else
        {
            DrawDetails(selectedAgent);
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private static void DrawDetails(DebugAgentSnapshot agent)
    {
        ImGui.Text(agent.DisplayName);
        ImGui.Separator();
        ImGui.Text("Identity");
        ImGui.BulletText($"Entity ID: {agent.EntityId}");
        ImGui.BulletText($"Name ID: {agent.NameId}");
        ImGui.BulletText($"Intelligence role: {agent.IntelligenceRole}");
        ImGui.BulletText($"Occupation: {agent.OccupationName} ({agent.OccupationId})");
        ImGui.BulletText($"Faction: {agent.FactionName} ({agent.FactionId})");

        ImGui.Separator();
        ImGui.Text("LOD projection (debug only)");
        ImGui.BulletText($"Materialized / desired tier: {agent.LodTier} / {agent.DesiredLodTier}");
        ImGui.BulletText(agent.PendingDemotionMinute is null
            ? "Pending demotion: None" : $"Pending demotion minute: {agent.PendingDemotionMinute}");
        ImGui.BulletText($"Coarse profile: {agent.CoarseProfileId} (0x{agent.CoarseProfileFingerprint:X})");

        ImGui.Separator();
        ImGui.Text("Attributes");
        foreach (var attribute in agent.Attributes)
        {
            ImGui.BulletText($"{attribute.Id}: {attribute.Value:0.###}");
        }

        ImGui.Separator();
        ImGui.Text($"Psychology (mask: 0x{agent.TraitMask:X})");
        foreach (var trait in agent.Traits)
        {
            ImGui.BulletText($"{trait.Name}: {(trait.IsPresent ? "Present" : "Absent")} (bit 0x{trait.Bit:X})");
        }

        ImGui.Separator();
        ImGui.Text("State");
        ImGui.BulletText($"Current action: {agent.CurrentActionName} ({agent.CurrentActionHash})");
        ImGui.BulletText($"Activity: {agent.ActivityTypeName} ({agent.ActivityTypeHash})");
        ImGui.BulletText($"Activity phase: {agent.ActivityPhase}");
        ImGui.BulletText($"Secret state: {agent.SecretStateName} ({agent.SecretStateHash})");
        ImGui.BulletText($"Home: {FormatLocation(agent.Home)}");
        ImGui.BulletText($"Workplace: {FormatLocation(agent.Workplace)}");
        ImGui.BulletText($"Current location: {FormatLocation(agent.CurrentLocation)}");

        ImGui.Separator();
        ImGui.Text("Coordination");
        if (agent.Coordination is null)
        {
            ImGui.BulletText("None");
        }
        else
        {
            ImGui.BulletText($"Partner entity: {agent.Coordination.PartnerEntityId}");
            ImGui.BulletText($"Role/status: {agent.Coordination.Role} / {agent.Coordination.Status}");
            ImGui.BulletText($"Accepted/start minute: {agent.Coordination.AcceptedAtMinute} / {agent.Coordination.StartedAtMinute}");
            ImGui.BulletText($"Duration window: {agent.Coordination.MinimumDurationMinutes}-{agent.Coordination.MaximumDurationMinutes} minutes");
            ImGui.BulletText($"Coordination utility: {agent.Coordination.Utility:0.###}");
            ImGui.BulletText($"Release requested: {agent.Coordination.ReleaseRequested}");
        }

        ImGui.Separator();
        ImGui.Text("Decision inspector");
        if (agent.Decisions.Count == 0) ImGui.BulletText("No diagnostic evaluation captured.");
        foreach (var candidate in agent.Decisions)
        {
            if (!ImGui.TreeNode($"{candidate.IntentName}##decision-{candidate.IntentId}")) continue;
            ImGui.BulletText($"Eligible: {candidate.Eligible}");
            if (candidate.RejectedPredicate is not null) ImGui.BulletText($"Rejected: {candidate.RejectedPredicate}");
            ImGui.BulletText($"Target: entity {candidate.TargetEntityId}, location {candidate.TargetLocationId}");
            ImGui.BulletText($"Base utility: {candidate.BaseUtility:0.###}");
            foreach (var item in candidate.UtilityContributions) ImGui.BulletText($"{item.Label}: {item.Value:+0.###;-0.###;0}");
            foreach (var item in candidate.TraitModifiers) ImGui.BulletText($"Trait {item.Label}: {item.Value:+0.###;-0.###;0}");
            ImGui.BulletText($"Cooldown until minute: {candidate.CooldownUntilMinute}");
            ImGui.BulletText($"Commitment block: {candidate.CommitmentBlocked}");
            ImGui.BulletText($"Final score: {candidate.FinalScore:0.###}");
            ImGui.BulletText($"Selected winner: {candidate.SelectedWinner}");
            ImGui.TreePop();
        }

        ImGui.Separator();
        ImGui.Text("Travel");
        ImGui.BulletText($"Mode: {agent.Travel.Mode}");
        ImGui.BulletText($"Total travel: {agent.Travel.TotalTravelMinutes} minutes");
        ImGui.BulletText($"Route position: {agent.Travel.RoutePosition}");
        ImGui.BulletText($"Remaining travel: {agent.Travel.RemainingTravelMinutes:0.##} minutes");
        ImGui.BulletText($"Route: {string.Join(" -> ", agent.Travel.Route.Select(FormatLocation))}");

        ImGui.Separator();
        ImGui.Text("Networks");
        if (agent.Networks.Count == 0) ImGui.BulletText("None");
        foreach (var membership in agent.Networks)
        {
            var supervisor = membership.SupervisorDisplayName ?? "None (root/flat)";
            ImGui.BulletText($"{membership.NetworkDisplayName} ({membership.NetworkTypeName})");
            ImGui.Indent();
            ImGui.BulletText($"Role: {membership.RoleName} ({membership.RoleHash})");
            ImGui.BulletText($"Supervisor: {supervisor}");
            ImGui.Unindent();
        }
    }

    private static string FormatLocation(DebugLocationSnapshot location) =>
        $"{location.Name} ({location.Id})";
}
