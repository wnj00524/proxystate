using System.Text.Json;

namespace ProxyState.Simulation;

public sealed record TraitDefinition(string Id, string Name, long Bit);
public sealed record ActionDefinition(string Id, string Name, int Hash);
public sealed record FactionDefinition(string Id, string Name, byte FactionId);

public sealed class ContentCatalog
{
    private ContentCatalog(
        IReadOnlyList<TraitDefinition> traits,
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyList<FactionDefinition> factions)
    {
        Traits = traits;
        Actions = actions;
        Factions = factions;
        AllTraitBits = traits.Aggregate(0L, (mask, trait) => mask | trait.Bit);
    }

    public IReadOnlyList<TraitDefinition> Traits { get; }
    public IReadOnlyList<ActionDefinition> Actions { get; }
    public IReadOnlyList<FactionDefinition> Factions { get; }
    public long AllTraitBits { get; }

    public static ContentCatalog Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var traits = LoadFile<TraitDefinition>(directory, "traits.json", options);
        var actions = LoadFile<ActionDefinition>(directory, "actions.json", options);
        var factions = LoadFile<FactionDefinition>(directory, "factions.json", options);

        Validate(traits, actions, factions);
        return new ContentCatalog(traits, actions, factions);
    }

    private static IReadOnlyList<T> LoadFile<T>(
        string directory,
        string fileName,
        JsonSerializerOptions options)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required content file was not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<T>>(json, options)
            ?? throw new InvalidDataException($"Content file is empty or invalid: {path}");
    }

    private static void Validate(
        IReadOnlyList<TraitDefinition> traits,
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyList<FactionDefinition> factions)
    {
        if (traits.Count == 0 || actions.Count == 0 || factions.Count == 0)
        {
            throw new InvalidDataException("Traits, actions, and factions must each contain at least one definition.");
        }

        var traitBits = new HashSet<long>();
        foreach (var trait in traits)
        {
            if (trait.Bit <= 0 || (trait.Bit & (trait.Bit - 1)) != 0 || !traitBits.Add(trait.Bit))
            {
                throw new InvalidDataException($"Trait '{trait.Id}' must have a unique positive single-bit value.");
            }
        }

        if (factions.Select(faction => faction.FactionId).Distinct().Count() != factions.Count)
        {
            throw new InvalidDataException("Faction IDs must be unique.");
        }

        if (actions.Select(action => action.Hash).Distinct().Count() != actions.Count)
        {
            throw new InvalidDataException("Action hashes must be unique.");
        }
    }
}
