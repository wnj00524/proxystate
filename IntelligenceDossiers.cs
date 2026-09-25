using System.Collections.ObjectModel;
using System.Numerics;
using Friflo.Engine.ECS;
using ImGuiNET;
using ProxyState.Simulation;

namespace ProxyState;

/// <summary>
/// The UI-facing copy of one agent's identity and the intelligence known by
/// the Operative team. It intentionally contains no ECS Entity reference.
/// </summary>
public sealed record PlayerIntelligenceAgentSnapshot(
    int EntityId,
    int NameId,
    bool IsOperative,
    IntelligenceRole IntelligenceRole,
    long KnownTraitMask,
    bool IsUnderInvestigation)
{
    public string DisplayName => $"Agent {EntityId} (Name ID {NameId})";
}

/// <summary>
/// Read-only intelligence database captured at the ECS/UI boundary. Each
/// target mask is the union of the directional knowledge held by all current
/// Operatives, so the terminal represents the player's team rather than one
/// specific character.
/// </summary>
public sealed class PlayerIntelligenceDB
{
    private readonly PlayerIntelligenceAgentSnapshot[] _agents;
    private readonly ReadOnlyCollection<PlayerIntelligenceAgentSnapshot> _readOnlyAgents;
    private readonly long _allTraitBits;

    private PlayerIntelligenceDB(
        IReadOnlyList<int> operativeEntityIds,
        PlayerIntelligenceAgentSnapshot[] agents,
        PlayerIntelligenceProjectionDiagnostics diagnostics,
        long allTraitBits)
    {
        OperativeEntityIds = operativeEntityIds;
        _agents = agents;
        _readOnlyAgents = Array.AsReadOnly(_agents);
        Diagnostics = diagnostics;
        _allTraitBits = allTraitBits;
    }

    public IReadOnlyList<int> OperativeEntityIds { get; }

    public IReadOnlyList<PlayerIntelligenceAgentSnapshot> Agents => _readOnlyAgents;

    /// <summary>Sanitized operative schedules, assignments, and task reports.</summary>
    public OperativeManagementProjection? OperativeManagement { get; private set; }

    public void Apply(OperativeManagementProjection projection) =>
        OperativeManagement = projection ?? throw new ArgumentNullException(nameof(projection));

    public PlayerIntelligenceProjectionDiagnostics Diagnostics { get; }

    // Identities are immutable after bootstrap. UI search caches can therefore
    // use this token without being invalidated by trait/investigation deltas.
    public long StableIdentityVersion => 1;

    /// <summary>
    /// Copies only identity, team membership, and known relationship masks out
    /// of the ECS store. Ground-truth Psychology is never placed in the copy.
    /// </summary>
    public static PlayerIntelligenceDB Create(EntityStore store, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        var agents = store.Query<Identity>().Entities
            .OrderBy(entity => entity.Id)
            .ToArray();
        var diagnostics = new PlayerIntelligenceProjectionDiagnostics
        {
            InitializationAgentVisits = agents.Length
        };
        var operativeEntityIds = agents
            .Where(entity => entity.Tags.Has<OperativeTag>())
            .Select(entity => entity.Id)
            .ToHashSet();
        var knownMasksByTarget = new Dictionary<int, long>();

        foreach (var edgeEntity in store.Query<EdgeData>().Entities)
        {
            diagnostics.InitializationEdgeVisits++;
            var edge = edgeEntity.GetComponent<EdgeData>();
            if (!operativeEntityIds.Contains(edge.Source.Id))
            {
                continue;
            }

            var targetId = edge.Target.Id;
            var knownMask = edge.KnownTraitMask & catalog.AllTraitBits;
            knownMasksByTarget[targetId] = knownMasksByTarget.GetValueOrDefault(targetId) | knownMask;
        }

        var operativeIds = new ReadOnlyCollection<int>(operativeEntityIds.OrderBy(id => id).ToList());
        var snapshots = agents
            .Select(entity =>
            {
                var identity = entity.GetComponent<Identity>();
                return new PlayerIntelligenceAgentSnapshot(
                    entity.Id,
                    identity.NameId,
                    operativeEntityIds.Contains(entity.Id),
                    identity.IntelligenceRole,
                    knownMasksByTarget.GetValueOrDefault(entity.Id),
                    IsUnderInvestigation: false);
            })
            .ToArray();

        return new PlayerIntelligenceDB(
            operativeIds,
            snapshots,
            diagnostics,
            catalog.AllTraitBits);
    }

