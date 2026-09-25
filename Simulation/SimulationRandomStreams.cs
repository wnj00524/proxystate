namespace ProxyState.Simulation;

/// <summary>
/// Derives stable, independent pseudo-random streams from one replay seed.
/// Adding draws to one simulation phase therefore cannot perturb another.
/// </summary>
internal static class SimulationRandomStreams
{
    public static Random Population(int seed) => Create(seed, 0x243F6A88u);
    public static Random Operatives(int seed) => Create(seed, 0x85A308D3u);
    public static Random Networks(int seed) => Create(seed, 0x13198A2Eu);
    public static Random SocialGraph(int seed) => Create(seed, 0x03707344u);
    public static Random Interactions(int seed) => Create(seed, 0xA4093822u);
    public static Random SurveyProfiles(int seed) => Create(seed, 0x082EFA98u);

    private static Random Create(int seed, uint stream)
    {
        // SplitMix32-style avalanche keeps neighboring root seeds and stream
        // constants from producing correlated System.Random seeds.
        var value = unchecked((uint)seed + stream + 0x9E3779B9u);
        value = (value ^ (value >> 16)) * 0x21F0AAADu;
        value = (value ^ (value >> 15)) * 0x735A2D97u;
        value ^= value >> 15;
        return new Random(unchecked((int)value));
    }
}
