using ImGuiNET;
using ProxyState.Simulation;

namespace ProxyState;

/// <summary>
/// Immutable world-time data captured for presentation code. Keeping this
/// separate from the ECS entity prevents ImGui from reading Ground Truth.
/// </summary>
public readonly record struct WorldTimeSnapshot(int DayNumber, int DayOfWeek, int MinuteOfDay)
{
    public static WorldTimeSnapshot From(WorldTime time) =>
        new(time.DayIndex + 1, time.DayOfWeek, time.MinuteOfDay);
}

public static class WorldTimeFormatter
{
    private static readonly string[] DayNames =
    {
        "Monday",
        "Tuesday",
        "Wednesday",
        "Thursday",
        "Friday",
        "Saturday",
        "Sunday"
    };

    public static string Format(WorldTimeSnapshot time)
    {
        var hours = time.MinuteOfDay / 60;
        var minutes = time.MinuteOfDay % 60;
        var dayName = time.DayOfWeek is >= 1 and <= 7
            ? DayNames[time.DayOfWeek - 1]
            : "Unknown day";

        return $"Day {time.DayNumber} | {dayName} | {hours:00}:{minutes:00}";
    }
}

public static class WorldTimeBar
{
    private const float Height = 36f;

    public static void Draw(WorldTimeSnapshot time, int speed, Action<int> setSpeed)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 0f || displaySize.Y <= 0f)
        {
            return;
        }

        // Draw last and pin the window to the viewport so every application
        // mode receives the same persistent status bar during resizing.
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(0f, MathF.Max(0f, displaySize.Y - Height)), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(displaySize.X, Height), ImGuiCond.Always);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNavFocus;

        if (ImGui.Begin("##world-time-bar", flags))
        {
            ImGui.Text($"WORLD TIME  {WorldTimeFormatter.Format(time)}");
            ImGui.SameLine(MathF.Max(0f, displaySize.X - 300f));
            ImGui.Text("SPEED");
            for (var option = 1; option <= 4; option++)
            {
                ImGui.SameLine();
                if (option == speed) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.25f, 0.36f, 0.48f, 1f));
                if (ImGui.SmallButton($"{option}x##world-speed-{option}")) setSpeed(option);
                if (option == speed) ImGui.PopStyleColor();
            }
        }

        ImGui.End();
    }
}
