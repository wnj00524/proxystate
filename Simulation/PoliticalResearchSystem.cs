using Friflo.Engine.ECS;

namespace ProxyState.Simulation;

/// <summary>
/// Conducts aggregate-only political surveys. Contact eligibility is checked
/// at call time; the company receives a response or a generic nonresponse only.
/// </summary>
public sealed class PoliticalResearchSystem
{
    private readonly EntityStore _store;
    private readonly ContentCatalog _catalog;
    private readonly Entity _clock;
    private readonly int _seed;
    private readonly Dictionary<int, Entity> _agents;
    private readonly Dictionary<int, JobDefinition> _jobsByHash;
    private readonly Dictionary<string, ProviderRuntime> _providers;
    private readonly List<PendingCall> _pendingCalls = [];
    private int _lastStartedWave;
    private long _lastProcessedMinute = -1;

    public PoliticalResearchSystem(EntityStore store, ContentCatalog catalog, Entity clock, int seed)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock;
        _seed = seed;
        _agents = store.Query<Identity, PoliticalAlignment, AgentAttributes, AgentLocation,
                PoliticalParticipation>().Entities
            .Where(agent => agent.HasComponent<SurveyDemographicProfile>())
            .ToDictionary(agent => agent.Id);
        _jobsByHash = catalog.Jobs.ToDictionary(job => job.Hash);
        _providers = catalog.Research.Providers.ToDictionary(provider => provider.Id,
            provider => new ProviderRuntime(provider), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns immutable aggregate copies suitable for a presentation window.</summary>
    public IReadOnlyList<ResearchProviderProjection> GetProviderProjections() =>
        Array.AsReadOnly(_providers.Values.OrderBy(runtime => runtime.Definition.Id, StringComparer.Ordinal)
            .Select(runtime => new ResearchProviderProjection(runtime.Definition.Id,
                runtime.Definition.Name, runtime.Definition.Methodology,
                runtime.Definition.SamplingMethod, runtime.Definition.LikelyVoterOnly,
                runtime.History.LastOrDefault(), Array.AsReadOnly(runtime.History.ToArray()))).ToArray());

    public void Update()
    {
        var time = _clock.GetComponent<WorldTime>();
        var minute = (long)Math.Floor(time.ElapsedSimulationSeconds / SimulationDefaults.SimulationSecondsPerMinute);
        if (minute <= _lastProcessedMinute) return;
        _lastProcessedMinute = minute;
        var day = (int)(minute / SimulationDefaults.SimulationMinutesPerDay) + 1;
        var minuteOfDay = (int)(minute % SimulationDefaults.SimulationMinutesPerDay);
        var eligibleStartDay = ((day - 1) % _catalog.Research.CadenceDays) == 0;
        var wave = ((day - 1) / _catalog.Research.CadenceDays) + 1;
        if (eligibleStartDay && minuteOfDay >= _catalog.Research.CallStartMinute && wave > _lastStartedWave)
        {
            _lastStartedWave = wave;
            StartWave(wave, day, minute);
        }

        for (var index = 0; index < _pendingCalls.Count;)
        {
            if (_pendingCalls[index].DueMinute > minute)
            {
                index++;
                continue;
            }
            var call = _pendingCalls[index];
            _pendingCalls.RemoveAt(index);
            ProcessCall(call);
        }
    }

    private void StartWave(int wave, int startDay, long currentMinute)
    {
        var frame = _store.Query<Identity, PoliticalAlignment, AgentAttributes, AgentLocation,
                PoliticalParticipation>().Entities
            .Where(agent => agent.HasComponent<SurveyDemographicProfile>())
            .OrderBy(agent => agent.Id).ToArray();
        foreach (var runtime in _providers.Values.OrderBy(value => value.Definition.Id, StringComparer.Ordinal))
        {
            var provider = runtime.Definition;
            var attemptCount = Math.Min(provider.AttemptCount, frame.Length);
            if (attemptCount == 0) continue;
            var random = new Random(unchecked(_seed ^ provider.SeedOffset ^ wave * 0x45D9F3B));
            var selected = SelectSample(frame, provider, attemptCount, random);
            var waveState = runtime.GetWave(wave, startDay);
            waveState.ExpectedAttempts = attemptCount;
            waveState.PopulationFrame = frame;
            var startOfDay = (long)(startDay - 1) * SimulationDefaults.SimulationMinutesPerDay;
            var fieldworkMinutes = _catalog.Research.FieldworkDays * SimulationDefaults.SimulationMinutesPerDay;
            var callWindow = _catalog.Research.CallEndMinute - _catalog.Research.CallStartMinute;
            foreach (var entry in selected)
            {
                var offset = random.Next(fieldworkMinutes);
                var callDayOffset = offset / SimulationDefaults.SimulationMinutesPerDay;
                var minuteOffset = offset % SimulationDefaults.SimulationMinutesPerDay;
                var minuteOfDay = _catalog.Research.CallStartMinute + minuteOffset % callWindow;
                var due = Math.Max(currentMinute, startOfDay + callDayOffset * SimulationDefaults.SimulationMinutesPerDay + minuteOfDay);
                _pendingCalls.Add(new PendingCall(runtime, wave, startDay, entry.Agent.Id,
                    due, entry.DesignWeight));
            }
        }
        _pendingCalls.Sort(PendingCall.Compare);
    }

    private (Entity Agent, double DesignWeight)[] SelectSample(Entity[] frame,
        SurveyProviderDefinition provider, int attemptCount, Random random)
    {
        var groups = frame.GroupBy(agent => StratumKey(agent, provider.SamplingStrata))
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var counts = groups.Select(group => group.Count()).ToArray();
        var allocations = provider.SamplingMethod == "demographic-quota"
            ? AllocateQuotas(counts, attemptCount)
            : Allocate(counts, attemptCount);
        var selected = new List<(Entity Agent, double DesignWeight)>(attemptCount);
        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var quota = allocations[groupIndex];
            if (quota == 0) continue;
            var candidates = groups[groupIndex].ToArray();
            for (var index = 0; index < quota; index++)
            {
                var other = random.Next(index, candidates.Length);
                (candidates[index], candidates[other]) = (candidates[other], candidates[index]);
            }
            var weight = groups[groupIndex].Count() / (double)quota;
            selected.AddRange(candidates.Take(quota).Select(agent => (agent, weight)));
        }
        return selected.ToArray();
    }

