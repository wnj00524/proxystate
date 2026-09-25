using System.Globalization;
using ProxyState.Simulation;

namespace ProxyState;

/// <summary>Validated startup options shared by interactive and diagnostic runs.</summary>
public sealed record ApplicationOptions(int AgentCount, bool DebugMode, bool Headless = false,
    int? Days = null, int Seed = 17001, string ReportPrefix = "politics-report")
{
    public const int MaximumAgentCount = 100_000;
    public const int MaximumDays = 3650;

    public static bool TryParse(IReadOnlyList<string> arguments, out ApplicationOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var agentCount = SimulationDefaults.AgentCount;
        var debugMode = false;
        var headless = false;
        var days = (int?)null;
        var seed = 17001;
        var reportPrefix = "politics-report";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var normalized = argument.ToLowerInvariant();
            if (normalized is "-debug" or "--headless")
            {
                if (!seen.Add(normalized)) return Fail($"The {argument} option may only be specified once.", out options, out error);
                if (normalized == "-debug") debugMode = true;
                else headless = true;
                continue;
            }

            if (normalized is not ("--agents" or "--days" or "--seed" or "--report"))
                return Fail($"Unknown option '{argument}'. Usage: ProxyState [--agents <1-{MaximumAgentCount}>] [-debug] [--headless --days <1-{MaximumDays}> [--seed <int>] [--report <prefix>]]", out options, out error);
            if (!seen.Add(normalized)) return Fail($"The {argument} option may only be specified once.", out options, out error);
            if (++index >= arguments.Count)
                return Fail($"The {argument} option requires a value.", out options, out error);

            var value = arguments[index];
            switch (normalized)
            {
                case "--agents":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out agentCount) ||
                        agentCount is < 1 or > MaximumAgentCount)
                        return Fail($"The --agents value must be a whole number between 1 and {MaximumAgentCount}.", out options, out error);
                    break;
                case "--days":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDays) ||
                        parsedDays is < 1 or > MaximumDays)
                        return Fail($"The --days value must be a whole number between 1 and {MaximumDays}.", out options, out error);
                    days = parsedDays;
                    break;
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                        return Fail("The --seed value must be a signed 32-bit integer.", out options, out error);
                    break;
                case "--report":
                    if (string.IsNullOrWhiteSpace(value))
                        return Fail("The --report prefix cannot be empty.", out options, out error);
                    reportPrefix = value;
                    break;
            }
        }

        if (headless && days is null)
            return Fail("Headless mode requires --days <1-3650>.", out options, out error);
        if (!headless && (days is not null || seen.Contains("--seed") || seen.Contains("--report")))
            return Fail("--days, --seed, and --report can only be used with --headless.", out options, out error);
        if (headless && debugMode)
            return Fail("Headless mode cannot be combined with -debug.", out options, out error);

        options = new ApplicationOptions(agentCount, debugMode, headless, days, seed, reportPrefix);
        error = null;
        return true;
    }

    private static bool Fail(string message, out ApplicationOptions options, out string? error)
    {
        options = new ApplicationOptions(SimulationDefaults.AgentCount, false);
        error = message;
        return false;
    }
}
