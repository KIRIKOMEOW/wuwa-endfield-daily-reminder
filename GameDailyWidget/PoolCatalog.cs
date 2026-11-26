using System;
using System.Collections.Generic;
using System.Linq;

namespace GameDailyWidget;

internal static class PoolCatalog
{
    internal static IEnumerable<GamePool> Current(EventData data, string game, DateTimeOffset now)
    {
        var maintenance = data.Maintenance.GetValueOrDefault(game) ?? [];
        var activeVersion = maintenance.Where(m => m.End <= now).MaxBy(m => m.End);
        return data.Pools.Where(p => p.Game == game && (p.End == null || p.End > now))
            .Where(p => p.End.HasValue || p.Version.Length > 0 || p.Published >= now.AddDays(-60))
            .Where(p => p.End.HasValue || activeVersion == null || !maintenance.Any(m =>
                m.Version == p.Version && m.End < activeVersion.End))
            .OrderBy(p => p.Start > now).ThenBy(p => p.End ?? DateTimeOffset.MaxValue)
            .ThenBy(p => p.Name, StringComparer.Ordinal);
    }

    internal static void Rebuild(EventData data, string game, List<Maintenance> maintenance)
    {
        var fragments = data.Articles.Where(kv => kv.Key.StartsWith(game + ":", StringComparison.Ordinal))
            .SelectMany(kv => kv.Value.ParsedPools ?? []).ToList();
        var result = new List<GamePool>();
        foreach (var fragment in fragments.OrderBy(f => f.Published))
        {
            var version = fragment.Version;
            if (version.Length == 0)
            {
                // A stand-alone notice often omits its version. An unambiguous named banner
                // with overlapping known dates is stronger evidence than the publication date.
                var related = fragments.Where(p => p.Version.Length > 0 && p.Kind == fragment.Kind &&
                    EventParser.Normalize(p.Name) == EventParser.Normalize(fragment.Name) &&
                    (fragment.Start == null || p.End == null || fragment.Start < p.End) &&
                    (fragment.End == null || p.Start == null || fragment.End > p.Start) &&
                    Math.Abs((fragment.Published - p.Published).TotalDays) <= 60)
                    .Select(p => p.Version).Distinct().ToList();
                version = related.Count == 1 ? related[0] :
                    maintenance.Where(m => m.End <= (fragment.Start ?? fragment.Published)).MaxBy(m => m.End)?.Version ?? "";
            }
            var origin = maintenance.FirstOrDefault(m => m.Version == version);
            var start = fragment.Start ?? (fragment.StartsAfterMaintenance ? origin?.End : null);
            var end = fragment.End;
            if (end == null && fragment.EndsBeforeMaintenance && origin != null)
                end = maintenance.Where(m => m.End > origin.End && m.Start > (start ?? origin.End)).MinBy(m => m.Start)?.Start;
            var key = $"{game}|{fragment.Kind}|{EventParser.Normalize(fragment.Name)}|{version}";
            // Unknown-version standalone notices separated by months may be independent re-runs.
            if (version.Length == 0) key += fragment.Source.Id;
            var item = fragment with { Key=key, Version=version, Start=start, End=end, TimePending=start == null || end == null };
            var index = result.FindIndex(p => p.Key == key);
            if (index < 0) { result.Add(item); continue; }
            var old = result[index];
            start = item.Start ?? old.Start;
            end = item.End ?? old.End;
            var source = PoolParser.IsAnnouncement(game, old.Source.Title) && !PoolParser.IsAnnouncement(game,item.Source.Title)
                ? old.Source : item.Source;
            result[index] = item with { Start=start, End=end, TimePending=start == null || end == null, Source=source,
                Items=old.Items.Concat(item.Items).DistinctBy(i => (i.Name,i.Stars)).ToList() };
        }
        data.Pools.RemoveAll(p => p.Game == game);
        data.Pools.AddRange(result.Where(p => p.Items.Count > 0));
    }
}
