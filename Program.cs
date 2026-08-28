using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ImGuiNET;
using ProxyState.Simulation;
using Raylib_cs;
using rlImGui_cs;

namespace ProxyState;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var contentDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var catalog = ContentCatalog.Load(contentDirectory);
        var store = new EntityStore();
        var spawner = new DummyAgentSpawner(catalog);

        // A fresh seed gives each interactive run a new population. The spawner
        // accepts Random explicitly so tests and future replay tools can inject one.
        spawner.Spawn(store, SimulationDefaults.AgentCount, new Random());

        var systems = new SystemRoot(store)
        {
            new FatigueStressSystem()
        };

        Raylib.InitWindow(1280, 720, "Proxy State - Intelligence Terminal");
        Raylib.SetTargetFPS(60);

        try
        {
            rlImGui.Setup(true);

            while (!Raylib.WindowShouldClose())
            {
                // Simulation runs before rendering so the frame presents the
                // state produced by the current ECS tick.
                systems.Update(default);

                Raylib.BeginDrawing();
                Raylib.ClearBackground(new Color(15, 15, 15, 255));

                rlImGui.Begin();
                ImGui.Begin("Intelligence Dossier");
                ImGui.Text("Agent data will render here...");
                ImGui.End();
                rlImGui.End();

                Raylib.EndDrawing();
            }
        }
        finally
        {
            rlImGui.Shutdown();
            Raylib.CloseWindow();
        }
    }
}
