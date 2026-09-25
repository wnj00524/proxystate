using System.Numerics;
using ImGuiNET;
using ProxyState.Simulation;

namespace ProxyState;

/// <summary>
/// Applications that can be launched from the desktop-style program manager.
/// Keeping the identifiers separate from their labels makes the double-click
/// routing explicit and leaves room for more applications later.
/// </summary>
public enum ApplicationId
{
    Dossiers,
    NorthstarOpinion,
    TownlineResearch,
    DebugWindow
}

public readonly record struct ApplicationIcon(ApplicationId Id, string Label, string Glyph);

public static class ApplicationCatalog
{
    public static IReadOnlyList<ApplicationIcon> GetAvailable(bool debugMode) =>
        debugMode
            ? new[]
            {
                new ApplicationIcon(ApplicationId.Dossiers, "Dossiers", "D"),
                new ApplicationIcon(ApplicationId.NorthstarOpinion, "Northstar Opinion", "N"),
                new ApplicationIcon(ApplicationId.TownlineResearch, "Townline Research", "T"),
                new ApplicationIcon(ApplicationId.DebugWindow, "Debug Window", "DBG")
            }
            : new[]
            {
                new ApplicationIcon(ApplicationId.Dossiers, "Dossiers", "D"),
                new ApplicationIcon(ApplicationId.NorthstarOpinion, "Northstar Opinion", "N"),
                new ApplicationIcon(ApplicationId.TownlineResearch, "Townline Research", "T")
            };
}

/// <summary>
/// Owns the launcher state, including which application windows are open.
/// The launcher only changes presentation state; it does not read simulation
/// entities or any other Ground Truth data.
/// </summary>
public sealed class ApplicationShell
{
    public const string DossiersWindowTitle = "Surveillance Terminal";
    public const string DebugWindowTitle = "Debug Window";
    public const string NorthstarWindowTitle = "Northstar Opinion";
    public const string TownlineWindowTitle = "Townline Research";

    private ApplicationId? _selectedApplication;

    public bool DossiersOpen { get; private set; }

    public bool DebugWindowOpen { get; private set; }
    public bool NorthstarWindowOpen { get; private set; }
    public bool TownlineWindowOpen { get; private set; }

    public void OpenApplication(ApplicationId application) => Open(application);

    public void CloseApplication(ApplicationId application)
    {
        switch (application)
        {
            case ApplicationId.NorthstarOpinion: NorthstarWindowOpen = false; break;
            case ApplicationId.TownlineResearch: TownlineWindowOpen = false; break;
            case ApplicationId.Dossiers: DossiersOpen = false; break;
            case ApplicationId.DebugWindow: DebugWindowOpen = false; break;
        }
    }