    public bool TryGetAgent(int entityId, out PlayerIntelligenceAgentSnapshot? agent)
    {
        var index = FindIndex(entityId);
        agent = index >= 0 ? _agents[index] : null;
        return index >= 0;
    }

    /// <summary>Applies only copied, sanitized simulation notifications.</summary>
    public bool Apply(OperativeTraitDiscoveryEvent discovery)
    {
        var index = FindIndex(discovery.TargetAgentId);
        if (index < 0) return false;
        var previous = _agents[index];
        var combined = previous.KnownTraitMask | (discovery.KnownTraitMask & _allTraitBits);
        if (combined == previous.KnownTraitMask) return false;
        _agents[index] = previous with { KnownTraitMask = combined };
        Diagnostics.IncrementalUpdates++;
        return true;
    }

    public bool Apply(InvestigationChangedEvent change)
    {
        var index = FindIndex(change.AgentId);
        if (index < 0) return false;
        var previous = _agents[index];
        if (previous.IsUnderInvestigation == change.Enabled) return false;
        _agents[index] = previous with { IsUnderInvestigation = change.Enabled };
        Diagnostics.IncrementalUpdates++;
        return true;
    }

    private int FindIndex(int entityId)
    {
        var low = 0;
        var high = _agents.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = _agents[middle].EntityId.CompareTo(entityId);
            if (comparison == 0) return middle;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }
}

/// <summary>Work counters that distinguish one-time capture from frame deltas.</summary>
public sealed class PlayerIntelligenceProjectionDiagnostics
{
    public long InitializationAgentVisits { get; internal set; }
    public long InitializationEdgeVisits { get; internal set; }
    public long IncrementalUpdates { get; internal set; }
}

public readonly record struct InvestigationCommand(int AgentId, bool Enabled);

/// <summary>
/// Simulation-owned command boundary. Presentation queues stable IDs; only
/// this adapter can ask the LOD service to mutate Ground Truth.
/// </summary>
public sealed class InvestigationCommandQueue
{
    private readonly Queue<InvestigationCommand> _commands = new();

    public int PendingCount => _commands.Count;

    public void Enqueue(InvestigationCommand command) => _commands.Enqueue(command);

    public InvestigationCommandResult Process(AgentLodService lodService, PlayerIntelligenceDB intelligence)
    {
        ArgumentNullException.ThrowIfNull(lodService);
        ArgumentNullException.ThrowIfNull(intelligence);
        var accepted = 0;
        var rejected = 0;
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                lodService.SetInvestigation(command.AgentId, command.Enabled);
                accepted++;
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected++;
            }
        }

        foreach (var change in lodService.DrainInvestigationEvents()) intelligence.Apply(change);
        return new InvestigationCommandResult(accepted, rejected);
    }
}

public readonly record struct InvestigationCommandResult(int Accepted, int Rejected);

/// <summary>
/// Formats a configured trait using only the team knowledge mask. The
/// bitwise check is kept in a pure helper so the security rule is testable
/// without creating an ImGui context.
/// </summary>
public static class DossierTraitFormatter
{
    public static string Format(TraitDefinition trait, long knownTraitMask)
    {
        ArgumentNullException.ThrowIfNull(trait);
        return (knownTraitMask & trait.Bit) != 0 ? trait.Name : "Trait: ???";
    }
}

/// <summary>Caches matching row indexes until text or stable identities change.</summary>
public sealed class AgentIdentitySearchIndex
{
    private string _search = string.Empty;
    private long _identityVersion = long.MinValue;
    private int[] _matches = [];

    public int RebuildCount { get; private set; }
    public IReadOnlyList<int> Matches => _matches;