    private static int[] AllocateQuotas(IReadOnlyList<int> populationByGroup, int sampleSize)
    {
        var result = new int[populationByGroup.Count];
        if (sampleSize >= populationByGroup.Sum()) return populationByGroup.ToArray();
        var nonEmpty = Enumerable.Range(0, populationByGroup.Count)
            .Where(index => populationByGroup[index] > 0).ToArray();
        if (sampleSize < nonEmpty.Length) return Allocate(populationByGroup, sampleSize);
        foreach (var index in nonEmpty) result[index] = 1;
        var remaining = sampleSize - nonEmpty.Length;
        while (remaining > 0)
        {
            var capacity = populationByGroup.Select((count, index) => Math.Max(0, count - result[index])).ToArray();
            if (capacity.Sum() == 0) break;
            var proposal = Allocate(capacity, remaining);
            var assigned = 0;
            for (var index = 0; index < result.Length; index++)
            {
                var addition = Math.Min(capacity[index], proposal[index]);
                result[index] += addition;
                assigned += addition;
            }
            if (assigned == 0)
            {
                var index = Enumerable.Range(0, result.Length).First(candidate => capacity[candidate] > 0);
                result[index]++;
                assigned = 1;
            }
            remaining -= assigned;
        }
        return result;
    }

    private static int[] Allocate(IReadOnlyList<int> populationByGroup, int sampleSize)
    {
        var population = populationByGroup.Sum();
        var exact = populationByGroup.Select(count => sampleSize * (double)count / population).ToArray();
        var result = exact.Select(Math.Floor).Select(value => (int)value).ToArray();
        var remaining = sampleSize - result.Sum();
        foreach (var index in Enumerable.Range(0, exact.Length)
                     .OrderByDescending(index => exact[index] - result[index]).ThenBy(index => index).Take(remaining))
            result[index]++;
        return result;
    }

