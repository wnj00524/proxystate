namespace ProxyState.Simulation;

/// <summary>A concrete interval in a shared seven-day Tier 3 itinerary.</summary>
public readonly record struct CoarseRoutineInterval(
    int StartMinute, int EndMinute, ushort IntentIndex, int IntentHash,
    CoarseRoutineLocation Location, EffectSubject EffectRole);

/// <summary>Immutable profile data shared by all agents with one material routine key.</summary>
public sealed class CoarseRoutineProfile
{
    internal CoarseRoutineProfile(int id, ulong fingerprint, CoarseRoutineInterval[] intervals)
    {
        Id = id; Fingerprint = fingerprint; Intervals = intervals;
    }

    public int Id { get; }
    public ulong Fingerprint { get; }
    public IReadOnlyList<CoarseRoutineInterval> Intervals { get; }

    // Intervals are sorted and non-overlapping, making this a bounded binary
    // search instead of a per-agent schedule allocation or string lookup.
    public CoarseRoutineInterval GetSegment(long simulatedMinute)
    {
        var minute = (int)(simulatedMinute % SimulationDefaults.SimulationMinutesPerWeek);
        if (minute < 0) minute += SimulationDefaults.SimulationMinutesPerWeek;
        var low = 0; var high = Intervals.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var segment = Intervals[middle];
            if (minute < segment.StartMinute) high = middle - 1;
            else if (minute >= segment.EndMinute) low = middle + 1;
            else return segment;
        }
        throw new InvalidOperationException("A compiled Tier 3 profile does not cover the requested minute.");
    }

    /// <summary>Visits the profile portions overlapped by [startMinute, endMinute).</summary>
    public void ForEachOverlap(long startMinute, long endMinute, Action<CoarseRoutineInterval, int> visitor)
    {
        if (endMinute <= startMinute) return;
        var cursor = startMinute;
        while (cursor < endMinute)
        {
            var weekOffset = FloorDiv(cursor, SimulationDefaults.SimulationMinutesPerWeek);
            var weekStart = weekOffset * SimulationDefaults.SimulationMinutesPerWeek;
            var localStart = (int)(cursor - weekStart);
            var localEnd = (int)Math.Min(endMinute - weekStart, SimulationDefaults.SimulationMinutesPerWeek);
            foreach (var interval in Intervals)
            {
                var overlapStart = Math.Max(localStart, interval.StartMinute);
                var overlapEnd = Math.Min(localEnd, interval.EndMinute);
                if (overlapEnd > overlapStart) visitor(interval, overlapEnd - overlapStart);
                if (interval.StartMinute >= localEnd) break;
            }
            cursor = weekStart + SimulationDefaults.SimulationMinutesPerWeek;
        }
    }

    private static long FloorDiv(long value, int divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
}

/// <summary>Compiles and retains profiles by material key, never by agent identity.</summary>
public sealed class CoarseRoutineProfileCache
{
    private readonly ContentCatalog _catalog;
    private readonly Dictionary<int, JobDefinition> _jobs;
    private readonly Dictionary<ulong, CoarseRoutineProfile> _profiles = [];
    private int _nextId = 1;

    public CoarseRoutineProfileCache(ContentCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _jobs = catalog.Jobs.ToDictionary(job => job.Hash);
    }

    public int Count => _profiles.Count;

    public CoarseRoutineProfile GetOrCreate(int occupationHash, long traitMask, int commuteMinutes,
        OperativeWorkSchedule? rota = null)
    {
        if (!_jobs.TryGetValue(occupationHash, out var job))
            throw new InvalidOperationException($"Unknown occupation hash '{occupationHash}' cannot receive a coarse profile.");
        if (commuteMinutes < 0) throw new ArgumentOutOfRangeException(nameof(commuteMinutes));
        if (rota is { } schedule)
            job = job with
            {
                WorkDays = Enumerable.Range(1, SimulationDefaults.DaysPerWeek)
                    .Where(day => (schedule.WorkDaysMask & (1 << (day - 1))) != 0).ToList(),
                WorkStartMinute = schedule.WorkStartMinute,
                WorkEndMinute = schedule.WorkEndMinute
            };
        var fingerprint = Fingerprint(occupationHash, traitMask, commuteMinutes, rota);
        if (_profiles.TryGetValue(fingerprint, out var existing)) return existing;
        var profile = new CoarseRoutineProfile(_nextId++, fingerprint, BuildWeek(job, traitMask, commuteMinutes));
        _profiles.Add(fingerprint, profile);
        return profile;
    }

    public bool TryGet(int id, out CoarseRoutineProfile? profile)
    {
        profile = _profiles.Values.FirstOrDefault(candidate => candidate.Id == id);
        return profile is not null;
    }

