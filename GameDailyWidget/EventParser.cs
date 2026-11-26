using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace GameDailyWidget;

internal static class EventParser
{
    internal const int Revision = 3;
    internal static readonly TimeSpan China = TimeSpan.FromHours(8);
    private const string DatePattern = @"(?<y>\d{4})[年/](?<m>\d{1,2})[月/](?<d>\d{1,2})日?\s*(?<h>\d{1,2})[:：](?<min>\d{2})";
    private const string Excluded = "唤取|寻访|申领|限时特卖|永久开放|常驻|商城|采购中心|组合包|补偿|网页活动|平台专属";
    private static readonly Regex Dates = new(DatePattern);
    private static readonly Regex Heading = new(@"^(?:\d+[.、．]\s*「(?<name>[^」]+)」|\[(?<name>[^\]]+)\]|「(?<name>[^」]+)」)\s*(?<kind>[^。]{0,70})$");
    private static readonly Regex InlineEvent = new(@"(?:开放|开启|推出)[「\[](?<name>[^」\]]+)[」\]](?<kind>[\p{L}]{0,12}活动)[。！]?$");
    private static readonly Regex Season = new(@"^[○●]\s*「(?<name>[^」]+)」");
    private static readonly Regex Trial = new(@"干员【(?<operator>[^】]+)】的「(?<name>[^」]+)」干员试用活动");
    private static readonly Regex OnlyEnd = new(@"^✦\s*【(?<name>[^】]+)】(?<suffix>.*?)结束时间[：:](?<date>.*)$");
    private static readonly Regex WindowLabel = new(@"^[✦·]?\s*(?:活动时间|赛季更新|开放时间|挑战时间|参与时间|更新时间)\s*[：:]\s*");
    private static readonly Regex Reward = new(@"可领取|可获得|获得.*奖励|奖励|星声|嵌晶玉");
    internal static string Normalize(string name) => Regex.Replace(name, @"[「」\[\]【】\s]", "");
    internal static string VersionOf(EventArticle article) => Regex.Match(article.Source.Title,
        article.Game == "wuwa" ? @"(\d+\.\d+)\s*版本" : @"「([^」]+)」版本").Groups[1].Value;

