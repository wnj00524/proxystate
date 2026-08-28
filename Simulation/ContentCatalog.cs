using System.Text.Json;

namespace ProxyState.Simulation;

public sealed record TraitDefinition(string Id, string Name, long Bit, float Prevalence);
public sealed record ActionDefinition(string Id, string Name, int Hash);
public sealed record FactionDefinition(string Id, string Name, byte FactionId);
public sealed record AgentAttributeDefinition(string Id, float Min, float Max, float Average);
public sealed record JobDefinition(
    string Id,
    string Name,
    int Hash,
    int WorkStartMinute,
    int WorkEndMinute,
    List<int> WorkDays,
    string WorkplaceType);
public sealed record WorldLocationDefinition(string Id, string Name, int Hash, string Type);
public sealed record WorldConnectionDefinition(string From, string To, int TravelMinutes);

public sealed class AgentAttributeSchema
{
    private readonly Dictionary<string, int> _indices;

    internal AgentAttributeSchema(IReadOnlyList<AgentAttributeDefinition> definitions)
    {
        Definitions = definitions;
        _indices = definitions
            .Select((definition, index) => new { definition.Id, index })
            .ToDictionary(item => item.Id, item => item.index, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AgentAttributeDefinition> Definitions { get; }

    public int Count => Definitions.Count;

    public int GetIndex(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _indices.TryGetValue(id, out var index)
            ? index
            : throw new KeyNotFoundException($"Agent attribute '{id}' is not defined in the schema.");
    }
}

public sealed class AgentSchemaDocument
{
    public List<AgentAttributeDefinition>? Attributes { get; init; }
}

public sealed class WorldDocument
{
    public List<WorldLocationDefinition>? Locations { get; init; }
    public List<WorldConnectionDefinition>? Connections { get; init; }
}

public sealed class ContentCatalog
{
    private ContentCatalog(
        IReadOnlyList<TraitDefinition> traits,
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyList<FactionDefinition> factions,
        AgentAttributeSchema agentAttributes,
        IReadOnlyList<JobDefinition> jobs,
        WorldTopology world)
    {
        Traits = traits;
        Actions = actions;
        Factions = factions;
        AgentAttributes = agentAttributes;
        Jobs = jobs;
        World = world;
        AllTraitBits = traits.Aggregate(0L, (mask, trait) => mask | trait.Bit);
    }

    public IReadOnlyList<TraitDefinition> Traits { get; }
    public IReadOnlyList<ActionDefinition> Actions { get; }
    public IReadOnlyList<FactionDefinition> Factions { get; }
    public AgentAttributeSchema AgentAttributes { get; }
    public IReadOnlyList<JobDefinition> Jobs { get; }
    public WorldTopology World { get; }
    public long AllTraitBits { get; }

    public static ContentCatalog Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var traits = LoadFile<TraitDefinition>(directory, "traits.json", options);
        var actions = LoadFile<ActionDefinition>(directory, "actions.json", options);
        var factions = LoadFile<FactionDefinition>(directory, "factions.json", options);
        var schemaDocument = LoadObject<AgentSchemaDocument>(directory, "agent-schema.json", options);
        var jobs = LoadFile<JobDefinition>(directory, "jobs.json", options);
        var worldDocument = LoadObject<WorldDocument>(directory, "world.json", options);

        var agentAttributes = Validate(traits, actions, factions, schemaDocument.Attributes);
        var world = ValidateWorld(jobs, worldDocument.Locations, worldDocument.Connections);
        return new ContentCatalog(traits, actions, factions, agentAttributes, jobs, world);
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

    private static T LoadObject<T>(
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
        return JsonSerializer.Deserialize<T>(json, options)
            ?? throw new InvalidDataException($"Content file is empty or invalid: {path}");
    }

    private static AgentAttributeSchema Validate(
        IReadOnlyList<TraitDefinition> traits,
        IReadOnlyList<ActionDefinition> actions,
        IReadOnlyList<FactionDefinition> factions,
        IReadOnlyList<AgentAttributeDefinition>? attributeDefinitions)
    {
        if (traits.Count == 0 || actions.Count == 0 || factions.Count == 0)
        {
            throw new InvalidDataException("Traits, actions, and factions must each contain at least one definition.");
        }

        if (attributeDefinitions is null || attributeDefinitions.Count == 0)
        {
            throw new InvalidDataException("The agent attribute schema must contain at least one attribute.");
        }

        var traitBits = new HashSet<long>();
        var traitIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trait in traits)
        {
            if (string.IsNullOrWhiteSpace(trait.Id) || !traitIds.Add(trait.Id))
            {
                throw new InvalidDataException($"Trait IDs must be non-empty and unique; '{trait.Id}' is invalid or duplicated.");
            }

            if (trait.Bit <= 0 || (trait.Bit & (trait.Bit - 1)) != 0 || !traitBits.Add(trait.Bit))
            {
                throw new InvalidDataException($"Trait '{trait.Id}' must have a unique positive single-bit value.");
            }

            if (!float.IsFinite(trait.Prevalence) || trait.Prevalence is < 0f or > 1f)
            {
                throw new InvalidDataException($"Trait '{trait.Id}' prevalence must be a finite value between 0 and 1.");
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

        var attributeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributeDefinitions)
        {
            if (string.IsNullOrWhiteSpace(attribute.Id) || !attributeIds.Add(attribute.Id))
            {
                throw new InvalidDataException($"Agent attribute IDs must be non-empty and unique; '{attribute.Id}' is invalid or duplicated.");
            }

            if (!float.IsFinite(attribute.Min) || !float.IsFinite(attribute.Max) || !float.IsFinite(attribute.Average))
            {
                throw new InvalidDataException($"Agent attribute '{attribute.Id}' must contain only finite numeric values.");
            }

            if (attribute.Min > attribute.Max || attribute.Average < attribute.Min || attribute.Average > attribute.Max)
            {
                throw new InvalidDataException($"Agent attribute '{attribute.Id}' must satisfy min <= average <= max.");
            }
        }

        var schema = new AgentAttributeSchema(attributeDefinitions);
        _ = schema.GetIndex("fatigue");
        _ = schema.GetIndex("stress");
        return schema;
    }

    private static WorldTopology ValidateWorld(
        IReadOnlyList<JobDefinition> jobs,
        IReadOnlyList<WorldLocationDefinition>? locations,
        IReadOnlyList<WorldConnectionDefinition>? connections)
    {
        if (jobs.Count == 0)
        {
            throw new InvalidDataException("At least one job definition is required.");
        }

        var jobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobHashes = new HashSet<int>();
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id) || !jobIds.Add(job.Id))
            {
                throw new InvalidDataException($"Job IDs must be non-empty and unique; '{job.Id}' is invalid or duplicated.");
            }

            if (!jobHashes.Add(job.Hash))
            {
                throw new InvalidDataException($"Job hashes must be unique; '{job.Hash}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(job.Name) || string.IsNullOrWhiteSpace(job.WorkplaceType))
            {
                throw new InvalidDataException($"Job '{job.Id}' must have a name and workplace type.");
            }

            if (job.WorkStartMinute < 0 || job.WorkEndMinute > SimulationDefaults.SimulationMinutesPerDay ||
                job.WorkStartMinute >= job.WorkEndMinute)
            {
                throw new InvalidDataException($"Job '{job.Id}' must define a non-overnight interval within a day.");
            }

            if (job.WorkDays is null || job.WorkDays.Count == 0 ||
                job.WorkDays.Any(day => day < 1 || day > SimulationDefaults.DaysPerWeek) ||
                job.WorkDays.Distinct().Count() != job.WorkDays.Count)
            {
                throw new InvalidDataException($"Job '{job.Id}' must define unique workdays from 1 through 7.");
            }
        }

        if (locations is null || locations.Count == 0)
        {
            throw new InvalidDataException("The world must contain at least one location.");
        }

        var locationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locationHashes = new HashSet<int>();
        foreach (var location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.Id) || !locationIds.Add(location.Id))
            {
                throw new InvalidDataException($"Location IDs must be non-empty and unique; '{location.Id}' is invalid or duplicated.");
            }

            if (!locationHashes.Add(location.Hash))
            {
                throw new InvalidDataException($"Location hashes must be unique; '{location.Hash}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(location.Name) || string.IsNullOrWhiteSpace(location.Type))
            {
                throw new InvalidDataException($"Location '{location.Id}' must have a name and type.");
            }
        }

        if (!locations.Any(location => string.Equals(
                location.Type,
                SimulationDefaults.ResidentialLocationType,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The world must contain at least one residential location.");
        }

        var locationTypes = locations
            .Select(location => location.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (!locationTypes.Contains(job.WorkplaceType))
            {
                throw new InvalidDataException($"Job '{job.Id}' requires unavailable workplace type '{job.WorkplaceType}'.");
            }
        }

        if (connections is null || connections.Count == 0)
        {
            throw new InvalidDataException("The world must contain at least one connection.");
        }

        var locationById = locations.ToDictionary(location => location.Id, StringComparer.OrdinalIgnoreCase);
        var connectionPairs = new HashSet<(int From, int To)>();
        foreach (var connection in connections)
        {
            if (string.IsNullOrWhiteSpace(connection.From) || string.IsNullOrWhiteSpace(connection.To) ||
                string.Equals(connection.From, connection.To, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("World connections must connect two different locations.");
            }

            if (!locationById.TryGetValue(connection.From, out var from) ||
                !locationById.TryGetValue(connection.To, out var to))
            {
                throw new InvalidDataException($"World connection '{connection.From}' -> '{connection.To}' references an unknown location.");
            }

            if (connection.TravelMinutes <= 0)
            {
                throw new InvalidDataException("World connection travel durations must be positive.");
            }

            var pair = from.Hash < to.Hash
                ? (from.Hash, to.Hash)
                : (to.Hash, from.Hash);
            if (!connectionPairs.Add(pair))
            {
                throw new InvalidDataException($"World connection '{connection.From}' -> '{connection.To}' is duplicated.");
            }
        }

        return new WorldTopology(locations, connections);
    }
}