    private void ProcessCall(PendingCall call)
    {
        var wave = call.Provider.GetWave(call.Wave, call.StartDay);
        wave.CallsAttempted++;
        var electionId = (int)(call.DueMinute / SimulationDefaults.SimulationMinutesPerDay) /
                         _catalog.Politics.ElectionIntervalDays + 1;
        var candidates = GetCandidates(electionId);
        if (candidates.Length > 0)
            wave.CandidateOptions[_catalog.Research.CandidateOfficeId] = candidates.Select(candidate => candidate.Id).ToHashSet();
        if (_agents.TryGetValue(call.AgentId, out var agent) && IsAtHome(agent) && WillRespond(agent, call))
        {
            var alignment = agent.GetComponent<PoliticalAlignment>().FactionId;
            var attributes = agent.GetComponent<AgentAttributes>().Values;
            var engagement = attributes[_catalog.Politics.PoliticalEngagementAttributeIndex] / 100f;
            var random = WaveRandom(call.Provider.Definition, call.Wave, call.AgentId);
            byte? party = random.NextDouble() < _catalog.Research.BaseUndecidedProbability +
                (1f - engagement) * _catalog.Research.LowEngagementUndecidedWeight ? null : alignment;
            var participation = agent.GetComponent<PoliticalParticipation>();
            var likely = engagement * .6f + attributes[_catalog.Politics.MotivationAttributeIndex] / 100f * .25f +
                         (participation.VotedElectionId > 0 ? .15f : 0f) >= .42f;
            var preference = party ?? alignment;
            var candidate = ChooseCandidate(agent, preference, candidates);
            wave.Responses.Add(new SurveyRespondent(agent.Id, agent.GetComponent<SurveyDemographicProfile>(),
                District(agent), Sector(agent), party, candidate.AgentId, candidate.OfficeId,
                likely, call.DesignWeight));
        }

        if (wave.CallsAttempted != wave.ExpectedAttempts) return;
        call.Provider.History.Add(Publish(call.Provider.Definition, wave, call.Wave,
            (int)(call.DueMinute / SimulationDefaults.SimulationMinutesPerDay) + 1));
    }

    private bool WillRespond(Entity agent, PendingCall call)
    {
        var attributes = agent.GetComponent<AgentAttributes>().Values;
        var engagement = attributes[_catalog.Politics.PoliticalEngagementAttributeIndex] / 100f;
        var motivation = attributes[_catalog.Politics.MotivationAttributeIndex] / 100f;
        var probability = _catalog.Research.BaseResponseProbability +
                          engagement * _catalog.Research.EngagementResponseWeight +
                          motivation * _catalog.Research.MotivationResponseWeight;
        return WaveRandom(call.Provider.Definition, call.Wave, call.AgentId + 173).NextDouble() < probability;
    }

    private bool IsAtHome(Entity agent)
    {
        var location = agent.GetComponent<AgentLocation>();
        return location.CurrentLocationId == location.HomeLocationId;
    }

    private Entity[] GetCandidates(int electionId)
    {
        var officeHash = _jobsByHash.Values.First(job => string.Equals(job.Id,
            _catalog.Research.CandidateOfficeId, StringComparison.OrdinalIgnoreCase)).Hash;
        return _store.Query<Identity, PoliticalAlignment, AgentAttributes,
                PoliticalParticipation>().Entities
            .Where(candidate =>
            {
                var participation = candidate.GetComponent<PoliticalParticipation>();
                return participation.CandidateElectionId == electionId && participation.CandidateJobHash == officeHash;
            })
            .OrderBy(candidate => candidate.Id).ToArray();
    }

    private (int? AgentId, string? OfficeId) ChooseCandidate(Entity voter, byte factionId, Entity[] candidates)
    {
        if (candidates.Length == 0) return (null, null);
        var aligned = candidates.Where(candidate => candidate.GetComponent<PoliticalAlignment>().FactionId == factionId).ToArray();
        var choices = aligned.Length > 0 ? aligned : candidates;
        var selected = choices.OrderByDescending(candidate =>
                candidate.GetComponent<AgentAttributes>().Values[_catalog.AgentAttributes.GetIndex("charisma")])
            .ThenBy(candidate => candidate.Id).First();
        return (selected.Id, _catalog.Research.CandidateOfficeId);
    }

