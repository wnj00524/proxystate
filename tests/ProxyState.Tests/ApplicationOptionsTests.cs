using ProxyState.Simulation;
using Xunit;

namespace ProxyState.Tests;

public sealed class ApplicationOptionsTests
{
    [Fact]
    public void EmptyArgumentsKeepTheOrdinaryPopulation()
    {
        Assert.True(ApplicationOptions.TryParse([], out var options, out var error));
        Assert.Equal(SimulationDefaults.AgentCount, options.AgentCount);
        Assert.False(options.DebugMode);
        Assert.Null(error);
    }

    [Fact]
    public void PopulationOverrideAndDebugCanAppearInEitherOrder()
    {
        Assert.True(ApplicationOptions.TryParse(["-debug", "--agents", "100000"], out var options, out var error));
        Assert.Equal(100_000, options.AgentCount);
        Assert.True(options.DebugMode);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("nonnumeric")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("excessive")]
    public void InvalidPopulationIsRejectedWithHelpfulError(string kind)
    {
        var arguments = kind switch
        {
            "missing" => new[] { "--agents" },
            "nonnumeric" => new[] { "--agents", "many" },
            "zero" => new[] { "--agents", "0" },
            "negative" => new[] { "--agents", "-1" },
            _ => new[] { "--agents", "100001" }
        };

        Assert.False(ApplicationOptions.TryParse(arguments, out _, out var error));
        Assert.Contains("--agents", error);
    }

    [Fact]
    public void UnknownAndDuplicateOptionsAreRejected()
    {
        Assert.False(ApplicationOptions.TryParse(["--unknown"], out _, out var unknownError));
        Assert.Contains("Usage", unknownError);
        Assert.False(ApplicationOptions.TryParse(["--agents", "10", "--agents", "20"], out _, out var duplicateError));
        Assert.Contains("only be specified once", duplicateError);
    }

    [Fact]
    public void HeadlessOptionsUseDocumentedDefaultsAndAcceptMixedCaseFlags()
    {
        Assert.True(ApplicationOptions.TryParse(["--HEADLESS", "--DAYS", "28"], out var options, out var error));
        Assert.True(options.Headless);
        Assert.Equal(28, options.Days);
        Assert.Equal(17001, options.Seed);
        Assert.Equal("politics-report", options.ReportPrefix);
        Assert.Equal(SimulationDefaults.AgentCount, options.AgentCount);
        Assert.Null(error);
    }

    [Fact]
    public void HeadlessOptionsValidateBoundsAndCombinations()
    {
        Assert.False(ApplicationOptions.TryParse(["--headless"], out _, out var missingDays));
        Assert.Contains("requires --days", missingDays);
        Assert.False(ApplicationOptions.TryParse(["--headless", "--days", "0"], out _, out _));
        Assert.False(ApplicationOptions.TryParse(["--headless", "--days", "3651"], out _, out _));
        Assert.False(ApplicationOptions.TryParse(["--headless", "--days", "1", "--seed", "nope"], out _, out _));
        Assert.False(ApplicationOptions.TryParse(["--headless", "--days", "1", "--report", " "], out _, out _));
        Assert.False(ApplicationOptions.TryParse(["--days", "1"], out _, out var combination));
        Assert.Contains("only be used with --headless", combination);
        Assert.False(ApplicationOptions.TryParse(["--headless", "--days", "1", "-debug"], out _, out _));
    }
}