    private CoarseRoutineInterval[] BuildWeek(JobDefinition job, long traits, int commuteMinutes)
    {
        var result = new List<CoarseRoutineInterval>(32);
        for (var day = 1; day <= SimulationDefaults.DaysPerWeek; day++)
        {
            var template = job.WorkDays.Contains(day)
                ? _catalog.Lod.Tier3Routines.Workday
                : _catalog.Lod.Tier3Routines.NonWorkday;
            BuildDay(result, day - 1, template, job, traits, commuteMinutes);
        }
        return result.ToArray();
    }

    private void BuildDay(List<CoarseRoutineInterval> result, int dayIndex,
        IReadOnlyList<CompiledCoarseRoutineSegment> template, JobDefinition job, long traits, int commuteMinutes)
    {
        var dayStart = dayIndex * SimulationDefaults.SimulationMinutesPerDay;
        var occupied = new List<(int Start, int End, CompiledCoarseRoutineSegment Segment)>();
        foreach (var segment in template)
        {
            if (segment.Kind == CoarseRoutineSegmentKind.JobWork)
                occupied.Add((job.WorkStartMinute, job.WorkEndMinute, segment));
            else if (segment.Kind == CoarseRoutineSegmentKind.CommuteToWork)
                occupied.Add((job.WorkStartMinute - commuteMinutes, job.WorkStartMinute, segment));
            else if (segment.Kind == CoarseRoutineSegmentKind.CommuteHome)
                occupied.Add((job.WorkEndMinute, job.WorkEndMinute + commuteMinutes, segment));
        }
        if (occupied.Any(item => item.Start < 0 || item.End > SimulationDefaults.SimulationMinutesPerDay))
            throw new InvalidDataException($"Tier 3 commute duration {commuteMinutes} cannot fit around job '{job.Id}'.");

        // Authored fixed blocks fill the earliest available minute in stable
        // source order. Fill blocks are emitted only after all reservations.
        foreach (var segment in template.Where(segment => segment.Kind == CoarseRoutineSegmentKind.Fixed && !segment.FillRemaining))
        {
            var duration = segment.FixedMinutes + DurationAdjustment(segment, traits);
            if (duration < 0) throw new InvalidDataException($"Trait adjustments make coarse segment '{segment.Id}' negative.");
            var start = FindFreeRange(occupied, duration);
            occupied.Add((start, start + duration, segment));
        }
        var fill = template.Single(segment => segment.FillRemaining);
        occupied.Sort((left, right) => left.Start.CompareTo(right.Start));
        var cursor = 0;
        foreach (var interval in occupied)
        {
            if (interval.Start < cursor) throw new InvalidDataException("Tier 3 routine segments overlap after job and commute insertion.");
            if (cursor < interval.Start) Add(result, dayStart + cursor, dayStart + interval.Start, fill);
            Add(result, dayStart + interval.Start, dayStart + interval.End, interval.Segment);
            cursor = interval.End;
        }
        if (cursor < SimulationDefaults.SimulationMinutesPerDay)
            Add(result, dayStart + cursor, dayStart + SimulationDefaults.SimulationMinutesPerDay, fill);
    }

    private int DurationAdjustment(CompiledCoarseRoutineSegment segment, long traits) => _catalog.Lod.Tier3Routines.TraitDurationModifiers
        .Where(modifier => modifier.SegmentIndex == segment.TemplateIndex && (traits & modifier.TraitBit) != 0)
        .Sum(modifier => modifier.Minutes);

    private static int FindFreeRange(List<(int Start, int End, CompiledCoarseRoutineSegment Segment)> occupied, int duration)
    {
        var cursor = 0;
        foreach (var interval in occupied.OrderBy(item => item.Start))
        {
            if (interval.Start - cursor >= duration) return cursor;
            cursor = Math.Max(cursor, interval.End);
        }
        if (SimulationDefaults.SimulationMinutesPerDay - cursor >= duration) return cursor;
        throw new InvalidDataException("Tier 3 fixed segments do not fit in a day after job and commute insertion.");
    }

    private static void Add(List<CoarseRoutineInterval> output, int start, int end, CompiledCoarseRoutineSegment segment)
    {
        if (start != end) output.Add(new(start, end, segment.RuntimeIndex, segment.IntentHash, segment.Location, segment.EffectRole));
    }

    private static ulong Fingerprint(int occupation, long traits, int commute, OperativeWorkSchedule? rota)
    {
        // The catalog/topology revisions are represented by this cache's
        // lifetime: replacing either creates a fresh cache with no stale keys.
        var value = 14695981039346656037UL;
        var schedule = rota.GetValueOrDefault();
        foreach (var part in new[] { occupation, (int)traits, (int)(traits >> 32), commute,
                     schedule.WorkDaysMask, schedule.WorkStartMinute, schedule.WorkEndMinute })
        { value ^= unchecked((uint)part); value *= 1099511628211UL; }
        return value;
    }
}
