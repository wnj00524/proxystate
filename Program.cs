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
    public static int Main(string[] args)
    {
        if (ContentValidation.IsRequested(args))
            return ContentValidation.Run(args, Console.Out, Console.Error);
        if (!ApplicationOptions.TryParse(args, out var options, out var optionError))
        {
            Console.Error.WriteLine(optionError);
            return 2;
        }
        var debugMode = options.DebugMode;
        var contentDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var catalog = ContentCatalog.Load(contentDirectory);
        if (options.Headless)
        {
            try
            {
                var report = HeadlessSimulationRunner.Run(catalog, options.AgentCount, options.Days!.Value,
                    options.Seed, Console.WriteLine);
                var paths = HeadlessSimulationRunner.WriteReports(report, options.ReportPrefix);
                Console.WriteLine($"Political diagnostics written to {paths.MarkdownPath} and {paths.JsonPath}");
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine($"Headless simulation failed: {exception.Message}");
                return 1;
            }
        }
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);

        // A fresh seed gives each interactive run a new population. The spawner
        // accepts Random explicitly so tests and future replay tools can inject one.
        var simulationSeed = new Random().Next();
        spawner.Spawn(store, options.AgentCount, simulationSeed);
        var lodService = spawner.LodService ?? throw new InvalidOperationException("Agent LOD service was not initialized.");
        // Retain the immutable bootstrap indexes for indexed simulation systems
        // introduced by subsequent Milestone 17 slices.
        var agentSocialIndexes = spawner.Indexes;

        var intelligence = PlayerIntelligenceDB.Create(store, catalog);
        var investigationCommands = new InvestigationCommandQueue();
        var interactionSystem = new InteractionSystem(store, catalog, SimulationRandomStreams.Interactions(simulationSeed), socialIndexes: agentSocialIndexes);
        var clock = new WorldClockSystem(store);
        var systems = new SystemRoot(store)
        {
            clock,
            new AgentDecisionSystem(store, catalog, clock.ClockEntity, captureDiagnostics: debugMode,
                socialIndexes: agentSocialIndexes, lodService: lodService),
            new CoordinationSystem(store, catalog, clock.ClockEntity, agentSocialIndexes, lodService),
            new IntentExecutionSystem(store, catalog, clock.ClockEntity, agentSocialIndexes),
            new ActivityEffectsSystem(catalog, clock.ClockEntity),
            interactionSystem
        };
        var politicalSystem = new PoliticalSystem(store, catalog, clock.ClockEntity, agentSocialIndexes, lodService);
        var factionSystem = new PoliticalFactionSystem(store, catalog, agentSocialIndexes, lodService);
        var researchSystem = new PoliticalResearchSystem(store, catalog, clock.ClockEntity, simulationSeed);

        Raylib.InitWindow(1280, 720, "Proxy State - Applications");
        Raylib.SetTargetFPS(60);

        try
        {
            rlImGui.Setup(true);
            Windows31Theme.Apply();
            var applicationShell = new ApplicationShell();
            var dossierWindow = new DossierWindow();
            var debugWindow = debugMode ? new DebugWindow() : null;
            var debugProjection = debugMode ? DebugInspectionProjection.Create(store, catalog) : null;

            while (!Raylib.WindowShouldClose())
            {
                // Simulation runs before rendering so the frame presents the
                // state produced by the current ECS tick.
                clock.Advance(Raylib.GetFrameTime());
                // Commands cross the stable-ID adapter before LOD lifecycle work.
                investigationCommands.Process(lodService, intelligence);
                lodService.UpdateCoarse((long)(clock.ClockEntity.GetComponent<WorldTime>().ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute));
                systems.Update(default);
                politicalSystem.Update();
                factionSystem.Update(clock.ClockEntity.GetComponent<WorldTime>().DayIndex + 1);
                researchSystem.Update();
                foreach (var discovery in interactionSystem.DrainOperativeDiscoveries()) intelligence.Apply(discovery);
                var worldTime = WorldTimeSnapshot.From(clock.ClockEntity.GetComponent<WorldTime>());

                Raylib.BeginDrawing();
                Raylib.ClearBackground(new Color(15, 15, 15, 255));

                rlImGui.Begin();
                applicationShell.DrawLauncher(debugMode);
                applicationShell.DrawDossiersWindow(intelligence, catalog.Traits, dossierWindow,
                    investigationCommands.Enqueue);
                applicationShell.DrawResearchWindows(researchSystem.GetProviderProjections());
                if (debugWindow is not null && applicationShell.DebugWindowOpen)
                {
                    // Only a changed selection crosses the on-demand copy boundary.
                    debugProjection!.Select(debugWindow.SelectedAgentId);
                    applicationShell.DrawDebugWindow(debugProjection.View, debugWindow);
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
        return 0;
    }
}
