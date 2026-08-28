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
    public static void Main(string[] args)
    {
        var debugMode = DebugMode.IsEnabled(args);
        var contentDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var catalog = ContentCatalog.Load(contentDirectory);
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);

        // A fresh seed gives each interactive run a new population. The spawner
        // accepts Random explicitly so tests and future replay tools can inject one.
        spawner.Spawn(store, SimulationDefaults.AgentCount, new Random());

        var clock = new WorldClockSystem(store);
        var systems = new SystemRoot(store)
        {
            clock,
            new CommutingSystem(catalog, clock.ClockEntity),
            new FatigueStressSystem(catalog.AgentAttributes),
            new InteractionSystem(catalog, new Random())
        };

        Raylib.InitWindow(1280, 720, "Proxy State - Intelligence Terminal");
        Raylib.SetTargetFPS(60);

        try
        {
            rlImGui.Setup(true);
            var debugWindow = debugMode ? new DebugWindow() : null;

            while (!Raylib.WindowShouldClose())
            {
                // Simulation runs before rendering so the frame presents the
                // state produced by the current ECS tick.
                clock.Advance(Raylib.GetFrameTime());
                systems.Update(default);
                var worldTime = WorldTimeSnapshot.From(clock.ClockEntity.GetComponent<WorldTime>());

                Raylib.BeginDrawing();
                Raylib.ClearBackground(new Color(15, 15, 15, 255));

                rlImGui.Begin();
                ImGui.Begin("Intelligence Dossier");
                ImGui.Text("Agent data will render here...");
                ImGui.End();
                if (debugWindow is not null)
                {
                    // Capture immutable values before drawing so the UI never
                    // reaches into the Ground Truth ECS store directly.
                    debugWindow.Draw(DebugSnapshotBuilder.Capture(store, catalog));
                }
                WorldTimeBar.Draw(worldTime);
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
