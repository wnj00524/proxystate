namespace ProxyState.Simulation;

public sealed record WorldRoute(IReadOnlyList<int> LocationIds, int TravelMinutes);

public sealed class WorldTopology
{
    private readonly Dictionary<int, WorldLocationDefinition> _locationsByHash;
    private readonly Dictionary<(int From, int To), int> _travelMinutes;
    private readonly Dictionary<int, List<(int Destination, int TravelMinutes)>> _neighbors;

    internal WorldTopology(
        IReadOnlyList<WorldLocationDefinition> locations,
        IReadOnlyList<WorldConnectionDefinition> connections)
    {
        Locations = locations;
        Connections = connections;
        _locationsByHash = locations.ToDictionary(location => location.Hash);
        _travelMinutes = new Dictionary<(int From, int To), int>();
        _neighbors = locations.ToDictionary(location => location.Hash, _ => new List<(int, int)>());

        foreach (var connection in connections)
        {
            var from = _locationsByHash.Values.First(location =>
                string.Equals(location.Id, connection.From, StringComparison.OrdinalIgnoreCase));
            var to = _locationsByHash.Values.First(location =>
                string.Equals(location.Id, connection.To, StringComparison.OrdinalIgnoreCase));

            AddConnection(from.Hash, to.Hash, connection.TravelMinutes);
            AddConnection(to.Hash, from.Hash, connection.TravelMinutes);
        }

        foreach (var neighbors in _neighbors.Values)
        {
            // Stable ordering makes equal-cost shortest paths reproducible.
            neighbors.Sort((left, right) => left.Destination.CompareTo(right.Destination));
        }
    }

    public IReadOnlyList<WorldLocationDefinition> Locations { get; }
    public IReadOnlyList<WorldConnectionDefinition> Connections { get; }

    public IReadOnlyList<WorldLocationDefinition> GetLocationsByType(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return Locations
            .Where(location => string.Equals(location.Type, type, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public WorldLocationDefinition GetLocation(int locationId)
    {
        return _locationsByHash.TryGetValue(locationId, out var location)
            ? location
            : throw new KeyNotFoundException($"Location {locationId} is not defined in the world topology.");
    }

    public int GetTravelMinutes(int fromLocationId, int toLocationId)
    {
        return _travelMinutes.TryGetValue((fromLocationId, toLocationId), out var minutes)
            ? minutes
            : throw new InvalidOperationException(
                $"No world connection exists from location {fromLocationId} to {toLocationId}.");
    }

    public WorldRoute? FindShortestRoute(int startLocationId, int destinationLocationId)
    {
        if (!_locationsByHash.ContainsKey(startLocationId) || !_locationsByHash.ContainsKey(destinationLocationId))
        {
            throw new KeyNotFoundException("A route endpoint is not defined in the world topology.");
        }

        if (startLocationId == destinationLocationId)
        {
            return new WorldRoute(new[] { startLocationId }, 0);
        }

        var distances = _locationsByHash.Keys.ToDictionary(locationId => locationId, _ => int.MaxValue);
        var previous = new Dictionary<int, int>();
        var unvisited = _locationsByHash.Keys.ToHashSet();
        distances[startLocationId] = 0;

        while (unvisited.Count > 0)
        {
            int? current = null;
            foreach (var candidate in unvisited)
            {
                if (current is null || distances[candidate] < distances[current.Value] ||
                    (distances[candidate] == distances[current.Value] && candidate < current.Value))
                {
                    current = candidate;
                }
            }

            if (current is null || distances[current.Value] == int.MaxValue)
            {
                break;
            }

            unvisited.Remove(current.Value);
            if (current.Value == destinationLocationId)
            {
                break;
            }

            foreach (var neighbor in _neighbors[current.Value])
            {
                if (!unvisited.Contains(neighbor.Destination))
                {
                    continue;
                }

                var candidateDistance = distances[current.Value] + neighbor.TravelMinutes;
                if (candidateDistance < distances[neighbor.Destination])
                {
                    distances[neighbor.Destination] = candidateDistance;
                    previous[neighbor.Destination] = current.Value;
                }
            }
        }

        if (!previous.ContainsKey(destinationLocationId))
        {
            return null;
        }

        var route = new List<int> { destinationLocationId };
        var cursor = destinationLocationId;
        while (cursor != startLocationId)
        {
            cursor = previous[cursor];
            route.Add(cursor);
        }

        route.Reverse();
        return new WorldRoute(route, distances[destinationLocationId]);
    }

    private void AddConnection(int from, int to, int travelMinutes)
    {
        _travelMinutes[(from, to)] = travelMinutes;
        _neighbors[from].Add((to, travelMinutes));
    }
}
