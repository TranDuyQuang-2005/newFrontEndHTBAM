using HTBAM.Domain.Entities;

namespace HTBAM.Application.Services;

public sealed record StoredBehaviorSummary(
    int ObservedIdentityCount,
    int ObservedSeconds,
    decimal FocusedRatio,
    decimal DistractedRatio,
    decimal SleepyRatio,
    decimal ActiveRatio,
    decimal ObservationQuality);

public static class BehaviorSummaryCalculator
{
    private static readonly string[] StateLabels = ["FOCUSED", "DISTRACTED", "SLEEPY", "ACTIVE"];

    public static StoredBehaviorSummary Calculate(IEnumerable<BehaviorEvent> source, DateTime rangeStart, DateTime rangeEnd)
    {
        if (rangeEnd <= rangeStart) return Empty();
        var events = source.Where(IsValidState).ToList();
        if (events.Count == 0) return Empty();

        var observedSeconds = 0;
        var observedIdentities = 0;
        var labelSeconds = StateLabels.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        decimal qualityNumerator = 0;
        decimal qualityDenominator = 0;

        foreach (var identityEvents in events.GroupBy(x => x.StableIdentityId))
        {
            var observed = UnionSeconds(identityEvents.Select(x => (x.StartedAt, x.EndedAt)), rangeStart, rangeEnd);
            if (observed <= 0) continue;
            observedIdentities++;
            observedSeconds += observed;
            foreach (var label in StateLabels)
                labelSeconds[label] += UnionSeconds(identityEvents.Where(x => x.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
                    .Select(x => (x.StartedAt, x.EndedAt)), rangeStart, rangeEnd);

            foreach (var item in identityEvents)
            {
                var seconds = ClampedSeconds(item.StartedAt, item.EndedAt, rangeStart, rangeEnd);
                qualityNumerator += item.ObservationQuality * seconds;
                qualityDenominator += seconds;
            }
        }

        if (observedSeconds <= 0) return Empty();
        decimal Ratio(string label) => Math.Clamp((decimal)labelSeconds[label] / observedSeconds, 0m, 1m);
        return new StoredBehaviorSummary(observedIdentities, observedSeconds,
            Ratio("FOCUSED"), Ratio("DISTRACTED"), Ratio("SLEEPY"), Ratio("ACTIVE"),
            qualityDenominator <= 0 ? 0 : Math.Clamp(qualityNumerator / qualityDenominator, 0m, 1m));
    }

    public static bool IsValidState(BehaviorEvent item) =>
        item.EndedAt > item.StartedAt && StateLabels.Contains(item.Label, StringComparer.OrdinalIgnoreCase);

    public static int UnionSeconds(IEnumerable<(DateTime Start, DateTime End)> source, DateTime rangeStart, DateTime rangeEnd)
    {
        var intervals = source.Select(x =>
            (Start: x.Start < rangeStart ? rangeStart : x.Start, End: x.End > rangeEnd ? rangeEnd : x.End))
            .Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
        if (intervals.Count == 0) return 0;
        var total = TimeSpan.Zero;
        var start = intervals[0].Start;
        var end = intervals[0].End;
        foreach (var item in intervals.Skip(1))
        {
            if (item.Start <= end)
            {
                if (item.End > end) end = item.End;
            }
            else
            {
                total += end - start;
                start = item.Start;
                end = item.End;
            }
        }
        total += end - start;
        return Math.Max(0, (int)Math.Round(total.TotalSeconds));
    }

    private static decimal ClampedSeconds(DateTime start, DateTime end, DateTime rangeStart, DateTime rangeEnd)
    {
        if (start < rangeStart) start = rangeStart;
        if (end > rangeEnd) end = rangeEnd;
        return Math.Max(0, (decimal)(end - start).TotalSeconds);
    }

    private static StoredBehaviorSummary Empty() => new(0, 0, 0, 0, 0, 0, 0);
}
