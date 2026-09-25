using System.Diagnostics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace ProxyState.Tests;

public sealed class Milestone17BaselineTests(ITestOutputHelper output)
{
    [Fact]
    public void WorkCountersAndDecisionsRepeatForFixedSeed()
    {
        var first = RunDeterministicFixture();
        var second = RunDeterministicFixture();

        Assert.Equal(first.Work, second.Work);
        Assert.Equal(first.Intentions, second.Intentions);
        // Faction-only activist jobs are now excluded from population spawn
        // assignments, changing this fixed-seed detailed-work baseline.
        Assert.Equal(45, first.Work.DecisionPasses);
        Assert.True(first.Work.CandidateEvaluations > 0);
        Assert.Equal(0, first.Work.TargetPopulationVisits);
        Assert.Equal(0, first.Work.EdgeVisits);
        Assert.Equal(0, first.Work.TransientOperations);
    }

    [Theory]
    [Trait("Category", "Performance")]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void PopulationGenerationAndDetailedLoopBenchmark(int population)
    {
        // Large cases are opt-in for ordinary test runs, but remain first-class
        // Release fixtures for the explicitly documented baseline command.
        if (population > 1_000 && Environment.GetEnvironmentVariable("PROXYSTATE_RUN_LARGE_BENCHMARKS") != "1")
        {
            output.WriteLine($"population={population}; notRun=true; set PROXYSTATE_RUN_LARGE_BENCHMARKS=1");
            return;
        }

        var catalog = LoadCatalog();
        var store = new EntityStore();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, population, 17_001);
        stopwatch.Stop();
        var generationAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        output.WriteLine($"phase=generation; population={population}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}; allocatedBytes={generationAllocated}; edges={store.Query<EdgeData>().Count}");

        var clock = store.CreateEntity(new WorldTime
        {
            ElapsedSimulationSeconds = 600 * SimulationDefaults.SimulationSecondsPerMinute,
            DeltaSimulationSeconds = SimulationDefaults.SimulationSecondsPerMinute
        });
        var diagnostics = new SimulationWorkDiagnostics();
        var root = new SystemRoot(store)
        {
            new AgentDecisionSystem(store, catalog, clock, workDiagnostics: diagnostics,
                socialIndexes: spawner.Indexes)
        };
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        root.Update(default);
        stopwatch.Stop();
        var loopAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var work = diagnostics.Snapshot();
        var detailedPopulation = store.Query<Identity>().AllTags(Tags.Get<DetailedSimulationTag>()).Count;
        output.WriteLine($"phase=detailed-loop; population={population}; elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}; allocatedBytes={loopAllocated}; decisionPasses={work.DecisionPasses}; candidateEvaluations={work.CandidateEvaluations}; targetPopulationVisits={work.TargetPopulationVisits}; edgeVisits={work.EdgeVisits}; transientOperations={work.TransientOperations}");

        // Every newly spawned detailed agent needs one cache-building pass.
        // Subsequent Tier 2 work is bounded by the hourly cadence.
        Assert.Equal(detailedPopulation, work.DecisionPasses);
        Assert.Equal(0, work.TargetPopulationVisits);
        Assert.Equal(0, work.EdgeVisits);
        Assert.Equal(0, work.TransientOperations);
    }

    private static (SimulationWorkSnapshot Work, (int Action, int Target, int Location)[] Intentions)
        RunDeterministicFixture()
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, 64, 17_002);
        var clock = store.CreateEntity(new WorldTime
        {
            ElapsedSimulationSeconds = 600 * SimulationDefaults.SimulationSecondsPerMinute
        });
        var diagnostics = new SimulationWorkDiagnostics();
        new SystemRoot(store)
        {
            new AgentDecisionSystem(store, catalog, clock, workDiagnostics: diagnostics,
                socialIndexes: spawner.Indexes)
        }.Update(default);

        var intentions = store.Query<IntentionState>().Entities.OrderBy(entity => entity.Id).Select(entity =>
        {
            var state = entity.GetComponent<IntentionState>();
            return (state.ActionHash, state.TargetEntityId, state.TargetLocationId);
        }).ToArray();
        return (diagnostics.Snapshot(), intentions);
    }

    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));
}
