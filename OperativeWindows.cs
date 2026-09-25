using System.Numerics;
using ImGuiNET;
using ProxyState.Simulation;

namespace ProxyState;

/// <summary>Management window for the player-controlled operative team.</summary>
public sealed class AgentsWindow
{
    private int? _selectedOperative;
    private int _targetId;
    private int _durationMinutes = 240;
    private int _startHour = 9;
    private int _startMinute;
    private int _endHour = 17;
    private int _endMinute;
    private byte _daysMask = 0b0001_1111;
    private string _targetSearch = string.Empty;
    private readonly AgentIdentitySearchIndex _targetIndex = new();

    public unsafe void Draw(OperativeManagementProjection management,
        IReadOnlyList<PlayerIntelligenceAgentSnapshot> knownAgents,
        Action<OperativeCommand> commandSink, ref bool open)
    {
        if (!ImGui.Begin("Agents", ref open)) { ImGui.End(); return; }
        ImGui.Text("Operative team");
        ImGui.TextDisabled("Assignments interrupt routine work; the rota resumes when a task ends.");
        ImGui.Separator();
        ImGui.BeginChild("agents-roster", new Vector2(245, 0), ImGuiChildFlags.Borders);
        foreach (var operative in management.Operatives)
            if (ImGui.Selectable(operative.DisplayName, _selectedOperative == operative.AgentId))
            {
                _selectedOperative = operative.AgentId;
                _daysMask = operative.WorkDaysMask;
                _startHour = operative.WorkStartMinute / 60;
                _startMinute = operative.WorkStartMinute % 60;
                _endHour = operative.WorkEndMinute / 60;
                _endMinute = operative.WorkEndMinute % 60;
            }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("agents-details", new Vector2(0, 0), ImGuiChildFlags.Borders);
        var selected = management.Operatives.FirstOrDefault(item => item.AgentId == _selectedOperative);
        if (selected.AgentId == 0) ImGui.Text("Select an operative.");
        else
        {
            ImGui.Text(selected.DisplayName);
            ImGui.Text($"Role: {selected.Role}");
            ImGui.Text($"Occupation: {selected.Occupation}");
            ImGui.Separator();
            ImGui.Text("Weekly rota");
            for (var day = 0; day < 7; day++)
            {
                ImGui.PushID(day);
                var active = (_daysMask & (1 << day)) != 0;
                if (ImGui.Checkbox(new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }[day], ref active))
                    _daysMask = active ? (byte)(_daysMask | (1 << day)) : (byte)(_daysMask & ~(1 << day));
                ImGui.PopID();
                if (day < 6) ImGui.SameLine();
            }
            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("Start hour", ref _startHour);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("Start minute", ref _startMinute);
            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("End hour", ref _endHour);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("End minute", ref _endMinute);
            if (ImGui.Button("Save rota"))
                commandSink(new(OperativeCommandKind.SetRota, selected.AgentId, _daysMask,
                    Math.Clamp(_startHour, 0, 23) * 60 + Math.Clamp(_startMinute, 0, 59),
                    Math.Clamp(_endHour, 0, 24) * 60 + Math.Clamp(_endMinute, 0, 59)));
            ImGui.Separator();
            ImGui.Text(selected.TaskKind == OperativeTaskKind.None
                ? "Assignment: Available" : $"Assignment: {selected.TaskKind} Agent {selected.TargetAgentId}");
            if (selected.TaskKind != OperativeTaskKind.None)
            {
                if (ImGui.Button("Recall assignment"))
                    commandSink(new(OperativeCommandKind.Recall, selected.AgentId));
            }
            else
            {
                ImGui.Text("Assign target");
                ImGui.SetNextItemWidth(250);
                ImGui.InputTextWithHint("##operative-target-search", "Search target ID or name", ref _targetSearch, 128);
                var matches = _targetIndex.Update(knownAgents, _targetSearch, 1);
                ImGui.BeginChild("operative-target-list", new Vector2(0, 170), ImGuiChildFlags.Borders);
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(matches.Count);
                while (clipper.Step())
                {
                    VisibleRowRange.Visit(matches.Count, clipper.DisplayStart, clipper.DisplayEnd, row =>
                    {
                        var target = knownAgents[matches[row]];
                        if (target.EntityId != selected.AgentId &&
                            ImGui.Selectable(target.DisplayName, _targetId == target.EntityId))
                            _targetId = target.EntityId;
                    });
                }
                clipper.End();
                clipper.Destroy();
                ImGui.EndChild();
                ImGui.TextDisabled(_targetId == 0 ? "No target selected" : $"Selected: Agent {_targetId}");
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Follow minutes", ref _durationMinutes);
                ImGui.BeginDisabled(_targetId == 0);
                if (ImGui.Button("Follow")) commandSink(new(OperativeCommandKind.Assign,
                    selected.AgentId, TaskKind: OperativeTaskKind.Follow, TargetAgentId: _targetId,
                    DurationMinutes: Math.Clamp(_durationMinutes, 1, 10080)));
                ImGui.SameLine();
                if (ImGui.Button("Talk")) commandSink(new(OperativeCommandKind.Assign,
                    selected.AgentId, TaskKind: OperativeTaskKind.Talk, TargetAgentId: _targetId));
                ImGui.EndDisabled();
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }
}

/// <summary>Displays sourced evidence separately from analyst assessments.</summary>
public sealed class ReportsWindow
{
    private int _selectedIndex;
    public void Draw(OperativeManagementProjection management, ref bool open)
    {
        if (!ImGui.Begin("Reports", ref open)) { ImGui.End(); return; }
        ImGui.Text($"Completed assignments: {management.Reports.Count}");
        ImGui.Separator();
        ImGui.BeginChild("report-list", new Vector2(330, 0), ImGuiChildFlags.Borders);
        for (var index = 0; index < management.Reports.Count; index++)
        {
            var report = management.Reports[index];
            if (ImGui.Selectable($"Day {report.Minute / SimulationDefaults.SimulationMinutesPerDay + 1} · Agent {report.SubjectAgentId}", index == _selectedIndex))
                _selectedIndex = index;
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("report-detail", new Vector2(0, 0), ImGuiChildFlags.Borders);
        if (management.Reports.Count == 0) ImGui.Text("No reports received yet.");
        else
        {
            _selectedIndex = Math.Clamp(_selectedIndex, 0, management.Reports.Count - 1);
            var report = management.Reports[_selectedIndex];
            ImGui.Text($"Subject: Agent {report.SubjectAgentId}");
            ImGui.Text($"Source: Operative {report.SourceOperativeId}");
            ImGui.Text($"Received at minute {report.Minute}");
            ImGui.Separator();
            ImGui.Text("Assessment");
            ImGui.TextWrapped(report.Summary);
            ImGui.Text($"Confidence: {report.Confidence:P0}");
            ImGui.Separator();
            ImGui.Text("Evidence");
            foreach (var evidence in report.Evidence)
            {
                ImGui.BulletText($"Minute {evidence.Minute}: {evidence.Kind} — {evidence.Detail}");
                ImGui.TextDisabled($"Reported by operative {evidence.SourceOperativeId}");
            }
            if (report.Evidence.Count == 0) ImGui.TextDisabled("No observations were confirmed.");
        }
        ImGui.EndChild();
        ImGui.End();
    }
}