    public IReadOnlyList<int> Update(
        IReadOnlyList<PlayerIntelligenceAgentSnapshot> agents,
        string? search,
        long identityVersion)
    {
        ArgumentNullException.ThrowIfNull(agents);
        search = search?.Trim() ?? string.Empty;
        if (_identityVersion == identityVersion && string.Equals(_search, search, StringComparison.Ordinal))
            return _matches;

        _search = search;
        _identityVersion = identityVersion;
        _matches = agents.Select((agent, index) => (agent, index))
            .Where(item => search.Length == 0 ||
                item.agent.EntityId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.agent.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        RebuildCount++;
        return _matches;
    }
}

/// <summary>Pure range traversal used by clipped UI lists and scaling tests.</summary>
public static class VisibleRowRange
{
    public static int Visit(int itemCount, int displayStart, int displayEnd, Action<int> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        var start = Math.Clamp(displayStart, 0, itemCount);
        var end = Math.Clamp(displayEnd, start, itemCount);
        for (var index = start; index < end; index++) visitor(index);
        return end - start;
    }
}

public static class DossierInvestigationActions
{
    public static InvestigationCommand Toggle(PlayerIntelligenceAgentSnapshot agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.IsOperative)
            throw new InvalidOperationException("Operatives have permanent detail and cannot be investigated.");
        return new InvestigationCommand(agent.EntityId, !agent.IsUnderInvestigation);
    }
}

public sealed class DossierWindow
{
    private int? _selectedAgentId;
    private string _search = string.Empty;
    private readonly AgentIdentitySearchIndex _searchIndex = new();

    public unsafe void Draw(
        PlayerIntelligenceDB intelligence,
        IReadOnlyList<TraitDefinition> traits,
        Action<InvestigationCommand> commandSink,
        ref bool isOpen)
    {
        ArgumentNullException.ThrowIfNull(intelligence);
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(commandSink);

        if (_selectedAgentId is not null &&
            !intelligence.Agents.Any(agent => agent.EntityId == _selectedAgentId.Value))
        {
            _selectedAgentId = null;
        }

        if (!ImGui.Begin(ApplicationShell.DossiersWindowTitle, ref isOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.Text("Operative Intelligence");
        ImGui.Text($"Operatives: {intelligence.OperativeEntityIds.Count}");
        ImGui.Text($"Agents: {intelligence.Agents.Count}");
        ImGui.SetNextItemWidth(280);
        ImGui.InputTextWithHint("##dossier-search", "Search agent ID or name", ref _search, 128);
        ImGui.Separator();

        ImGui.BeginChild("dossier-agent-list", new Vector2(300, 0), ImGuiChildFlags.Borders);
        var matches = _searchIndex.Update(intelligence.Agents, _search, intelligence.StableIdentityVersion);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(matches.Count);
        while (clipper.Step())
        {
            VisibleRowRange.Visit(matches.Count, clipper.DisplayStart, clipper.DisplayEnd, row =>
            {
                var agent = intelligence.Agents[matches[row]];
                var selected = agent.EntityId == _selectedAgentId;
                var label = agent.IntelligenceRole != IntelligenceRole.None
                    ? $"[{agent.IntelligenceRole}] {agent.DisplayName}"
                    : agent.DisplayName;
                if (ImGui.Selectable(label, selected)) _selectedAgentId = agent.EntityId;
            });
        }
        clipper.End();
        clipper.Destroy();

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("dossier-agent-details", new Vector2(0, 0), ImGuiChildFlags.Borders);

        if (intelligence.TryGetAgent(_selectedAgentId ?? -1, out var selectedAgent) && selectedAgent is not null)
        {
            DrawDetails(selectedAgent, traits, commandSink);
        }
        else
        {
            ImGui.Text("Select an agent to open its dossier.");
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private static void DrawDetails(
        PlayerIntelligenceAgentSnapshot agent,
        IReadOnlyList<TraitDefinition> traits,
        Action<InvestigationCommand> commandSink)
    {
        ImGui.Text(agent.DisplayName);
        ImGui.Text($"Intelligence role: {agent.IntelligenceRole}");
        if (agent.IsOperative)
        {
            ImGui.Text("Permanent detail: Operative team member");
        }
        else
        {
            var label = agent.IsUnderInvestigation ? "End Investigation" : "Investigate";
            if (ImGui.Button(label))
                commandSink(DossierInvestigationActions.Toggle(agent));
        }
        ImGui.Text($"Under investigation: {(agent.IsUnderInvestigation ? "Yes" : "No")}");

        ImGui.Separator();
        ImGui.Text("Known traits");
        foreach (var trait in traits)
        {
            ImGui.BulletText(DossierTraitFormatter.Format(trait, agent.KnownTraitMask));
        }
    }
}
