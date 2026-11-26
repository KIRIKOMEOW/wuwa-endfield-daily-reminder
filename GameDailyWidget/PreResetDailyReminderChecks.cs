using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FakeController = GameDailyWidget.DailyResetCoordinatorChecks.FakeController;

namespace GameDailyWidget;

internal static class PreResetDailyReminderChecks
{
    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add("PASS " + name);
        }

        var first = new DateTimeOffset(2026, 11, 25, 3, 30, 0, TimeSpan.FromHours(8));
        var now = first;
        var settings = UnifiedSettings.Load(true);
        using var wuwa = new FakeController("wuwa");
        using var endfield = new FakeController("endfield");
        GameViewState Sample(string key, int points) => new()
        {
            GameKey = key, UpdatedAt = now, LoggedIn = true, Daily = points, MaxDaily = 100,
            DailyState = points < 100 ? "incomplete" : "complete"
        };
        var wuwaPoints = 40;
        var endfieldPoints = 20;
        wuwa.State = Sample("wuwa", 0);
        endfield.State = Sample("endfield", 0);
        wuwa.Work = () => { wuwa.State = Sample("wuwa", wuwaPoints); return Task.CompletedTask; };
        endfield.Work = () => { endfield.State = Sample("endfield", endfieldPoints); return Task.CompletedTask; };
        var sent = new List<DailyReminderMessage>();
        PreResetDailyReminder Create() => new(wuwa, endfield, settings,
            (message, _, _) => { sent.Add(message); return true; }, () => TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
        var reminder = Create();
        reminder.Tick(first.AddSeconds(-1));
        Check(wuwa.Calls == 0 && endfield.Calls == 0 && sent.Count == 0, "03:30 前不额外查询或发送");
        reminder.Tick(now.ToOffset(TimeSpan.FromHours(-8)));
        Check(sent.Count == 1 && sent[0].Body.Contains("40/100") && sent[0].Body.Contains("20/100")
            && sent[0].Title.Contains("30 分钟"), "按北京时间检查两个游戏并合并为一条通知");
        now = first.AddSeconds(7);
        reminder.Tick(now);
        reminder = Create();
        reminder.Tick(now);
        Check(sent.Count == 1 && wuwa.Calls == 1 && endfield.Calls == 1, "同分钟内重复 tick 和重启不会重复提醒");
        wuwaPoints = 100;
        now = first.AddMinutes(15);
        reminder.Tick(now);
        Check(sent.Count == 2 && sent[1].TargetGameKey == "endfield" && !sent[1].Body.Contains("鸣潮")
            && sent[1].Title.Contains("15 分钟"), "03:45 重新查询并只列出尚未达标的终末地");
        now = first.AddMinutes(25);
        reminder.Tick(now);
        Check(sent.Count == 3 && sent[2].Title.Contains("5 分钟"), "忽略通知且仍未达标时 03:55 再提醒");
        endfieldPoints = 100;
        now = first.AddDays(1).AddMinutes(25);
        reminder.Tick(now);
        Check(wuwa.Calls == 4 && endfield.Calls == 4 && sent.Count == 3, "下一天恢复调度，休眠后只检查当前时点，全部达标静默");
        var valid = Sample("wuwa", 0);
        GameViewState[] ignored = [valid with { LoggedIn = false }, valid with { Error = "offline" },
            valid with { UpdatedAt = now.AddSeconds(-1) }, valid with { Stale = true }, valid with { Demo = true },
            valid with { DailyState = "locked" }, valid with { Busy = true }];
        Check(ignored.All(state => StartupDailyReminder.ComposeBeforeReset(state, endfield.State, now, now, TimeZoneInfo.Utc) == null),
            "未登录、失败、上次查询、过期、演示、未解锁及刷新中的进度不冒充待清");
        now = first.AddDays(2);
        wuwa.State = valid with { Busy = true };
        endfieldPoints = 35;
        reminder.Tick(now);
        Check(sent.Count == 3 && wuwa.Calls == 4, "等待正在进行的查询，不重复启动");
        now = now.AddSeconds(25);
        reminder.Tick(now);
        Check(sent.Count == 4 && sent[^1].TargetGameKey == "endfield" && sent[^1].Body.Contains("35/100"),
            "一个游戏超时不会阻塞另一个游戏的待清提醒");
        now = first.AddDays(2).AddMinutes(15);
        reminder.Tick(now);
        wuwa.State = Sample("wuwa", 50);
        reminder.Tick(now.AddSeconds(1));
        Check(sent.Count == 5 && sent[^1].Body.Contains("50/100"), "复用时点内完成的在途查询");
        now = first.AddDays(2).AddMinutes(25);
        wuwa.State = valid with { Busy = true };
        reminder.Tick(now);
        reminder.Tick(now.AddMinutes(5));
        Check(sent.Count == 5, "跨过 04:00 后不补发旧通知");
        return checks;
    }
}
