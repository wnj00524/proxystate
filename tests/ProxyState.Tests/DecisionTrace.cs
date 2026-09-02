using Friflo.Engine.ECS;
using ProxyState.Simulation;
using System.Collections.ObjectModel;

namespace ProxyState.Tests;

// This immutable projection lets behavioural tests compare deliberation and
// execution without reaching through any UI or retaining a live ECS entity.
public sealed record DecisionTrace(
    int IntentHash,
    int TargetEntityId,
    int TargetLocationId,
    float Utility,
    long SelectedAtMinute,
    AgentTravelMode TravelMode,
    ActivityPhase ActivityPhase,
    int ActivityActionHash,
    int ActivityTypeHash,
    IReadOnlyDictionary<int, long> Cooldowns)
{
    public static DecisionTrace Capture(Entity entity)
    {
        var intention = entity.GetComponent<IntentionState>();
        var travel = entity.GetComponent<AgentTravel>();
        var activity = entity.GetComponent<ActivityState>();
        var decision = entity.GetComponent<DecisionState>();
        var cooldowns = ((ReadOnlySpan<int>)decision.CooldownActionHashes).ToArray()
            .Select((hash, index) => (hash, until: decision.CooldownUntilMinutes[index]))
            .Where(item => item.hash != 0)
            .ToDictionary(item => item.hash, item => item.until);
        return new DecisionTrace(intention.ActionHash, intention.TargetEntityId,
            intention.TargetLocationId, intention.Utility, intention.SelectedAtMinute,
            travel.Mode, activity.Phase, activity.ActionHash, activity.ActivityTypeHash,
            new ReadOnlyDictionary<int, long>(cooldowns));
    }
}
