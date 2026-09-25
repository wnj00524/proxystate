using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace ProxyState.Simulation;

public sealed record DailyFactionDiagnostic(int Day, FactionSnapshot Faction, string MetaGoalName,
    string? ActiveGoalName, IReadOnlyList<string> CompletedGoals);
public sealed record PoliticalOfficeholder(string OfficeId, string OfficeName, int AgentId,
    byte FactionId, int? MemberFactionId, string? MemberRole);
public sealed record PoliticalHeadlessReport(int SchemaVersion, int Seed, int AgentCount,
    int DaysSimulated, long SimulatedMinutes, IReadOnlyList<DailyFactionDiagnostic> DailyFactionSnapshots,
    IReadOnlyList<ElectionDiagnosticResult> Elections, IReadOnlyList<PoliticalOfficeholder> Officeholders,
    IReadOnlyList<PoliticalDiagnosticEvent> Events);

/// <summary>Runs the normal ECS systems on deterministic minute ticks without creating a graphics window.</summary>
public static class HeadlessSimulationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PoliticalHeadlessReport Run(ContentCatalog catalog, int agentCount, int days, int seed,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (agentCount is < 1 or > ApplicationOptions.MaximumAgentCount)
            throw new ArgumentOutOfRangeException(nameof(agentCount));
        if (days is < 1 or > ApplicationOptions.MaximumDays)
            throw new ArgumentOutOfRangeException(nameof(days));

        var stopwatch = Stopwatch.StartNew();
        progress?.Invoke($"Headless political diagnostics started: agents={agentCount:N0}, days={days:N0}, seed={seed}.");
        var store = new EntityStore();
        var spawner = new AgentSpawner(catalog);
        spawner.Spawn(store, agentCount, seed);
        var lod = spawner.LodService ?? throw new InvalidOperationException("Agent LOD service was not initialized.");
        var indexes = spawner.Indexes;
        var clock = new WorldClockSystem(store);
        var systems = new SystemRoot(store)
        {
            clock,
            new AgentDecisionSystem(store, catalog, clock.ClockEntity, socialIndexes: indexes, lodService: lod),
            new CoordinationSystem(store, catalog, clock.ClockEntity, indexes, lod),
            new IntentExecutionSystem(store, catalog, clock.ClockEntity, indexes),
            new ActivityEffectsSystem(catalog, clock.ClockEntity),
            new InteractionSystem(store, catalog, SimulationRandomStreams.Interactions(seed), socialIndexes: indexes)
        };
        var politics = new PoliticalSystem(store, catalog, clock.ClockEntity, indexes, lod);
        var factions = new PoliticalFactionSystem(store, catalog, indexes, lod);
        var research = new PoliticalResearchSystem(store, catalog, clock.ClockEntity, seed);
        var daily = new List<DailyFactionDiagnostic>(days * catalog.Factions.Count);
        var chronologicalEvents = new List<PoliticalDiagnosticEvent>();
        var recordedPoliticalEvents = 0;
        var recordedFactionEvents = 0;
        var secondsPerSimulationMinute = SimulationDefaults.RealSecondsPerSimulationDay /
                                         SimulationDefaults.SimulationMinutesPerDay;
        var totalMinutes = (long)days * SimulationDefaults.SimulationMinutesPerDay;

        for (long elapsedMinute = 1; elapsedMinute <= totalMinutes; elapsedMinute++)
        {
            var dayAtStart = (int)((elapsedMinute - 1) / SimulationDefaults.SimulationMinutesPerDay) + 1;
            if ((elapsedMinute - 1) % SimulationDefaults.SimulationMinutesPerDay == 0)
                progress?.Invoke($"Starting day {dayAtStart:N0}/{days:N0} (elapsed {FormatDuration(stopwatch.Elapsed)}).");
            clock.Advance(secondsPerSimulationMinute);
            lod.UpdateCoarse(elapsedMinute);
            systems.Update(default);
            politics.Update();
            while (recordedPoliticalEvents < politics.Events.Count)
                chronologicalEvents.Add(politics.Events[recordedPoliticalEvents++]);
            var dayNumber = (int)((elapsedMinute - 1) / SimulationDefaults.SimulationMinutesPerDay) + 1;
            factions.Update(dayNumber);
            research.Update();
            while (recordedFactionEvents < factions.Events.Count)
                chronologicalEvents.Add(factions.Events[recordedFactionEvents++]);
            if (elapsedMinute % SimulationDefaults.SimulationMinutesPerDay == 0)
            {
                var definitions = catalog.Factions.ToDictionary(faction => faction.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var snapshot in factions.Snapshots)
                {
                    var definition = definitions[snapshot.Id];
                    var goalName = definition.Goals?.FirstOrDefault(goal =>
                        string.Equals(goal.Id, snapshot.ActiveGoalId, StringComparison.OrdinalIgnoreCase))?.Name;
                    var completedNames = definition.Goals?.Where(goal => snapshot.CompletedGoals.Contains(goal.Id))
                        .Select(goal => goal.Name).Order(StringComparer.Ordinal).ToArray() ?? [];
                    var stableSnapshot = snapshot with
                    {
                        CompletedGoals = new SortedSet<string>(snapshot.CompletedGoals, StringComparer.Ordinal)
                    };
                    daily.Add(new DailyFactionDiagnostic(dayNumber, stableSnapshot, definition.MetaGoal!.Name,
                        goalName, completedNames));
                }
                var remainingDays = days - dayNumber;
                var estimatedRemaining = TimeSpan.FromSeconds(stopwatch.Elapsed.TotalSeconds / dayNumber * remainingDays);
                progress?.Invoke($"Completed day {dayNumber:N0}/{days:N0} (elapsed {FormatDuration(stopwatch.Elapsed)}, estimated remaining {FormatDuration(estimatedRemaining)}).");
            }
        }

        var jobByHash = catalog.Jobs.ToDictionary(job => job.Hash);
        var officeholders = store.Query<Identity, PoliticalAlignment>().Entities
            .Where(agent => jobByHash.TryGetValue(agent.GetComponent<Identity>().OccupationId, out var job) &&
                            job.SelectionMethod is not null)
            .OrderBy(agent => jobByHash[agent.GetComponent<Identity>().OccupationId].Id, StringComparer.Ordinal)
            .ThenBy(agent => agent.Id)
            .Select(agent =>
            {
                var identity = agent.GetComponent<Identity>();
                var alignment = agent.GetComponent<PoliticalAlignment>();
                var memberFaction = agent.TryGetComponent<FactionParticipation>(out var member) &&
                                    member.Role != FactionMemberRole.None ? (int?)member.FactionId : null;
                var memberRole = memberFaction is null ? null : member.Role.ToString();
                var job = jobByHash[identity.OccupationId];
                return new PoliticalOfficeholder(job.Id, job.Name, agent.Id, alignment.FactionId,
                    memberFaction, memberRole);
            }).ToArray();
        // Events are copied after each minute's political and faction updates,
        // retaining actual tick order rather than alphabetizing timestamp ties.
        var events = chronologicalEvents.ToArray();
        return new PoliticalHeadlessReport(1, seed, agentCount, days, totalMinutes, daily,
            politics.ElectionResults.ToArray(), officeholders, events);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)duration.TotalHours;
        return totalHours >= 24
            ? $"{totalHours / 24}.{totalHours % 24:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{totalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    public static (string MarkdownPath, string JsonPath) WriteReports(PoliticalHeadlessReport report,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Report prefix cannot be empty.", nameof(prefix));
        var fullPrefix = Path.GetFullPath(prefix);
        var directory = Path.GetDirectoryName(fullPrefix);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var markdownPath = fullPrefix + ".md";
        var jsonPath = fullPrefix + ".json";
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(markdownPath, RenderMarkdown(report), new UTF8Encoding(false));
        return (markdownPath, jsonPath);
    }

    public static string RenderMarkdown(PoliticalHeadlessReport report)
    {
        var output = new StringBuilder();
        output.AppendLine("# Political Diagnostics");
        output.AppendLine();
        output.AppendLine($"- Schema: {report.SchemaVersion}  ");
        output.AppendLine($"- Seed: {report.Seed}  ");
        output.AppendLine($"- Agents: {report.AgentCount}  ");
        output.AppendLine($"- Duration: {report.DaysSimulated} days ({report.SimulatedMinutes:N0} simulated minutes)  ");
        output.AppendLine($"- Political events: {report.Events.Count}  ");
        output.AppendLine();
        output.AppendLine("## Current political landscape");
        output.AppendLine();
        output.AppendLine("| Office | Holder | Alignment | Membership |");
        output.AppendLine("| --- | ---: | --- | --- |");
        foreach (var office in report.Officeholders)
            output.AppendLine($"| {office.OfficeName} (`{office.OfficeId}`) | Agent {office.AgentId} | Faction {office.FactionId} | {office.MemberRole ?? "none"}{(office.MemberFactionId is null ? "" : $" (Faction {office.MemberFactionId})")} |");
        if (report.Officeholders.Count == 0) output.AppendLine("| No elected or appointed offices are currently filled | | | |");
        output.AppendLine();
        output.AppendLine("## Daily faction progress");
        output.AppendLine();
        output.AppendLine("| Day | Faction | Members (volunteers / activists / leader) | Organization | Support | Institutional control | Active goal | Completed goals |");
        output.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (var day in report.DailyFactionSnapshots)
        {
            var f = day.Faction;
            output.AppendLine($"| {day.Day} | {f.Id} | {f.Members} ({f.Volunteers} / {f.Activists} / {f.Leaders}) | {f.Organization:F1} | {f.PublicSupport:F1} | {f.InstitutionalControl} | {day.ActiveGoalName ?? "—"} (`{f.ActiveGoalId ?? "—"}`) | {string.Join(", ", day.CompletedGoals)} |");
        }
        output.AppendLine();
        output.AppendLine("## Election outcomes");
        output.AppendLine();
        if (report.Elections.Count == 0) output.AppendLine("No election concluded during this run.");
        foreach (var election in report.Elections)
        {
            output.AppendLine($"### Cycle {election.ElectionId} (day {election.Day})");
            output.AppendLine();
            output.AppendLine($"Candidates: {election.CandidateCount}; voters who cast at least one ballot: {election.VoterCount}.");
            output.AppendLine();
            output.AppendLine("| Office | Winner | Faction | Votes | Candidate tallies |");
            output.AppendLine("| --- | ---: | --- | ---: | --- |");
            foreach (var office in election.Offices)
                output.AppendLine($"| {office.OfficeId} | {(office.WinnerAgentId is null ? "Vacant" : $"Agent {office.WinnerAgentId}")} | {office.WinnerFactionId?.ToString() ?? "—"} | {office.VoteCount} | {string.Join("; ", office.Tallies.Select(tally => $"Agent {tally.AgentId} (Faction {tally.FactionId}): {tally.Votes}"))} |");
            output.AppendLine();
        }
        output.AppendLine("## Political event log");
        output.AppendLine();
        output.AppendLine("| Day | Time | Event | Agent | Faction | Office | Cycle | Other agent | Details |");
        output.AppendLine("| ---: | ---: | --- | ---: | ---: | --- | ---: | ---: | --- |");
        foreach (var entry in report.Events)
            output.AppendLine($"| {entry.Day} | {entry.MinuteOfDay / 60:D2}:{entry.MinuteOfDay % 60:D2} | {entry.Type} | {entry.AgentId?.ToString() ?? "—"} | {entry.FactionId?.ToString() ?? "—"} | {entry.OfficeId ?? "—"} | {entry.ElectionId?.ToString() ?? "—"} | {entry.OtherAgentId?.ToString() ?? "—"} | {(entry.Detail ?? "").Replace("|", "\\|")} |");
        return output.ToString();
    }
}
