using System.Text.Json;
using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class PoliticalHeadlessReportTests
{
    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void IdenticalSeedProducesIdenticalHeadlessReportData()
    {
        var catalog = LoadCatalog();
        var progress = new List<string>();
        var first = HeadlessSimulationRunner.Run(catalog, 48, 1, 7712, progress.Add);
        var second = HeadlessSimulationRunner.Run(catalog, 48, 1, 7712);

        Assert.Equal(48, first.AgentCount);
        Assert.Equal(1_440, first.SimulatedMinutes);
        Assert.Equal(catalog.Factions.Count, first.DailyFactionSnapshots.Count);
        Assert.Empty(first.Elections);
        Assert.Contains(progress, message => message.Contains("started: agents=48, days=1, seed=7712", StringComparison.Ordinal));
        Assert.Contains(progress, message => message.StartsWith("Starting day 1/1", StringComparison.Ordinal));
        Assert.Contains(progress, message => message.Contains("estimated remaining", StringComparison.Ordinal));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void ElectionCycleIsCapturedPerOfficeAndReportsWriteBothFormats()
    {
        var report = HeadlessSimulationRunner.Run(LoadCatalog(), 64, 28, 991);

        Assert.Contains(report.DailyFactionSnapshots, snapshot => snapshot.Day == 28);
        var election = Assert.Single(report.Elections);
        Assert.Equal(1, election.ElectionId);
        Assert.Equal(28, election.Day);
        Assert.NotEmpty(election.Offices);
        Assert.Contains(report.Events, entry => entry.Type == "election-resolved");
        Assert.Contains(report.Events, entry => entry.Type == "nomination-trip-started");
        Assert.Contains(report.Events, entry => entry.Type == "polling-trip-started");

        var directory = Path.Combine(Path.GetTempPath(), "proxystate-headless-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = HeadlessSimulationRunner.WriteReports(report, Path.Combine(directory, "run"));
            Assert.True(File.Exists(paths.MarkdownPath));
            Assert.True(File.Exists(paths.JsonPath));
            Assert.Contains("Election outcomes", File.ReadAllText(paths.MarkdownPath));
            Assert.Contains("\"SchemaVersion\": 1", File.ReadAllText(paths.JsonPath));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
