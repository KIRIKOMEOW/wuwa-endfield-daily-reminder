using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GameDailyWidget;

/// <summary>Deterministic reminder checks; no credentials, network, files or notification delivery.</summary>
public static class StartupDailyReminderChecks
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T12:00:00+08:00");

    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        Check("国服04点切换游戏日且不依赖本机时区", () =>
        {
            var before = DateTimeOffset.Parse("2026-10-05T03:59:59+08:00");
            var after = before.AddSeconds(1);
            Assert(StartupDailyReminder.GameDayKey(before) == "2026-10-04", "04点前日期错误");
            Assert(StartupDailyReminder.GameDayKey(after) == "2026-10-05", "04点整未换日");
            Assert(StartupDailyReminder.GameDayKey(after.ToOffset(TimeSpan.FromHours(-7))) == "2026-10-05", "使用了本机时区或夏令时");
        });
        Check("发送条件只取决于真实运行与提醒开关", () =>
        {
            Assert(StartupDailyReminder.ShouldSend(false, true), "真实运行且启用提醒仍被拦截");
            Assert(!StartupDailyReminder.ShouldSend(true, true), "预览允许发送");
            Assert(!StartupDailyReminder.ShouldSend(false, false), "关闭提醒仍发送");
            Assert(!StartupDailyReminder.ShouldSend(true, false), "关闭提醒的预览允许发送");
        });
        Check("手动自动启动条件一致且同日再次启动不被去重", () =>
        {
            var parameters = typeof(StartupDailyReminder).GetMethod(nameof(StartupDailyReminder.ShouldSend))!.GetParameters();
            Assert(parameters.Length == 2 && parameters[0].Name == "preview" && parameters[1].Name == "enabled",
                "提醒条件仍依赖启动方式或已发送日期");
            var automaticLaunch = StartupDailyReminder.ShouldSend(preview: false, enabled: true);
            var manualLaunch = StartupDailyReminder.ShouldSend(preview: false, enabled: true);
            var repeatedSameDayLaunch = StartupDailyReminder.ShouldSend(preview: false, enabled: true);
            Assert(automaticLaunch && manualLaunch && repeatedSameDayLaunch, "启动方式或上次发送影响了下一次启动");
        });
        Check("非强制内容组合在两游戏达标时仍保持原抑制规则", () =>
        {
            Assert(Compose(State("wuwa"), State("endfield")) == null, "两游戏达标仍发送");
        });
        Check("每次启动强制概览即使两游戏达标仍如实返回状态", () =>
        {
            var message = Compose(State("wuwa"), State("endfield"), force: true)!;
            Assert(message.Title == "今日进度概览" && message.TargetGameKey == "wuwa", "强制概览标题或默认页面错误");
            Assert(message.Body.Contains("鸣潮：今日活跃度已达标（活跃度 100/100）", StringComparison.Ordinal), "缺少鸣潮达标状态");
            Assert(message.Body.Contains("终末地：今日活跃度已达标（活跃度 100/100）", StringComparison.Ordinal), "缺少终末地达标状态");
            Assert(!message.Body.Contains("待清", StringComparison.Ordinal) && !message.Body.Contains("领取", StringComparison.Ordinal), "达标被写成待清或领奖");
        });
        Check("明确未达标显示服务器活跃度并优先打开该游戏", () =>
        {
            var message = Compose(State("wuwa"), Incomplete("endfield", 25))!;
            Assert(message.TargetGameKey == "endfield", "没有优先未达标游戏");
            Assert(message.Body.Contains("终末地：今日待清（活跃度 25/100）", StringComparison.Ordinal), "未达标数值或文案错误");
            Assert(message.Body.Contains("鸣潮：今日活跃度已达标", StringComparison.Ordinal), "达标游戏被标为待清");
        });
        Check("两游戏未达标时优先鸣潮", () =>
        {
            var message = Compose(Incomplete("wuwa", 0), Incomplete("endfield", 50))!;
            Assert(message.TargetGameKey == "wuwa" && message.Body.Contains("活跃度 0/100", StringComparison.Ordinal), "首个未达标选择错误或零值丢失");
        });
        Check("明确未达标优先于未知游戏", () =>
        {
            var message = Compose(State("wuwa") with { DailyState = "unknown" }, Incomplete("endfield", 60))!;
            Assert(message.TargetGameKey == "endfield", "未知进度抢占明确未达标");
            Assert(message.Body.Contains("鸣潮：进度待确认", StringComparison.Ordinal), "未知进度被写成未清");
        });
        Check("未知游戏作为打开目标且已达标游戏不被称为待清", () =>
        {
            var message = Compose(State("wuwa"), State("endfield") with { Daily = null })!;
            Assert(message.TargetGameKey == "endfield" && !message.Body.Contains("待清", StringComparison.Ordinal), "未知或已达标游戏被误判");
            Assert(Compose(State("wuwa") with { DailyState = "unknown" }, State("endfield") with { DailyState = "unknown" })!.TargetGameKey == "wuwa", "两个未知未选择首个游戏");
        });
        Check("未登录明确提示确认而不制造零进度", () =>
        {
            var message = Compose(State("wuwa") with { LoggedIn = false }, State("endfield"))!;
            Assert(message.Body.Contains("鸣潮：未登录，请打开组件确认", StringComparison.Ordinal), "未登录提示不明确");
            Assert(!message.Body.Contains("待清", StringComparison.Ordinal) && !message.Body.Contains("活跃度 0/", StringComparison.Ordinal), "未登录被当成零进度");
        });
        Check("失败忙碌过期演示均需确认且不能信任残留达标数据", () =>
        {
            var cases = new[]
            {
                State("wuwa") with { Error = "private-auth-url" },
                State("wuwa") with { Busy = true },
                State("wuwa") with { Stale = true },
                State("wuwa") with { Demo = true },
                State("wuwa") with { UpdatedAt = null },
                State("wuwa") with { UpdatedAt = Now.AddMinutes(-15) },
                State("wuwa") with { UpdatedAt = Now.AddMinutes(-16) },
                State("wuwa") with { UpdatedAt = Now.AddSeconds(1) }
            };
            foreach (var state in cases)
            {
                var message = Compose(state, State("endfield"))!;
                Assert(message.Body.Contains("鸣潮：进度待确认", StringComparison.Ordinal) && !message.Body.Contains("待清", StringComparison.Ordinal), "残留数据被信任或未知被说成待清");
            }
            Assert(Compose(State("wuwa") with { UpdatedAt = Now.AddMinutes(-14) }, State("endfield")) == null, "正常新鲜数据被当作过期");
        });
        Check("04点前旧快照虽不足15分钟也不得判断新日", () =>
        {
            var current = DateTimeOffset.Parse("2026-10-05T04:01:00+08:00");
            var old = State("wuwa") with { UpdatedAt = current.AddMinutes(-2) };
            var fresh = State("endfield") with { UpdatedAt = current };
            var message = StartupDailyReminder.Compose(old, fresh, current)!;
            Assert(message.Body.Contains("鸣潮：进度待确认", StringComparison.Ordinal), "昨天快照被用于今天");
        });
        Check("缺失矛盾或无效活跃度不得制造待清或达标", () =>
        {
            var cases = new[]
            {
                State("wuwa") with { Daily = null },
                State("wuwa") with { MaxDaily = null },
                State("wuwa") with { Daily = -1 },
                State("wuwa") with { MaxDaily = 0 },
                State("wuwa") with { MaxDaily = -1 },
                State("wuwa") with { Daily = 50, DailyState = "complete" },
                State("wuwa") with { DailyState = "incomplete" },
                State("wuwa") with { DailyState = "unknown" },
                State("wuwa") with { DailyState = "unexpected" },
                State("wuwa") with { GameKey = "endfield" }
            };
            foreach (var state in cases)
                Assert(Compose(state, State("endfield"))!.Body.Contains("鸣潮：进度待确认", StringComparison.Ordinal), "不完整或矛盾数据被使用");
            Assert(Compose(State("wuwa") with { Daily = 120 }, State("endfield")) == null, "达到或超过阈值未被视为达标");
        });
        Check("鸣潮可信未解锁状态不催清且可抑制自动提醒", () =>
        {
            var locked = State("wuwa") with { DailyState = "locked", Daily = null, MaxDaily = 0 };
            Assert(Compose(locked, State("endfield")) == null, "未解锁被当作待清");
            Assert(Compose(locked, State("endfield"), force: true)!.Body.Contains("鸣潮：日常活跃度未解锁", StringComparison.Ordinal), "强制概览未说明锁定");
            Assert(Compose(locked with { Error = "failed" }, State("endfield"))!.Body.Contains("鸣潮：进度待确认", StringComparison.Ordinal), "失败锁定状态被当作可信");
            Assert(Compose(State("wuwa"), State("endfield") with { DailyState = "locked" })!.Body.Contains("终末地：进度待确认", StringComparison.Ordinal), "终末地未知锁定状态被错误抑制");
        });
        Check("通知不含任何账号身份状态或异常原文", () =>
        {
            var secret = "PRIVATE-UID-AUTH-NICKNAME";
            var state = Incomplete("wuwa", 10) with
            {
                Nickname = secret,
                GameName = secret,
                CopyIdentity = secret,
                SelectedAccountKey = secret,
                IdentityLabel = secret,
                Status = secret,
                Details = new[] { new DetailLine(secret, secret) },
                Accounts = new[] { new AccountChoice(secret, secret) }
            };
            var message = Compose(state, State("endfield"), force: true)!;
            Assert(!(message.Title + message.Body + message.TargetGameKey).Contains(secret, StringComparison.Ordinal), "账号信息泄露到通知");
            var failed = Compose(state with { Error = secret }, State("endfield"), force: true)!;
            Assert(!failed.Body.Contains(secret, StringComparison.Ordinal) && failed.Body.Contains("鸣潮：进度待确认", StringComparison.Ordinal), "异常原文泄露到通知");
        });

        return Task.FromResult<IReadOnlyList<string>>(results);

        void Check(string name, Action action)
        {
            try { action(); results.Add("PASS " + name); }
            catch (Exception exception) { results.Add("FAIL " + name + "：" + exception.Message); }
        }
    }

    private static DailyReminderMessage? Compose(GameViewState wuwa, GameViewState endfield, bool force = false) =>
        StartupDailyReminder.Compose(wuwa, endfield, Now, force);

    private static GameViewState State(string game) => new()
    {
        GameKey = game, LoggedIn = true, UpdatedAt = Now,
        DailyState = "complete", Daily = 100, MaxDaily = 100
    };

    private static GameViewState Incomplete(string game, int daily) =>
        State(game) with { DailyState = "incomplete", Daily = daily };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
