using ImGuiNET;
using ProxyState.Simulation;

namespace ProxyState;

/// <summary>
/// Displays only copied, aggregate poll projections. It deliberately has no
/// EntityStore reference, preserving the UI's intelligence-isolation boundary.
/// </summary>
public static class PoliticalResearchWindow
{
    public static void Draw(ResearchProviderProjection provider, string title, ref bool open)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!ImGui.Begin(title, ref open))
        {
            ImGui.End();
            return;
        }

        ImGui.Text(provider.Name);
        ImGui.TextWrapped(provider.Methodology);
        ImGui.Separator();
        ImGui.Text($"Method: {provider.SamplingMethod} | Published basis: {(provider.LikelyVoterOnly ? "likely voters" : "all respondents")}");
        if (provider.Latest is not { } latest)
        {
            ImGui.TextDisabled("No poll has completed yet. Fieldwork is scheduled weekly.");
            ImGui.End();
            return;
        }

        ImGui.Text($"Wave {latest.Wave} | fieldwork day {latest.StartDay} to {latest.PublishedDay}");
        ImGui.Text($"Calls attempted {latest.CallsAttempted} | interviews {latest.CompletedInterviews} | nonresponse {latest.Nonresponse} | effective n {latest.EffectiveSampleSize:0.0}");
        ImGui.TextDisabled("Nonresponse combines no answer and declined interviews.");
        ImGui.Separator();
        ImGui.Text("Party support");
        foreach (var estimate in latest.PartySupport)
            ImGui.Text($"{estimate.Label,-25} {estimate.WeightedPercentage,5:0.0}% (raw {estimate.RawPercentage:0.0}%, 95% {estimate.Lower95:0.0}–{estimate.Upper95:0.0})");
        if (latest.CandidateSupport.Count > 0)
        {
            ImGui.Separator();
            ImGui.Text("Declared candidate preference");
            foreach (var race in latest.CandidateSupport)
            foreach (var candidate in race.Candidates)
                ImGui.Text($"{candidate.Label,-25} {candidate.WeightedPercentage,5:0.0}% (raw {candidate.RawPercentage:0.0}%, 95% {candidate.Lower95:0.0}–{candidate.Upper95:0.0})");
        }

        ImGui.Separator();
        ImGui.Text("Party support trend (weighted %, 0–100)");
        foreach (var category in latest.PartySupport)
        {
            var values = provider.History.Select(snapshot => snapshot.PartySupport
                    .FirstOrDefault(item => item.Id == category.Id)?.WeightedPercentage ?? 0f)
                .ToArray();
            if (values.Length > 0)
                ImGui.PlotLines(category.Label, ref values[0], values.Length, 0, null, 0f, 100f, new System.Numerics.Vector2(0f, 48f));
        }
        var candidateSeries = latest.CandidateSupport.FirstOrDefault();
        if (candidateSeries is not null)
        {
            ImGui.Text("Candidate support trend (weighted %, 0–100)");
            foreach (var category in candidateSeries.Candidates)
            {
                var values = provider.History.Select(snapshot => snapshot.CandidateSupport
                        .FirstOrDefault(race => race.OfficeId == candidateSeries.OfficeId)?.Candidates
                        .FirstOrDefault(item => item.Id == category.Id)?.WeightedPercentage ?? 0f)
                    .ToArray();
                if (values.Length > 0)
                    ImGui.PlotLines(category.Label, ref values[0], values.Length, 0, null, 0f, 100f, new System.Numerics.Vector2(0f, 48f));
            }
        }
        ImGui.End();
    }
}
