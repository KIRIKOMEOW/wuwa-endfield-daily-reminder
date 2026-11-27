using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameDailyWidget;

internal static class PoolParser
{
    private const string Quoted = @"[「\[【](?<name>[^」\]】]+)[」\]】]";
    private const string Excluded = "常驻|新手|基础寻访|限时特卖|礼包|商城|组合包";
    private static readonly Regex EndfieldBanner = new(Quoted + @"\s*(?<kind>特许寻访|重构寻访|重构申领)\d*");
    private static readonly Regex WuwaBanner = new(Quoted + @"\s*(?<kind>角色|武器)(?:活动|复刻)?唤取");
    private static readonly Regex WuwaItem = new(@"(?<stars>[1-5])\s*星(?:共鸣者|角色|武器)\s*" + Quoted + @"(?:[（(](?<type>[^）)]+)[）)])?");
    private static readonly Regex EndfieldItem = new(@"(?<stars>[1-6])\s*星(?:干员|武器)(?:为|有|包括)?\s*(?<names>【[^】]+】(?:\s*[、/和及]\s*【[^】]+】)*)");
    private static readonly Regex TimeLabel = new(@"^(?:[▼/✦·■※\s]*)?(?:开放|活动|唤取|寻访|申领)时间\s*[：:]?\s*");

    internal static bool IsAnnouncement(string game, string title) =>
        !Regex.IsMatch(title, Excluded) && (game == "wuwa" ? title.Contains("唤取") : EndfieldBanner.IsMatch(title));

    private static readonly Regex EndLabel = new(@"^[▼/✦·■※\s]*(?:活动|唤取|寻访|申领)?(?:结束时间|截止时间)\s*[：:]?\s*");

    internal static string TimeIn(IEnumerable<string> lines)
    {
        var body = lines.ToArray();
        string Value(Regex label)
        {
            for (var i = 0; i < body.Length; i++)
            {
                var match = label.Match(body[i]);
                if (!match.Success) continue;
                var value = body[i][match.Length..].TrimStart('✦','：',':',' ').Trim();
                return value.Length == 0 ? body.ElementAtOrDefault(i + 1) ?? "" : value;
            }
            return "";
        }
        var window = Value(TimeLabel);
        var end = Value(EndLabel);
        return end.Length == 0 ? window : window + " ~ " + end;
    }

    // Only dates bound to the pool itself supply a start/end; publication is never a fallback.
    private static GamePool Pool(EventArticle article, string name, string kind, IEnumerable<PoolItem> items, string window)
    {
        var version = EventParser.VersionOf(article);
        if (version.Length == 0)
            version = Regex.Match(window, article.Game == "endfield" ? @"「([^」]+)」版本" : @"(\d+\.\d+)\s*版本").Groups[1].Value;
        var dates = Regex.Replace(window, @"「[^」]+」(?=版本)", "");
        var bounds = Regex.Split(dates, @"[~～至—-]");
        var start = EventParser.ReadDate(bounds[0]);
        var end = bounds.Length == 2 ? EventParser.ReadDate(bounds[1], true) : null;
        if (end <= start) end = null;
        return new($"{article.Game}|{kind}|{EventParser.Normalize(name)}|{version}", article.Game, name, kind, start, end,
            start == null || end == null, items.Distinct().ToList(), article.Source, version, article.Published,
            bounds[0].Contains("版本更新后") || bounds[0].Contains("版本期间"),
            window.Contains("维护前") || window.Contains("版本期间"));
    }

    internal static List<GamePool> Parse(EventArticle article, string[] lines)
    {
        if (Regex.IsMatch(article.Source.Title, Excluded)) return [];
        return article.Game == "wuwa" ? ReadWuwa(article, lines) : ReadEndfield(article, lines);
    }

    private static List<GamePool> ReadEndfield(EventArticle article, string[] lines)
    {
        var results = new List<GamePool>();
        var headings = Enumerable.Range(0, lines.Length)
            .Where(i => Regex.IsMatch(lines[i], @"^(?:\d+[.．、]|▼//)\s*「")).ToArray();
        // Combined reconstruction announcements specify one window before both named sections.
        var sharedWindow = TimeIn(lines.Take(headings.FirstOrDefault(lines.Length)));
        foreach (var (index, position) in headings.Select((index, position) => (index, position)))
        {
            var heading = lines[index];
            var banner = EndfieldBanner.Match(heading);
            if (!banner.Success || Regex.IsMatch(heading, Excluded)) continue;
            var name = banner.Groups["name"].Value;
            var kind = banner.Groups["kind"].Value.Contains("寻访") ? "干员" : "武器";
            var block = lines[(index + 1)..(position + 1 < headings.Length ? headings[position + 1] : lines.Length)];
            var items = new List<PoolItem>();
            foreach (var line in block)
            {
                var other = EndfieldBanner.Match(line);
                if (other.Success && other.Groups["name"].Value != name) continue;
                foreach (var clause in Regex.Split(line, "[，,。；;！!]"))
                {
                    if (!clause.Contains("概率提升") || clause.Contains("全部可能")) continue;
                    foreach (Match item in EndfieldItem.Matches(clause))
                        foreach (Match entry in Regex.Matches(item.Groups["names"].Value, @"【([^】]+)】"))
                        {
                            var parts = Regex.Match(entry.Groups[1].Value, @"^([^（(]+)(?:[（(]([^）)]+)[）)])?$");
                            if (parts.Success)
                                items.Add(new(parts.Groups[1].Value.Trim(), int.Parse(item.Groups["stars"].Value),
                                    parts.Groups[2].Success ? parts.Groups[2].Value : kind));
                        }
                }
            }
            var time = TimeIn(block);
            if (items.Count > 0) results.Add(Pool(article, name, kind, items, time.Length > 0 ? time : sharedWindow));
        }
        return results;
    }

