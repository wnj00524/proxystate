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

// These records intentionally contain only copied values. The UI can inspect
// them freely without retaining an ECS Entity or a mutable component reference.
public sealed record DebugAgentSnapshot(
    int EntityId,
    int NameId,
    int OccupationId,
    string OccupationName,
    byte FactionId,
    string FactionName,
    IReadOnlyList<DebugAttributeSnapshot> Attributes,
    IReadOnlyList<DebugTraitSnapshot> Traits,
    long TraitMask,
    int CurrentActionHash,
    string CurrentActionName,
    DebugLocationSnapshot Home,
    DebugLocationSnapshot Workplace,
    DebugLocationSnapshot CurrentLocation,
    DebugTravelSnapshot Travel)
{
    public string DisplayName => $"Agent {EntityId} (Name ID {NameId})";
}

public static class DebugSnapshotBuilder
{
    public static IReadOnlyList<DebugAgentSnapshot> Capture(EntityStore store, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        var jobsByHash = catalog.Jobs.ToDictionary(job => job.Hash);
        var factionsById = catalog.Factions.ToDictionary(faction => faction.FactionId);
        var actionsByHash = catalog.Actions.ToDictionary(action => action.Hash);
        var snapshots = new List<DebugAgentSnapshot>();

        foreach (var entity in store.Query<Identity>().Entities.OrderBy(entity => entity.Id))
        {
            var identity = entity.GetComponent<Identity>();
            var faction = entity.GetComponent<PoliticalAlignment>();
            var attributes = entity.GetComponent<AgentAttributes>();
            var psychology = entity.GetComponent<Psychology>();
            var state = entity.GetComponent<AgentState>();
            var location = entity.GetComponent<AgentLocation>();
            var travel = entity.GetComponent<AgentTravel>();

            var jobName = jobsByHash.TryGetValue(identity.OccupationId, out var job)
                ? job.Name
                : $"Unknown ({identity.OccupationId})";
            var factionName = factionsById.TryGetValue(faction.FactionId, out var factionDefinition)
                ? factionDefinition.Name
                : $"Unknown ({faction.FactionId})";
            var actionName = actionsByHash.TryGetValue(state.CurrentActionHash, out var action)
                ? action.Name
                : $"Unknown ({state.CurrentActionHash})";

            snapshots.Add(new DebugAgentSnapshot(
                entity.Id,
                identity.NameId,
                identity.OccupationId,
                jobName,
                faction.FactionId,
                factionName,
                CopyAttributes(attributes, catalog.AgentAttributes),
                CopyTraits(psychology.TraitMask, catalog.Traits),
                psychology.TraitMask,
                state.CurrentActionHash,
                actionName,
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
                    travel.Mode)));
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
}

public sealed class DebugWindow
{
    private int? _selectedAgentId;

    public void Draw(IReadOnlyList<DebugAgentSnapshot> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        if (_selectedAgentId is not null && agents.All(agent => agent.EntityId != _selectedAgentId.Value))
        {
            _selectedAgentId = null;
        }

        if (!ImGui.Begin("Debug"))
        {
            ImGui.End();
            return;
        }

        ImGui.Text("Debug mode: ON");
        ImGui.Text($"Agents: {agents.Count}");
        ImGui.Separator();

        ImGui.BeginChild("debug-agent-list", new Vector2(280, 0), ImGuiChildFlags.Borders);
        foreach (var agent in agents)
        {
            var selected = agent.EntityId == _selectedAgentId;
            if (ImGui.Selectable(agent.DisplayName, selected))
            {
                _selectedAgentId = agent.EntityId;
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("debug-agent-details", new Vector2(0, 0), ImGuiChildFlags.Borders);

        var selectedAgent = agents.FirstOrDefault(agent => agent.EntityId == _selectedAgentId);
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
        ImGui.BulletText($"Occupation: {agent.OccupationName} ({agent.OccupationId})");
        ImGui.BulletText($"Faction: {agent.FactionName} ({agent.FactionId})");

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
        ImGui.BulletText($"Home: {FormatLocation(agent.Home)}");
        ImGui.BulletText($"Workplace: {FormatLocation(agent.Workplace)}");
        ImGui.BulletText($"Current location: {FormatLocation(agent.CurrentLocation)}");

        ImGui.Separator();
        ImGui.Text("Travel");
        ImGui.BulletText($"Mode: {agent.Travel.Mode}");
        ImGui.BulletText($"Total travel: {agent.Travel.TotalTravelMinutes} minutes");
        ImGui.BulletText($"Route position: {agent.Travel.RoutePosition}");
        ImGui.BulletText($"Remaining travel: {agent.Travel.RemainingTravelMinutes:0.##} minutes");
        ImGui.BulletText($"Route: {string.Join(" -> ", agent.Travel.Route.Select(FormatLocation))}");
    }

    private static string FormatLocation(DebugLocationSnapshot location) =>
        $"{location.Name} ({location.Id})";
}
