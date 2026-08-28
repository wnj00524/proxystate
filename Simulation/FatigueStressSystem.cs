using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

public sealed class FatigueStressSystem : QuerySystem<AgentState>
{
    private readonly float _increasePerTick;

    public FatigueStressSystem(float increasePerTick = SimulationDefaults.FatigueStressIncreasePerTick)
    {
        if (increasePerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(increasePerTick), "The update amount must be positive.");
        }

        _increasePerTick = increasePerTick;
        Filter.AllTags(Tags.Get<Tier1LodTag>());
    }

    protected override void OnUpdate()
    {
        Query.ForEachEntity((ref AgentState state, Entity _) =>
        {
            state.Fatigue = IncreaseOrReset(state.Fatigue);
            state.Stress = IncreaseOrReset(state.Stress);
        });
    }

    private float IncreaseOrReset(float value)
    {
        var updated = value + _increasePerTick;
        return updated >= SimulationDefaults.MaximumFatigueStress ? 0f : updated;
    }
}
