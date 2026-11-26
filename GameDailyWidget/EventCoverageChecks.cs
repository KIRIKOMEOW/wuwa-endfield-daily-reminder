using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameDailyWidget;

internal static partial class EventChecks
{
    // Independent of the production heading and time-label recognizers. A new
    // dated paragraph must be reviewed even if the production parser ignores it.
    private static void CheckCoverage(string id, string[] lines, List<GameEvent> parsed)
    {
        using var stream = typeof(EventChecks).Assembly.GetManifestResourceStream("events.coverage.tsv")!;
        using var reader = new StreamReader(stream);
        var reviewed = reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(l => l.TrimEnd('\r').Split('\t')).Where(row => row[0] == id)
            .ToDictionary(row => int.Parse(row[1],CultureInfo.InvariantCulture));
        var matched = new HashSet<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"\d{1,2}[:：]\d{2}|\d{1,2}月\d{1,2}日|\d{4}/\d{1,2}/\d{1,2}|版本期间|维护前|(?:活动时间|开放时间)[：:].+")) continue;
            if (!reviewed.Remove(i+1,out var row)) throw new InvalidDataException($"{id}:{i+1} 新时间段缺少收录/排除核对");
            if (row[2].Length == 0)
            {
                if (!row[6].StartsWith("排除 · ")) throw new InvalidDataException($"{id}:{i+1} 缺少排除理由");
                continue;
            }
            string Time(DateTimeOffset value) => value.ToOffset(EventParser.China).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
            var item = parsed.SingleOrDefault(e => e.Name == row[2] && Time(e.Start) == row[3]);
            if (item == null || (item.End is {} end ? Time(end) : item.EndPending ? "待公告" : "") != row[4] ||
                item.Currency.Amount?.ToString(CultureInfo.InvariantCulture) != (row[5].Length == 0 ? null : row[5]) ||
                !matched.Add(item.Key)) throw new InvalidDataException($"{id}:{i+1} 未按原文收录 {row[2]}");
        }
        if (reviewed.Count != 0 || parsed.Count != matched.Count)
            throw new InvalidDataException($"{id} 核对行与实际活动集合不符");
    }
}