    private OpinionPollSnapshot Publish(SurveyProviderDefinition provider, PollWave wave,
        int waveNumber, int publishedDay)
    {
        var respondents = provider.LikelyVoterOnly
            ? wave.Responses.Where(response => response.LikelyVoter).ToArray()
            : wave.Responses.ToArray();
        var calibrated = CalibrateWeights(provider, respondents, wave.PopulationFrame);
        var party = _catalog.Factions.Select(faction => Estimate(
                faction.FactionId.ToString(), faction.Name,
                respondents.Where(response => response.PartyFactionId == faction.FactionId).ToArray(),
                calibrated, respondents))
            .Append(Estimate("undecided", "Undecided / no party", respondents.Where(response => response.PartyFactionId is null).ToArray(), calibrated, respondents))
            .ToArray();

        var candidateResults = wave.CandidateOptions.OrderBy(option => option.Key, StringComparer.Ordinal)
            .Select(option =>
            {
                var raceResponses = respondents.Where(response => response.CandidateOfficeId == option.Key).ToArray();
                var estimates = option.Value.Order().Select(candidateId => Estimate(candidateId.ToString(),
                    $"Candidate {candidateId}", raceResponses.Where(response => response.CandidateAgentId == candidateId).ToArray(),
                    calibrated, raceResponses)).ToArray();
                return new CandidateSupportEstimate(option.Key, Array.AsReadOnly(estimates));
            }).ToArray();
        var candidateRespondents = respondents.Count(response => response.CandidateAgentId is not null);
        var effectiveN = EffectiveSampleSize(respondents.Select(response => calibrated[response]).ToArray());
        return new OpinionPollSnapshot(waveNumber, wave.StartDay, publishedDay,
            wave.CallsAttempted, wave.Responses.Count, wave.CallsAttempted - wave.Responses.Count,
            wave.Responses.Count(response => response.LikelyVoter),
            (float)(candidateRespondents == 0 ? effectiveN : EffectiveSampleSize(respondents
                .Where(response => response.CandidateAgentId is not null).Select(response => calibrated[response]).ToArray())),
            provider.Methodology, Array.AsReadOnly(party), Array.AsReadOnly(candidateResults));
    }

    private PollEstimate Estimate(string id, string label, SurveyRespondent[] category,
        IReadOnlyDictionary<SurveyRespondent, double> weights, SurveyRespondent[] denominatorRows)
    {
        var denominator = denominatorRows.Sum(row => weights[row]);
        var categoryWeight = category.Sum(row => weights[row]);
        var pct = denominator <= 0 ? 0f : (float)(categoryWeight / denominator * 100d);
        var rawPct = denominatorRows.Length == 0 ? 0f : category.Length * 100f / denominatorRows.Length;
        var effectiveN = EffectiveSampleSize(denominatorRows.Select(row => weights[row]).ToArray());
        var interval = WilsonInterval(pct / 100f, effectiveN);
        return new PollEstimate(id, label, category.Length, rawPct, pct,
            (float)(interval.Lower * 100d), (float)(interval.Upper * 100d));
    }

    private Dictionary<SurveyRespondent, double> CalibrateWeights(SurveyProviderDefinition provider,
        SurveyRespondent[] respondents, Entity[] populationFrame)
    {
        var weights = respondents.ToDictionary(response => response, response => response.DesignWeight);
        if (respondents.Length == 0) return weights;
        var dimensions = new (string Name, Func<SurveyRespondent, string> SampleKey,
            Func<Entity, string> PopulationKey)[]
        {
            ("ageBand", row => Category(_catalog.Research.AgeBands, row.Profile.AgeBand),
                agent => Category(_catalog.Research.AgeBands, agent.GetComponent<SurveyDemographicProfile>().AgeBand)),
            ("gender", row => Category(_catalog.Research.Genders, row.Profile.Gender),
                agent => Category(_catalog.Research.Genders, agent.GetComponent<SurveyDemographicProfile>().Gender)),
            ("education", row => Category(_catalog.Research.EducationLevels, row.Profile.Education),
                agent => Category(_catalog.Research.EducationLevels, agent.GetComponent<SurveyDemographicProfile>().Education)),
            ("district", row => row.District, District),
            ("sector", row => row.Sector, Sector)
        };
        var selectedDimensions = dimensions;
        var caps = provider.MaximumWeight;
        var mean = weights.Values.Average();
        foreach (var response in respondents) weights[response] /= mean;
        for (var iteration = 0; iteration < 24; iteration++)
        foreach (var dimension in selectedDimensions)
        {
            var target = populationFrame.GroupBy(dimension.PopulationKey).ToDictionary(group => group.Key,
                group => group.Count() / (double)populationFrame.Length, StringComparer.Ordinal);
            var sampleWeights = respondents.GroupBy(dimension.SampleKey).ToDictionary(group => group.Key,
                group => group.Sum(row => weights[row]), StringComparer.Ordinal);
            var totalWeight = weights.Values.Sum();
            var adjustment = target.ToDictionary(pair => pair.Key,
                pair => sampleWeights.TryGetValue(pair.Key, out var current) && current > 0
                    ? pair.Value / (current / totalWeight) : 1d, StringComparer.Ordinal);
            foreach (var response in respondents)
            {
                var key = dimension.SampleKey(response);
                if (adjustment.TryGetValue(key, out var factor))
                    weights[response] = Math.Clamp(weights[response] * factor, .2d, caps);
            }
        }
        // Percentages and Kish n are invariant to a common multiplier. Avoid a
        // final mean normalization so the authored maximum-weight cap remains
        // true in the published estimator weights.
        return weights;
    }

