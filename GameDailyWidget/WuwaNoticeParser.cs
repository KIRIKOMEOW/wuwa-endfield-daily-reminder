using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameDailyWidget;

internal static class WuwaNoticeParser
{
    private static readonly Regex Title = new(@"[\[【「《](?<name>[^\]】」》]+)[\]】」》]");
    private static readonly Regex Rated = new(@"(?<stars>[45])\s*星\s*(?<kind>角色|共鸣者|武器)\s*(?<names>「[^」]+」(?:\s*、\s*「[^」]+」)*)");

    internal static GamePool Parse(WuwaNotice notice, List<Maintenance> maintenance, DateTimeOffset now, Uri source)
    {
        var name = Title.Match(notice.Title).Groups["name"].Value.Trim();
        var kind = notice.Title.Contains("忆旅") ? "忆旅" : notice.Title.Contains("武器") ? "武器" : "角色";
        if (name.Length == 0) throw new InvalidDataException("无法识别唤取标题");
        var lines = EventParser.Lines(notice.Content);
        var window = PoolParser.TimeIn(lines);
        // Normalize ISO dates before splitting ranges that may themselves use a hyphen.
        window = Regex.Replace(window, @"(\d{4})-(\d{1,2})-(\d{1,2})", "$1/$2/$3");
        var version = Regex.Match(window, @"(\d+\.\d+)\s*版本").Groups[1].Value;
        var bounds = Regex.Split(window, @"[~～至—–-]");
        var first = EventParser.ReadDate(bounds[0]);
        var rawEnd = bounds.Length == 2 ? EventParser.ReadDate(bounds[1]) : first;
        var start = bounds.Length == 2 ? first : null;
        var metadata = !rawEnd.HasValue;
        var afterUpdate = window.Contains("版本更新后") || metadata;
        if (version.Length == 0)
            version = maintenance.Where(m => m.End <= (start ?? now)).MaxBy(m => m.End)?.Version ?? "";
        var origin = maintenance.FirstOrDefault(m => m.Version == version);
        if (metadata)
        {
            rawEnd = notice.DisplayEnd.AddTicks(-(notice.DisplayEnd.Ticks % TimeSpan.TicksPerMinute));
            start = notice.DisplayStart;
        }
        if (afterUpdate && origin != null && (start == null || origin.End > start)) start = origin.End;
        var inclusive = rawEnd!.Value.Minute == 59;
        var end = inclusive ? rawEnd.Value.AddMinutes(1) : rawEnd.Value;
        if (end <= start) throw new InvalidDataException("唤取时间段无效");

        var items = new List<PoolItem>();
        foreach (var clause in Regex.Split(string.Join(" ",lines), "[。！!；;]"))
        {
            var up = Regex.Match(clause, @"唤取概率(?:限时)?提升|获取概率(?:大幅)?提升");
            if (!up.Success) continue;
            foreach (Match rated in Rated.Matches(clause[..up.Index]))
                foreach (Match item in Regex.Matches(rated.Groups["names"].Value, "「([^」]+)」"))
                    items.Add(new(item.Groups[1].Value.Trim(),int.Parse(rated.Groups["stars"].Value),rated.Groups["kind"].Value));
        }
        return new($"wuwa|{kind}|{EventParser.Normalize(name)}|{version}","wuwa",name,kind,start,end,start == null,
            items.Distinct().ToList(),new(notice.Id,notice.Title,source.AbsoluteUri),version,notice.DisplayStart,
            StartsAfterMaintenance:afterUpdate,NoticeTime:metadata,InclusiveEndMinute:inclusive);
    }
}
