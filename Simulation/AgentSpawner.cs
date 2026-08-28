using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

public sealed class AgentSpawner
{
    private readonly ContentCatalog _catalog;
    private readonly AgentAttributeSchema _schema;
    private readonly WorldTopology _world;

    public AgentSpawner(ContentCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _schema = catalog.AgentAttributes;
        _world = catalog.World;
    }

    public int Spawn(EntityStore store, int count, Random random)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var assignments = new List<AgentWorldAssignment>(count);
        for (var index = 0; index < count; index++)
        {
            var job = _catalog.Jobs[random.Next(_catalog.Jobs.Count)];
            var home = ChooseLocation(random, SimulationDefaults.ResidentialLocationType);
            var workplace = ChooseLocation(random, job.WorkplaceType);
            var route = _world.FindShortestRoute(home.Hash, workplace.Hash)
                ?? throw new InvalidDataException(
                    $"No route exists from residential location '{home.Id}' to workplace type '{job.WorkplaceType}'.");

            assignments.Add(new AgentWorldAssignment(job, home, workplace, route));
        }

        foreach (var assignment in assignments)
        {
            var entity = store.CreateEntity(
                new Identity
                {
                    NameId = random.Next(),
                    OccupationId = assignment.Job.Hash
                },
                new PoliticalAlignment
                {
                    FactionId = _catalog.Factions[random.Next(_catalog.Factions.Count)].FactionId
                },
                new AgentAttributes
                {
                    Values = CreateAttributeValues(random)
                },
                new Psychology
                {
                    TraitMask = CreateTraitMask(random)
                },
                new AgentState
                {
                    CurrentActionHash = _catalog.Actions[random.Next(_catalog.Actions.Count)].Hash
                },
                new AgentLocation
                {
                    HomeLocationId = assignment.Home.Hash,
                    WorkLocationId = assignment.Workplace.Hash,
                    CurrentLocationId = assignment.Home.Hash
                },
                new AgentTravel
                {
                    RouteLocationIds = assignment.Route.LocationIds.ToArray(),
                    TotalTravelMinutes = assignment.Route.TravelMinutes,
                    RoutePosition = 0,
                    RemainingTravelMinutes = 0f,
                    Mode = AgentTravelMode.AtHome
                },
                Tags.Get<Tier1LodTag>());

            // Entity creation is the only structural operation in this loop.
            _ = entity;
        }

        return count;
    }

    private sealed record AgentWorldAssignment(
        JobDefinition Job,
        WorldLocationDefinition Home,
        WorldLocationDefinition Workplace,
        WorldRoute Route);

    private WorldLocationDefinition ChooseLocation(Random random, string type)
    {
        var locations = _world.GetLocationsByType(type);
        if (locations.Count == 0)
        {
            throw new InvalidDataException($"No world location is configured for type '{type}'.");
        }

        return locations[random.Next(locations.Count)];
    }

    private float[] CreateAttributeValues(Random random)
    {
        var values = new float[_schema.Count];
        for (var index = 0; index < _schema.Count; index++)
        {
            values[index] = NextBoundedNormal(random, _schema.Definitions[index]);
        }

        return values;
    }

    private long CreateTraitMask(Random random)
    {
        var mask = 0L;
        foreach (var trait in _catalog.Traits)
        {
            if (trait.Prevalence >= 1f || (trait.Prevalence > 0f && random.NextDouble() < trait.Prevalence))
            {
                mask |= trait.Bit;
            }
        }

        return mask;
    }

    private static float NextBoundedNormal(Random random, AgentAttributeDefinition definition)
    {
        if (definition.Min == definition.Max || definition.Average == definition.Min)
        {
            return definition.Min;
        }

        if (definition.Average == definition.Max)
        {
            return definition.Max;
        }

        var minimum = (double)definition.Min;
        var maximum = (double)definition.Max;
        var sigma = (maximum - minimum) / 6d;
        var mean = CalibrateTruncatedMean(minimum, maximum, definition.Average, sigma);
        var lowerCdf = NormalCdf((minimum - mean) / sigma);
        var upperCdf = NormalCdf((maximum - mean) / sigma);
        var probability = lowerCdf + (upperCdf - lowerCdf) * Math.Clamp(random.NextDouble(), 1e-12, 1d - 1e-12);
        var value = mean + sigma * InverseNormalCdf(probability);
        return (float)Math.Clamp(value, minimum, maximum);
    }

    private static double CalibrateTruncatedMean(double minimum, double maximum, double target, double sigma)
    {
        var lower = minimum - (maximum - minimum) * 16d;
        var upper = maximum + (maximum - minimum) * 16d;

        for (var iteration = 0; iteration < 64; iteration++)
        {
            var midpoint = (lower + upper) / 2d;
            if (TruncatedMean(minimum, maximum, midpoint, sigma) < target)
            {
                lower = midpoint;
            }
            else
            {
                upper = midpoint;
            }
        }

        return (lower + upper) / 2d;
    }

    private static double TruncatedMean(double minimum, double maximum, double mean, double sigma)
    {
        var alpha = (minimum - mean) / sigma;
        var beta = (maximum - mean) / sigma;
        var denominator = NormalCdf(beta) - NormalCdf(alpha);
        if (denominator <= double.Epsilon)
        {
            return Math.Clamp(mean, minimum, maximum);
        }

        var correction = (NormalPdf(alpha) - NormalPdf(beta)) / denominator;
        return mean + sigma * correction;
    }

    private static double NormalPdf(double value) => Math.Exp(-0.5d * value * value) / Math.Sqrt(2d * Math.PI);

    private static double NormalCdf(double value)
    {
        // Abramowitz and Stegun's error-function approximation is sufficient
        // for bounded sampling while keeping the generator dependency-free.
        var sign = value < 0d ? -1d : 1d;
        var absolute = Math.Abs(value) / Math.Sqrt(2d);
        var t = 1d / (1d + 0.3275911d * absolute);
        var polynomial = (((((1.061405429d * t) - 1.453152027d) * t) + 1.421413741d) * t - 0.284496736d) * t + 0.254829592d;
        var erf = sign * (1d - polynomial * t * Math.Exp(-absolute * absolute));
        return 0.5d * (1d + erf);
    }

    private static double InverseNormalCdf(double probability)
    {
        var lower = -8d;
        var upper = 8d;
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var midpoint = (lower + upper) / 2d;
            if (NormalCdf(midpoint) < probability)
            {
                lower = midpoint;
            }
            else
            {
                upper = midpoint;
            }
        }

        return (lower + upper) / 2d;
    }
}