    private string StratumKey(Entity agent, IReadOnlyList<string> strata) => string.Join('|', strata.Select(stratum => stratum.ToLowerInvariant() switch
    {
        "ageband" => Category(_catalog.Research.AgeBands, agent.GetComponent<SurveyDemographicProfile>().AgeBand),
        "gender" => Category(_catalog.Research.Genders, agent.GetComponent<SurveyDemographicProfile>().Gender),
        "education" => Category(_catalog.Research.EducationLevels, agent.GetComponent<SurveyDemographicProfile>().Education),
        "district" => District(agent),
        "sector" => Sector(agent),
        _ => string.Empty
    }));

    private static string Category(IReadOnlyList<SurveyCategoryDefinition> categories, byte value) =>
        value < categories.Count ? categories[value].Id : "unknown";

    private string District(Entity agent)
    {
        var id = agent.GetComponent<AgentLocation>().HomeLocationId;
        return _catalog.World.Locations.FirstOrDefault(location => location.Hash == id)?.Id ?? "unknown";
    }

    private string Sector(Entity agent)
    {
        var hash = agent.GetComponent<Identity>().OccupationId;
        return _jobsByHash.TryGetValue(hash, out var job) ? job.Sector : "unknown";
    }

    private static double EffectiveSampleSize(IReadOnlyList<double> weights)
    {
        var sum = weights.Sum();
        var squareSum = weights.Sum(weight => weight * weight);
        return squareSum <= 0 ? 0 : sum * sum / squareSum;
    }

    private static (double Lower, double Upper) WilsonInterval(double proportion, double effectiveN)
    {
        if (effectiveN <= 0) return (0, 1);
        const double z = 1.959963984540054;
        var denominator = 1d + z * z / effectiveN;
        var center = (proportion + z * z / (2d * effectiveN)) / denominator;
        var margin = z * Math.Sqrt(proportion * (1d - proportion) / effectiveN + z * z /
            (4d * effectiveN * effectiveN)) / denominator;
        return (Math.Max(0, center - margin), Math.Min(1, center + margin));
    }

    private Random WaveRandom(SurveyProviderDefinition provider, int wave, int agentId) =>
        new(unchecked(_seed ^ provider.SeedOffset ^ wave * 0x27D4EB2D ^ agentId * 0x165667B1));

    private sealed class ProviderRuntime(SurveyProviderDefinition definition)
    {
        public SurveyProviderDefinition Definition { get; } = definition;
        public List<OpinionPollSnapshot> History { get; } = [];
        private readonly Dictionary<int, PollWave> _waves = [];
        public PollWave GetWave(int wave, int startDay)
        {
            if (!_waves.TryGetValue(wave, out var state))
                _waves.Add(wave, state = new PollWave(startDay));
            return state;
        }
    }

    private sealed class PollWave(int startDay)
    {
        public int StartDay { get; } = startDay;
        public int ExpectedAttempts { get; set; }
        public int CallsAttempted { get; set; }
        public List<SurveyRespondent> Responses { get; } = [];
        public Entity[] PopulationFrame { get; set; } = [];
        public Dictionary<string, HashSet<int>> CandidateOptions { get; } = new(StringComparer.Ordinal);
    }

    private sealed record SurveyRespondent(int AgentId, SurveyDemographicProfile Profile, string District,
        string Sector, byte? PartyFactionId, int? CandidateAgentId, string? CandidateOfficeId,
        bool LikelyVoter, double DesignWeight);
    private sealed record PendingCall(ProviderRuntime Provider, int Wave, int StartDay,
        int AgentId, long DueMinute, double DesignWeight)
    {
        public static int Compare(PendingCall first, PendingCall second)
        {
            var minute = first.DueMinute.CompareTo(second.DueMinute);
            if (minute != 0) return minute;
            var provider = StringComparer.Ordinal.Compare(first.Provider.Definition.Id, second.Provider.Definition.Id);
            return provider != 0 ? provider : first.AgentId.CompareTo(second.AgentId);
        }
    }
}