    private static PoolItem WuwaEntry(Match item, string kind)
    {
        var type = item.Groups["type"].Value.Trim();
        return new(item.Groups["name"].Value, int.Parse(item.Groups["stars"].Value), kind == "武器" && type.Length > 0 ? type : kind);
    }

    internal static List<string> NewWuwaItems(string[] lines)
    {
        var items = new List<string>();
        var active = false;
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line,"^✦[^✦]+✦$") || line.StartsWith('【'))
                active = line is "✦全新角色✦" or "✦全新共鸣者✦" or "✦全新武器✦";
            if (active && WuwaItem.Match(line) is { Success:true, Index:0 } item && item.Groups["stars"].Value == "5")
                items.Add(item.Groups["name"].Value);
        }
        return items;
    }

    private static List<GamePool> ReadWuwa(EventArticle article, string[] lines)
    {
        var results = new List<GamePool>();
        string? section = null;
        bool newSection = false;
        PoolItem? entry = null;
        var itemStart = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, "^✦[^✦]+✦$") || line.StartsWith('【'))
            {
                section = Regex.IsMatch(line, "^✦(?:全新|复刻)(?:共鸣者|角色)✦$") ? "角色" :
                    Regex.IsMatch(line, "^✦(?:全新|复刻)武器✦$") ? "武器" : null;
                newSection = line.StartsWith("✦全新") && article.Source.Title.Contains("版本内容说明");
                entry = null;
            }
            if (section == null) continue;
            var item = WuwaItem.Match(line);
            if (item.Success && item.Index == 0) { entry = WuwaEntry(item, section); itemStart = i; }
            if (entry == null || !line.Contains("可通过") || Regex.IsMatch(line, Excluded)) continue;
            var banner = WuwaBanner.Match(line);
            if (!banner.Success || banner.Groups["kind"].Value != section) continue;
            var following = lines.Skip(i + 1).TakeWhile(l => !Regex.IsMatch(l, @"^(?:✦[^✦]+✦|【|[1-5]\s*星|※可通过)"));
            results.Add(Pool(article, banner.Groups["name"].Value, section, [entry], TimeIn(lines[itemStart..i].Concat(following))) with { NewUp=newSection && entry.Stars==5 });
            entry = null;
        }
        // Dedicated banner/re-run notices can also supplement a version note that omitted its schedule.
        var sections = new List<(int Index,Match Banner)>();
        var titleBanner = WuwaBanner.Match(article.Source.Title);
        if (titleBanner.Success) sections.Add((-1,titleBanner));
        for (var i = 0; i < lines.Length; i++)
        {
            var banner = WuwaBanner.Match(lines[i]);
            if (banner.Success && Regex.IsMatch(lines[i], @"^(?:[✦●■\s]*|▼//)\s*[[「【]") &&
                !Regex.IsMatch(lines[i], "[，。！]|" + Excluded)) sections.Add((i,banner));
        }
        for (var i = 0; i < sections.Count; i++)
        {
            var (index,banner) = sections[i];
            var end = i + 1 < sections.Count ? sections[i + 1].Index : lines.Length;
            var block = lines[(index + 1)..end].TakeWhile(l => !Regex.IsMatch(l, @"^✦[^✦]+✦$") &&
                !Regex.IsMatch(l, @"^(?:[✦■●\s]*|▼//)[\[「【].*(?:常驻|新手|商城|礼包)")).ToArray();
            var kind = banner.Groups["kind"].Value;
            var items = block.Where(l => Regex.IsMatch(l, "概率提升|概率UP|获取概率大幅提升") && !l.Contains("可能出现"))
                .SelectMany(l => WuwaItem.Matches(l).Cast<Match>()).Select(m => WuwaEntry(m,kind)).ToList();
            var time = TimeIn(block);
            if (items.Count > 0 || time.Length > 0)
                results.Add(Pool(article,banner.Groups["name"].Value,kind,items,time));
        }
        return results;
    }
}