    internal static string[] Lines(string html)
    {
        var lines = Regex.Replace(html, @"<br\b[^>]*>|</(?:p|div|h[1-6]|li|tr)\s*>", "\n", RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(Regex.Replace(lines, "<[^>]+>", "")).Split('\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    internal static DateTimeOffset? ReadDate(string text, bool deadline = false) => Date(Dates.Match(text), deadline);

    private static DateTimeOffset? Date(Match match, bool deadline = false)
    {
        if (!match.Success) return null;
        int Part(string name) => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
        try
        {
            var date = new DateTimeOffset(Part("y"), Part("m"), Part("d"), Part("h"), Part("min"), 0, China);
            return deadline && date.Minute == 59 ? date.AddMinutes(1) : date;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static Maintenance? ReadMaintenance(EventArticle article, string[] lines)
    {
        var version = VersionOf(article);
        if (version.Length == 0) return null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^(✦|■|▼//)?\s*(更新维护时间|版本维护时间)")) continue;
            var dates = Dates.Matches(lines[i] + " " + lines.ElementAtOrDefault(i + 1));
            if (dates.Count >= 2 && Date(dates[0]) is {} start && Date(dates[1]) is {} end && end > start)
                return new(version, article.Published, start, end);
        }
        return null;
    }

    internal static List<GameEvent> Parse(EventArticle article, string[] lines, DateTimeOffset? maintenanceEnd = null)
    {
        var result = new List<GameEvent>();
        var wuwa = article.Game == "wuwa";
        var version = VersionOf(article);
        var origin = maintenanceEnd ?? ReadMaintenance(article, lines)?.End ?? article.StartTime;
        var excludedSection = false;
        var banner = false;
        var trialSection = false;
        string? parent = null;
        string? name = null;
        var kind = "";
        var block = new List<string>();
        void Add(string title, string type, DateTimeOffset start, DateTimeOffset? end, bool pending, int? amount, DateTimeOffset? claim = null, string? displayName = null)
        {
            var key = $"{article.Game}|{Normalize(title)}|{start.ToOffset(China):yyyy-MM-dd}";
            result.Add(new(key, article.Game, displayName ?? title, type, start, end, pending, claim, new(wuwa ? "星声" : "嵌晶玉", amount),
                article.Source, version, article.Published));
        }
        void Emit(string title, string type, List<string> content)
        {
            if (Regex.IsMatch(title + type, Excluded) || content.Any(l => l.Contains("平台专属活动页"))) return;
            if (content.Any(l => Regex.IsMatch(l, "无奖励|不设奖励|没有奖励"))) return;
            if (!type.Contains("活动") && !content.Any(l => Reward.IsMatch(l))) return;
            var label = content.FindIndex(l => WindowLabel.IsMatch(l));
            if (label < 0) return;
            var time = WindowLabel.Replace(content[label], "");
            var windows = new List<string>();
            if (time.Length > 0) windows.Add(time);
            windows.AddRange(content.Skip(label + 1).TakeWhile(l => Regex.IsMatch(l, @"^(?:\d{4}[年/]|「[^」]+」版本)")));
            if (windows.Count == 0) windows.Add("");
            var rewards = Regex.Matches(string.Join("\n", content.Where(l => !l.Contains("补偿"))),
                wuwa ? @"星声[】]?\s*[*×x]\s*(\d{1,8})" : @"【嵌晶玉】\s*×\s*(\d{1,8})");
            int? amount = rewards.Count == 0 ? null : rewards.Sum(m => int.Parse(m.Groups[1].Value));
            var claimLine = content.FirstOrDefault(l => Regex.IsMatch(l, @"(?:领取|领奖|兑换)(?:截止时间|截止|时间)[：:]"));
            var claim = claimLine == null ? null : Date(Dates.Matches(claimLine).LastOrDefault() ?? Match.Empty, true);
            foreach (var window in windows)
            {
                if (Regex.IsMatch(window, "永久开放|常驻")) continue;
                var parts = Regex.Split(window, @"\s*[~～\-至]\s*", RegexOptions.None);
                // A release instant without an end is not a limited-time window.
                if (parts.Length == 1 && !window.Contains("版本期间") && (Dates.IsMatch(window) || window.Contains("版本更新后"))) continue;
                var versionStart = Regex.IsMatch(parts[0], "版本(?:更新后|期间)");
                var start = versionStart ? origin : Date(Dates.Match(parts[0]));
                var pending = window.Contains("版本期间") || window.Contains("维护前");
                var end = parts.Length == 2 ? Date(Dates.Match(parts[1]), true) : null;
                if (start == null || end <= start) { pending = false; end = null; }
                Add(title, type, start ?? origin, end, pending, amount, claim);
            }
        }
        void Flush()
        {
            if (banner && (!excludedSection || trialSection))
            {
                var label = block.FindIndex(l => WindowLabel.IsMatch(l));
                if (label < 0) return;
                var window = block.Skip(label + 1).TakeWhile(l => Dates.IsMatch(l) && !l.StartsWith("·")).Prepend(block[label]);
                foreach (var line in block)
                {
                    var trial = Trial.Match(line);
                    if (trial.Success && Reward.IsMatch(line))
                        Emit(trial.Groups["name"].Value + "·" + trial.Groups["operator"].Value,
                            "干员试用活动", window.Append(line).ToList());
                }
            }
            else if (!excludedSection && name != null) Emit(name,kind,block);
        }
        foreach (var line in lines)
        {
            bool section = Regex.IsMatch(line, @"^(?:■|▼//|【[^】]+】[：:]?$|✦[^✦]+✦$)");
            var heading = Heading.Match(line);
            var inline = InlineEvent.Match(line);
            var season = Season.Match(line);
            var endOnly = wuwa ? OnlyEnd.Match(line) : Match.Empty;
            if (section || heading.Success || inline.Success || season.Success || endOnly.Success)
            {
                Flush();
                block.Clear();
                name = null;
                banner = false;
                if (section)
                {
                    excludedSection = Regex.IsMatch(line, Excluded);
                    trialSection = Regex.IsMatch(line,"寻访|唤取") && !Regex.IsMatch(line,"商城|采购中心|补偿");
                    if (!excludedSection)
                    {
                        var title = Regex.Match(line, @"[「【](?<title>[^」】]+)[」】]");
                        if (title.Success) { name = title.Groups["title"].Value; kind = "限时内容"; }
                    }
                    parent = null;
                }
                else if (endOnly.Success)
                {
                    var title = endOnly.Groups["name"].Value + endOnly.Groups["suffix"].Value;
                    var cycle = Regex.Match(endOnly.Groups["suffix"].Value, @"周期「([^」]+)」$");
                    var displayName = cycle.Success ? endOnly.Groups["name"].Value + "·" + cycle.Groups[1].Value : title;
                    if (!excludedSection && !Regex.IsMatch(title, Excluded)) Add(title,"限时活动",origin,
                        Date(Dates.Match(endOnly.Groups["date"].Value), true),false,null,displayName:displayName);
                }
                else if (season.Success && parent != null)
                {
                    name = parent + "·" + season.Groups["name"].Value;
                    kind = "赛季";
                }
                else if (inline.Success || heading.Success)
                {
                    var match = inline.Success ? inline : heading;
                    name = match.Groups["name"].Value;
                    kind = match.Groups["kind"].Value;
                    if (!inline.Success) parent = name;
                    banner = Regex.IsMatch(kind,"寻访|唤取") && !Regex.IsMatch(kind,"申领|商城");
                }
            }
            else
            {
                if (name == null && WindowLabel.IsMatch(line) && !excludedSection &&
                    !Regex.IsMatch(article.Source.Title, Excluded + "|版本内容说明|版本更新说明|维护预告"))
                { name = article.Source.Title; kind = "限时内容"; }
                block.Add(line);
            }
        }
        Flush();
        return result;
    }

    internal static void Merge(List<GameEvent> events, GameEvent item)
    {
        var index = events.FindIndex(e => e.Key == item.Key);
        if (index < 0) events.Add(item);
        else if ((events[index].End == null || item.End != null) && (item.Published >= events[index].Published || events[index].End == null))
            events[index] = item;
    }
}
