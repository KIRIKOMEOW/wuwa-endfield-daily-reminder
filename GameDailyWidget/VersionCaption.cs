using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameDailyWidget;

internal static class VersionCaption
{
    private static readonly Regex Wuwa = new(@"「(?<name>[^」]+)」\s*(?<number>\d+(?:\.\d+)+)\s*版本内容说明");
    private static readonly Regex Endfield = new(@"「(?<name>[^」]+)」\s*版本更新说明");

    internal static string For(EventData? data,string game,DateTimeOffset now)
    {
        if (data == null) return "";
        var windows = data.Maintenance.GetValueOrDefault(game) ?? [];
        var isWuwa = game == "wuwa";
        foreach (var entry in data.Articles.Where(a => a.Key.StartsWith(game + ":",StringComparison.Ordinal))
            .Select(a => a.Value).Where(a => (a.Published ?? a.StartTime) <= now)
            .OrderByDescending(a => a.Published ?? a.StartTime))
        {
            var match = (isWuwa ? Wuwa : Endfield).Match(entry.Title ?? "");
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            var version = isWuwa ? match.Groups["number"].Value : name;
            var current = windows.Where(w => w.Version == version).MaxBy(w => w.Published);
            if (current != null && current.End > now) continue;
            var caption = isWuwa ? version + " · " + name : name;
            var next = windows.Where(w => w.Version != version && w.Start > now).MinBy(w => w.Start);
            if (next != null) return caption + $" · 版本剩 {Math.Ceiling((next.Start-now).TotalDays)} 天";
            if (current == null) return caption;
            DateTime GameDay(DateTimeOffset at) => at.ToOffset(EventParser.China).AddHours(-4).Date;
            return caption + $" · 版本第 {(GameDay(now)-GameDay(current.End)).Days+1} 天";
        }
        return "";
    }

    internal static IEnumerable<string> Check()
    {
        var now = new DateTimeOffset(2027,1,5,10,0,0,EventParser.China);
        var d = new EventData();
        d.Articles["wuwa:1"] = new(now,now,Title:"「演示标题」3.8版本内容说明");
        if (For(d,"wuwa",now) != "3.8 · 演示标题") throw new InvalidOperationException("无维护窗口时显示版本名");
        d.Maintenance["wuwa"] = [new("3.8",now.AddDays(-3),now.AddDays(-2),now.AddDays(-2))];
        if (!For(d,"wuwa",now).EndsWith("版本第 3 天")) throw new InvalidOperationException("版本日序错误");
        d.Maintenance["wuwa"].Add(new("3.9",now,now.AddHours(25),now.AddHours(28)));
        if (!For(d,"wuwa",now).EndsWith("版本剩 2 天")) throw new InvalidOperationException("下一次维护剩余时间错误");
        var afterReset = new DateTimeOffset(2027,1,6,4,2,0,EventParser.China);
        d.Articles["endfield:1"] = new(now,now,Title:"「演示旧版」 版本更新说明");
        d.Articles["endfield:2"] = new(afterReset,afterReset,Title:"「演示新版」版本更新说明");
        d.Maintenance["endfield"] = [
            new("演示旧版",now.AddDays(-2),now.AddDays(-1),afterReset.AddMinutes(-4)),
            new("演示新版",now,afterReset.AddHours(24),afterReset.AddHours(27))];
        if (For(d,"endfield",afterReset) != "演示旧版 · 版本剩 1 天")
            throw new InvalidOperationException("已发预告尚未结束维护时须保留当前版本");
        d.Maintenance["endfield"].RemoveAt(1);
        d.Articles.Remove("endfield:2");
        if (For(d,"endfield",afterReset) != "演示旧版 · 版本第 2 天")
            throw new InvalidOperationException("维护结束跨 04:00 后的游戏日序错误");
        return ["PASS 版本条仅名称、游戏日序与维护倒计时","PASS 提前发布的终末地版本公告与北京时间 04:00 跨日"];
    }
}
