using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

/// <summary>JSON authoring shape for relationship affinity bonuses.</summary>
public sealed class AgentAffinityDocument
{
    public float AgeBandBonus { get; init; }
    public float OccupationBonus { get; init; }
    public float HomeLocationBonus { get; init; }
    public string? WealthAttribute { get; init; }
    public List<ResponsePoint>? WealthSimilarity { get; init; }
    public string? FamilyNetworkType { get; init; }
    public float FamilyBonus { get; init; }
    public float Minimum { get; init; }
    public float Maximum { get; init; }
}

/// <summary>Validated affinity content with schema and network names resolved once.</summary>
public sealed record AgentAffinitySettings(
    float AgeBandBonus,
    float OccupationBonus,
    float HomeLocationBonus,
    IReadOnlyList<ResponsePoint> WealthSimilarity,
    int WealthAttributeIndex,
    float WealthMinimum,
    float WealthMaximum,
    int FamilyNetworkTypeHash,
    float FamilyBonus,
    float Minimum,
    float Maximum);

/// <summary>Calculates the shared baseline and directional discovered-trait affinity.</summary>
public static class AgentAffinityCalculator
{
    public static float Calculate(Entity agent, Entity target, bool isFamily,
        long knownTraitMask, long allTraitBits, int traitCount, AgentAffinitySettings settings,
        float rapportDelta = 0f)
    {
        var affinity = CalculateSharedBonus(agent, target, isFamily, settings) + rapportDelta;
        if (traitCount > 0 && target.TryGetComponent<Psychology>(out var psychology))
        {
            var sharedMask = psychology.TraitMask & knownTraitMask & allTraitBits;
            affinity += System.Numerics.BitOperations.PopCount((ulong)sharedMask) * 100f / traitCount;
        }
        return Math.Clamp(affinity, settings.Minimum, settings.Maximum);
    }

    public static float CalculateSharedBonus(Entity agent, Entity target, bool isFamily,
        AgentAffinitySettings settings)
    {
        var bonus = isFamily ? settings.FamilyBonus : 0f;
        if (agent.TryGetComponent<SurveyDemographicProfile>(out var age) &&
            target.TryGetComponent<SurveyDemographicProfile>(out var targetAge) &&
            age.AgeBand == targetAge.AgeBand)
            bonus += settings.AgeBandBonus;

        if (agent.TryGetComponent<Identity>(out var identity) &&
            target.TryGetComponent<Identity>(out var targetIdentity) &&
            identity.OccupationId == targetIdentity.OccupationId)
            bonus += settings.OccupationBonus;

        if (agent.TryGetComponent<AgentLocation>(out var location) &&
            target.TryGetComponent<AgentLocation>(out var targetLocation) &&
            location.HomeLocationId == targetLocation.HomeLocationId)
            bonus += settings.HomeLocationBonus;

        if (agent.TryGetComponent<AgentAttributes>(out var attributes) &&
            target.TryGetComponent<AgentAttributes>(out var targetAttributes))
        {
            var firstWealth = attributes.Values[settings.WealthAttributeIndex];
            var secondWealth = targetAttributes.Values[settings.WealthAttributeIndex];
            var normalizedDifference = Math.Clamp(
                Math.Abs(firstWealth - secondWealth) / (settings.WealthMaximum - settings.WealthMinimum),
                0f, 1f);
            bonus += EvaluateCurve(normalizedDifference, settings.WealthSimilarity);
        }

        return bonus;
    }

    private static float EvaluateCurve(float x, IReadOnlyList<ResponsePoint> curve)
    {
        for (var index = 1; index < curve.Count; index++)
        {
            var right = curve[index];
            if (x > right.X) continue;
            var left = curve[index - 1];
            var amount = (x - left.X) / (right.X - left.X);
            return left.Y + (right.Y - left.Y) * amount;
        }
        return curve[^1].Y;
    }
}
