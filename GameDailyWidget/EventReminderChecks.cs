using System;
using System.Collections.Generic;
using System.Linq;

namespace GameDailyWidget;

internal static partial class EventChecks
{
    internal static IReadOnlyList<string> Reminder()
    {
        var checks = new List<string>();
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException(text);
            checks.Add("PASS " + text);
        }
        var store = new EventStore();
        var end = SampleTime.AddDays(6);
        var item = EventParser.Parse(Article("wuwa","42","3.8版本内容说明"), ["[计时]签到活动","✦活动时间：版本期间"]).Single()
            with { End = end, EndPending = false };
        store.Data.Events.Add(item);
        var notices = new List<EventNotice>();
        var enabled = true;
        var success = true;
        var reminder = new EventDeadlineReminder(store, () => enabled, n => { notices.Add(n); return success; },() => TimeZoneInfo.Utc);
        foreach (var hours in new[] {72,24,3})
        {
            var before = notices.Count;
            var slot = end.AddHours(-hours).ToOffset(TimeSpan.FromHours(-8));
            reminder.Tick(slot.AddSeconds(-1)); reminder.Tick(slot); reminder.Tick(slot.AddSeconds(1));
            Check(notices.Count == before + 1 && notices[^1].ClaimKey == item.Key && notices[^1].Expires == end, hours + " 小时边界及跨时区去重");
        }
        store.Data.Sent.Clear(); notices.Clear();
        reminder.Tick(end.AddHours(-2)); reminder.Tick(end);
        Check(notices.Count == 1 && notices[0].Title.Contains("不到 3 小时") && store.Data.Sent.Count == 3, "休眠只补最急档，已过各档一起记账");
        store.Data.Sent.Clear(); notices.Clear();
        reminder.Tick(end); reminder.Tick(item.Start.AddSeconds(-1));
        var now = end.AddHours(-21);
        store.Mark(item,true,false,now); reminder.Tick(now);
        store.Mark(item,false,true,now); reminder.Tick(now);
        store.Mark(item,false,false,now); store.Data.Sent.Clear(); enabled = false; reminder.Tick(now);
        enabled = true; store.Data.Events[0] = item with { End = null, EndPending = true }; reminder.Tick(now);
        Check(notices.Count == 0, "截止、未开始、已领完、忽略、关闭、待公告均静默");
        store.Data.Events[0] = item;
        reminder.Tick(now);
        Check(notices.Single().Title.Contains("1 天") && store.Data.Sent.Count == 2, "刚补齐时间只补发一档");
        store.Mark(item,true,false,now);
        store.Data.Sent.Clear(); store.Mark(item,false,false,now); reminder.Tick(now);
        Check(notices.Count == 1 && store.Data.Sent.Count == 2, "撤销已领完从下一档开始");
        store.Data.Sent.Clear(); notices.Clear();
        for (var i = 1; i <= 5; i++) store.Data.Events.Add(item with { Key = item.Key + i, Name = "演示" + i, End = end.AddMinutes(-i) });
        reminder = new(store, () => true, n => { notices.Add(n); return success; },() => TimeZoneInfo.Utc);
        success = false; reminder.Tick(now); reminder.Tick(now);
        Check(store.Data.Sent.Count == 0 && notices.Count == 2, "失败不落已发记录，下一 tick 重试");
        success = true; reminder.Tick(now);
        var merged = notices.Last();
        Check(merged.ClaimKey == null && merged.Body.StartsWith("鸣潮「演示5」") && merged.Body.EndsWith("另有 2 个")
            && merged.Expires == end && merged.Tag.StartsWith("evt-") && merged.Tag.Length <= 64, "合并排序、四条上限、最晚过期");
        reminder.DeliveryFailed(merged.Tag); reminder.Tick(now);
        Check(store.Data.Sent.Count == 0 && notices.Count == 3, "异步失败撤销记录，总计最多三次");
        foreach (var (minutes, expected) in new[] { (4320,"剩 3 天"), (4319,"剩不到 3 天"),
            (1440,"剩不到 3 天"), (1439,"剩不到 1 天"), (300,"剩不到 1 天"),
            (180,"剩不到 1 天"), (179,"剩不到 3 小时") })
        {
            var pending = new EventStore();
            pending.Data.Events.Add(item with { End = end.AddHours(2) });
            pending.Data.Events.Add(item with { Key = item.Key + "-first", End = end });
            string? title = null;
            new EventDeadlineReminder(pending, () => true, n =>
            {
                title = EventNotification.Content(n,false).GetXml().SelectSingleNode("//binding/text[1]")?.InnerText;
                return true;
            },() => TimeZoneInfo.Utc).Tick(end.AddMinutes(-minutes));
            Check(title == "限时活动 · " + expected, $"剩余 {minutes} 分钟时最终 XML 标题为「{expected}」");
        }
        var single = merged with { ClaimKey = item.Key, Game = item.Game };
        var xml = EventNotification.Content(single, true).GetXml();
        EventActivation? Decode(string target, string attribute)
        {
            var argument = xml.SelectSingleNode(target)?.Attributes.GetNamedItem(attribute)?.NodeValue?.ToString();
            return EventActivation.Parse(Microsoft.Toolkit.Uwp.Notifications.ToastArguments.Parse(argument!));
        }
        Check(Decode("/toast","launch") == new EventActivation(item.Game,null,true)
            && Decode("//action[@content='打开活动']","arguments") == new EventActivation(item.Game,null,true)
            && Decode("//action[@content='已领完']","arguments") == new EventActivation(item.Game,item.Key,true),
            "从最终 Toast XML 回读正文和两个按钮参数，演示隔离和中文键完整");
        Check(EventNotification.Content(merged, false).GetXml().SelectNodes("//action").Count == 1, "多活动仅提供打开按钮");
        var report = System.Text.Json.JsonSerializer.SerializeToElement(EventNotification.ProbeReport(new(true,"演示"),true,false,"evt-demo"));
        Check(report.GetProperty("status").GetString() == "passed" && report.GetProperty("nativeToastInOwnWindowsHistory").GetBoolean(),
            "活动探针可序列化 Windows 历史回执");
        var deadline = new DateTimeOffset(2026,12,23,4,0,0,EventParser.China);
        var prior = new EventStore();
        prior.Data.Events.Add(item with { End=deadline });
        var viewZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var bodies = new List<string>();
        var localReminder = new EventDeadlineReminder(prior,() => true,n =>
        {
            bodies.Add(EventNotification.Content(n,false).GetXml().SelectSingleNode("//binding/text[2]")!.InnerText);
            return true;
        },() => viewZone);
        var slot72 = deadline.AddDays(-3);
        localReminder.Tick(slot72);
        var expectedKey = item.Key + "|2026-12-23T04:00|72h";
        var saved = System.Text.Json.JsonSerializer.Serialize(prior.Data,EventStore.Json);
        Check(bodies.Single().Contains("本地 12/22 15:00") && prior.Data.Sent.ContainsKey(expectedKey) &&
            EventDeadlineReminder.DeadlineTip(deadline,viewZone)=="12-22 15:00（北京时间 12-23 04:00）",
            "活动通知 XML 和提示按本地日期换算，发送键使用北京时间");
        viewZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        localReminder.Tick(slot72.AddSeconds(5));
        Check(bodies.Count==1 && System.Text.Json.JsonSerializer.Serialize(prior.Data,EventStore.Json)==saved &&
            EventDeadlineReminder.DeadlineTip(deadline,viewZone)=="12-23 04:00","切换时区不重复提醒，events.json 序列化内容不变，东八区只显示一次");
        return checks;
    }
}

