using ProxyState.Simulation;

namespace ProxyState;

/// <summary>Headless content gate used locally and by CI before Raylib starts.</summary>
public static class ContentValidation
{
    public const string Command = "--validate-content";

    public static bool IsRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, Command, StringComparison.OrdinalIgnoreCase));

    public static int Run(IReadOnlyList<string> arguments, TextWriter output, TextWriter error)
    {
        var commandIndex = arguments.ToList().FindIndex(argument =>
            string.Equals(argument, Command, StringComparison.OrdinalIgnoreCase));
        var directory = commandIndex >= 0 && commandIndex + 1 < arguments.Count
            ? Path.GetFullPath(arguments[commandIndex + 1])
            : Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            var catalog = ContentCatalog.Load(directory);
            IntelligenceTaskSettings.Load(directory, catalog);
            output.WriteLine($"Validated {catalog.Intents.Count} intents and intelligence task settings from {Path.Combine(directory, "actions.json")}.");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            error.WriteLine($"Content validation failed: {exception.Message}");
            return 1;
        }
    }
}