    public void DrawLauncher(bool debugMode)
    {
        if (!debugMode)
        {
            // A debug window cannot remain open if the process was not started
            // in debug mode. This also keeps the launcher contract testable.
            DebugWindowOpen = false;
            if (_selectedApplication == ApplicationId.DebugWindow)
            {
                _selectedApplication = null;
            }
        }

        ImGui.SetNextWindowPos(new Vector2(24f, 24f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(620f, 270f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Applications"))
        {
            ImGui.Text("Proxy State Program Manager");
            ImGui.TextDisabled("Double-click an icon to launch");
            ImGui.Separator();

            foreach (var application in ApplicationCatalog.GetAvailable(debugMode))
            {
                DrawApplicationIcon(application);
                ImGui.SameLine();
            }

            ImGui.NewLine();
            ImGui.Separator();
            ImGui.Text(_selectedApplication is null
                ? "No application selected"
                : $"Selected: {GetLabel(_selectedApplication.Value)}");
        }

        ImGui.End();
    }

    public void DrawDossiersWindow(
        PlayerIntelligenceDB intelligence,
        IReadOnlyList<TraitDefinition> traits,
        DossierWindow dossierWindow,
        Action<InvestigationCommand> commandSink)
    {
        ArgumentNullException.ThrowIfNull(intelligence);
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(dossierWindow);

        if (!DossiersOpen)
        {
            return;
        }

        var isOpen = DossiersOpen;
        dossierWindow.Draw(intelligence, traits, commandSink, ref isOpen);
        DossiersOpen = isOpen;
    }

    public void DrawDebugWindow(DebugInspectionView inspection, DebugWindow debugWindow)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(debugWindow);

        if (!DebugWindowOpen)
        {
            return;
        }

        var isOpen = DebugWindowOpen;
        debugWindow.Draw(inspection, ref isOpen);
        DebugWindowOpen = isOpen;
    }

    public void DrawResearchWindows(IReadOnlyList<ResearchProviderProjection> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var northstar = providers.FirstOrDefault(provider => provider.ProviderId == "northstar-opinion");
        var townline = providers.FirstOrDefault(provider => provider.ProviderId == "townline-research");
        if (NorthstarWindowOpen && northstar is not null)
        {
            var open = NorthstarWindowOpen;
            PoliticalResearchWindow.Draw(northstar, NorthstarWindowTitle, ref open);
            NorthstarWindowOpen = open;
        }
        if (TownlineWindowOpen && townline is not null)
        {
            var open = TownlineWindowOpen;
            PoliticalResearchWindow.Draw(townline, TownlineWindowTitle, ref open);
            TownlineWindowOpen = open;
        }
    }

    private void DrawApplicationIcon(ApplicationIcon application)
    {
        ImGui.BeginGroup();
        ImGui.PushID(application.Id.ToString());

        var buttonColor = _selectedApplication == application.Id
            ? new Vector4(0.12f, 0.36f, 0.68f, 1f)
            : new Vector4(0.28f, 0.32f, 0.38f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.46f, 0.82f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.08f, 0.25f, 0.52f, 1f));
        ImGui.Button(application.Glyph, new Vector2(82f, 74f));
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemClicked())
        {
            _selectedApplication = application.Id;
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            Open(application.Id);
        }

        var textWidth = ImGui.CalcTextSize(application.Label).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (82f - textWidth) / 2f));
        ImGui.Text(application.Label);
        ImGui.PopID();
        ImGui.EndGroup();
    }

    private void Open(ApplicationId application)
    {
        _selectedApplication = application;
        switch (application)
        {
            case ApplicationId.Dossiers:
                DossiersOpen = true;
                break;
            case ApplicationId.DebugWindow:
                DebugWindowOpen = true;
                break;
            case ApplicationId.NorthstarOpinion:
                NorthstarWindowOpen = true;
                break;
            case ApplicationId.TownlineResearch:
                TownlineWindowOpen = true;
                break;
        }
    }

    private static string GetLabel(ApplicationId application) => application switch
    {
        ApplicationId.Dossiers => "Dossiers",
        ApplicationId.DebugWindow => "Debug Window",
        _ => application.ToString()
    };
}

/// <summary>
/// Applies the restrained gray, navy, and blue palette used by the launcher.
/// The terminal remains an ImGui window, but its presentation follows the
/// classic desktop utility aesthetic requested for Proxy State.
/// </summary>
public static class Windows31Theme
{
    public static void Apply()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 0f;
        style.ChildRounding = 0f;
        style.FrameRounding = 0f;
        style.PopupRounding = 0f;
        style.ScrollbarRounding = 0f;
        style.GrabRounding = 0f;
        style.WindowBorderSize = 2f;
        style.FrameBorderSize = 1f;

        style.Colors[(int)ImGuiCol.WindowBg] = new Vector4(0.72f, 0.72f, 0.72f, 1f);
        style.Colors[(int)ImGuiCol.ChildBg] = new Vector4(0.78f, 0.78f, 0.78f, 1f);
        style.Colors[(int)ImGuiCol.Text] = new Vector4(0.05f, 0.05f, 0.05f, 1f);
        style.Colors[(int)ImGuiCol.Border] = new Vector4(0.10f, 0.10f, 0.10f, 1f);
        style.Colors[(int)ImGuiCol.TitleBg] = new Vector4(0.05f, 0.16f, 0.38f, 1f);
        style.Colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.05f, 0.16f, 0.38f, 1f);
        style.Colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.05f, 0.16f, 0.38f, 1f);
        style.Colors[(int)ImGuiCol.FrameBg] = new Vector4(0.86f, 0.86f, 0.86f, 1f);
        style.Colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.92f, 0.92f, 0.92f, 1f);
        style.Colors[(int)ImGuiCol.FrameBgActive] = new Vector4(1f, 1f, 1f, 1f);
        style.Colors[(int)ImGuiCol.Separator] = new Vector4(0.25f, 0.25f, 0.25f, 1f);
    }
}
