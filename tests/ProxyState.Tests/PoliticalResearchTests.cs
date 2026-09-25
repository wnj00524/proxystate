using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using ProxyState;
using ProxyState.Simulation;
using System.Text.Json.Nodes;
using Xunit;

namespace ProxyState.Tests;

public sealed class PoliticalResearchTests
{
    private static ContentCatalog LoadCatalog() =>
        ContentCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void ProviderConfigurationDefinesDistinctWeeklyMethodsAndValidTargets()
    {
        var research = LoadCatalog().Research;

        Assert.Equal(7, research.CadenceDays);
        Assert.Equal(3, research.FieldworkDays);
        Assert.All(research.Providers, provider => Assert.Equal(200, provider.AttemptCount));
        Assert.Equal(2, research.Providers.Select(provider => provider.SamplingMethod).Distinct().Count());
        Assert.Contains(research.Providers, provider => provider.LikelyVoterOnly);
        Assert.Contains(research.Providers, provider => !provider.LikelyVoterOnly);
    }

    [Theory]
    [InlineData("samplingMethod", "convenience")]
    [InlineData("attemptCount", "0")]
    public void ContentValidationRejectsInvalidProviderDefinitions(string field, string invalidValue)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "data");
        var directory = Directory.CreateTempSubdirectory("research-content-");
        try
        {
            foreach (var path in Directory.GetFiles(source, "*.json"))
                File.Copy(path, Path.Combine(directory.FullName, Path.GetFileName(path)));
            var researchPath = Path.Combine(directory.FullName, "research.json");
            var document = JsonNode.Parse(File.ReadAllText(researchPath))!;
            JsonNode property = field == "attemptCount"
                ? JsonValue.Create(int.Parse(invalidValue, System.Globalization.CultureInfo.InvariantCulture))!
                : JsonValue.Create(invalidValue)!;
            document["providers"]![0]![field] = property;
            File.WriteAllText(researchPath, document.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ContentCatalog.Load(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void WeeklyCallsPublishAnonymousWeightedResultsAndUseDistinctWindows()
    {
        var first = RunOneWave(seed: 443);
        var second = RunOneWave(seed: 443);

        Assert.Equal(first.Select(item => item.Latest!.CallsAttempted), second.Select(item => item.Latest!.CallsAttempted));
        Assert.Equal(first.SelectMany(item => item.Latest!.PartySupport.Select(estimate => estimate.WeightedPercentage)),
            second.SelectMany(item => item.Latest!.PartySupport.Select(estimate => estimate.WeightedPercentage)));
        Assert.All(first, provider =>
        {
            var poll = Assert.IsType<OpinionPollSnapshot>(provider.Latest);
            Assert.Equal(200, poll.CallsAttempted);
            Assert.Equal(poll.CallsAttempted - poll.CompletedInterviews, poll.Nonresponse);
            Assert.True(poll.CompletedInterviews > 0);
            Assert.True(poll.EffectiveSampleSize > 0);
            Assert.True(poll.EffectiveSampleSize <= poll.CompletedInterviews);
            Assert.Empty(poll.CandidateSupport); // No official nominees have registered.
            Assert.All(poll.PartySupport, estimate =>
            {
                Assert.InRange(estimate.Lower95, 0f, estimate.WeightedPercentage);
                Assert.InRange(estimate.Upper95, estimate.WeightedPercentage, 100f);
            });
        });
    }

    [Fact]
    public void ProviderApplicationsOpenIndependently()
    {
        var shell = new ApplicationShell();
        shell.OpenApplication(ApplicationId.NorthstarOpinion);
        Assert.True(shell.NorthstarWindowOpen);
        Assert.False(shell.TownlineWindowOpen);
        shell.OpenApplication(ApplicationId.TownlineResearch);
        Assert.True(shell.NorthstarWindowOpen);
        Assert.True(shell.TownlineWindowOpen);
        shell.CloseApplication(ApplicationId.NorthstarOpinion);
        Assert.False(shell.NorthstarWindowOpen);
        Assert.True(shell.TownlineWindowOpen);
    }

    [Fact]
    public void CallsToAgentsAwayFromHomeAreOnlyReportedAsNonresponse()
    {
        var projections = RunOneWave(seed: 443, agentsAway: true);
        Assert.All(projections, provider =>
        {
            Assert.Equal(200, provider.Latest!.CallsAttempted);
            Assert.Equal(0, provider.Latest.CompletedInterviews);
            Assert.Equal(200, provider.Latest.Nonresponse);
        });
    }

    private static IReadOnlyList<ResearchProviderProjection> RunOneWave(int seed, bool agentsAway = false)
    {
        var catalog = LoadCatalog();
        var store = new EntityStore();
        new AgentSpawner(catalog).Spawn(store, 600, seed);
        if (agentsAway)
        {
            foreach (var agent in store.Query<AgentLocation>().Entities)
                agent.GetComponent<AgentLocation>().CurrentLocationId++;
        }
        var clock = new WorldClockSystem(store);
        var systems = new SystemRoot(store) { clock };
        var research = new PoliticalResearchSystem(store, catalog, clock.ClockEntity, seed);
        var secondsPerMinute = SimulationDefaults.RealSecondsPerSimulationDay /
                               SimulationDefaults.SimulationMinutesPerDay;

        // Begin the first wave at its configured calling time, then advance past
        // the three-day field window so all scheduled attempts are processed.
        clock.Advance(secondsPerMinute * catalog.Research.CallStartMinute);
        systems.Update(default);
        research.Update();
        clock.Advance(secondsPerMinute * SimulationDefaults.SimulationMinutesPerDay * 3);
        systems.Update(default);
        research.Update();
        return research.GetProviderProjections();
    }
}
