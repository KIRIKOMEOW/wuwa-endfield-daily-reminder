using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GameDailyWidget;

/// <summary>Offline scheduling checks: no WPF, credentials, files or network.</summary>
public static class DailyResetRefreshChecks
{
    private static readonly DateTimeOffset Reset = new(2026, 10, 7, 4, 0, 0, TimeSpan.FromHours(8));

    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        Check("重置突发窗口固定为国服04点起含62秒且结束不包含", () =>
        {
            Assert(!DailyResetRefreshSchedule.IsBurstWindow(Reset.AddTicks(-1)), "重置前进入窗口");
            Assert(DailyResetRefreshSchedule.IsBurstWindow(Reset), "04点整不在窗口");
            Assert(DailyResetRefreshSchedule.IsBurstWindow(Reset.AddSeconds(60)), "最后槽不在窗口");
            Assert(DailyResetRefreshSchedule.IsBurstWindow(Reset.AddSeconds(62).AddTicks(-1)), "宽限末尾过早结束");
            Assert(!DailyResetRefreshSchedule.IsBurstWindow(Reset.AddSeconds(62)), "窗口结束仍允许突发");
            Assert(!DailyResetRefreshSchedule.IsBurstWindow(Reset.AddMinutes(2)), "窗口后仍允许突发");
        });
        Check("鸣潮00、20、40、60秒各领取一次且槽内不重复", () =>
        {
            var schedule = Burst();
            Assert(!schedule.TryTake(Reset.AddTicks(-1)), "04点前领取");
            foreach (var second in new[] { 0, 20, 40, 60 })
            {
                Assert(schedule.TryTake(Reset.AddSeconds(second)), "槽位没有领取：" + second);
                Assert(!schedule.TryTake(Reset.AddSeconds(second)), "相同tick重复领取：" + second);
                var slotEnd = second == 60 ? 62 : second + 20;
                Assert(!schedule.TryTake(Reset.AddSeconds(slotEnd).AddTicks(-1)), "同一槽末尾重复领取：" + second);
            }
            Assert(!schedule.TryTake(Reset.AddSeconds(62)), "结束后领取");
        });
        Check("最后60秒槽允许首次tick迟到不足2秒", () =>
        {
            var schedule = Burst();
            Assert(schedule.TryTake(Reset.AddSeconds(62).AddTicks(-1)), "首次迟到tick未领取最后槽");
            Assert(!schedule.TryTake(Reset.AddSeconds(60)), "最后槽时钟回拨后重复领取");
            Assert(!Burst().TryTake(Reset.AddSeconds(62)), "首次tick迟到2秒仍领取");
            Assert(!Burst().TryTake(Reset.AddMinutes(1).AddSeconds(5)), "迟到窗口结束仍领取");
        });
        Check("窗口中启动只领取最新槽而不补发较早槽", () =>
        {
            foreach (var second in new[] { 19, 39, 59, 61 })
            {
                var schedule = Burst();
                Assert(schedule.TryTake(Reset.AddSeconds(second)), "中途启动未领取最新槽");
                var currentSlotStart = second / 20 * 20;
                for (var earlier = 0; earlier <= currentSlotStart; earlier += 20)
                    Assert(!schedule.TryTake(Reset.AddSeconds(earlier)), "中途启动回放已错过槽");
            }
        });
        Check("跳时仅领取当下槽且结束后不回放", () =>
        {
            var schedule = Burst();
            Assert(schedule.TryTake(Reset), "首槽未领取");
            Assert(schedule.TryTake(Reset.AddSeconds(45)), "跳过20秒槽后未领取40秒槽");
            Assert(!schedule.TryTake(Reset.AddSeconds(20)), "跳时后回放错过的20秒槽");
            Assert(!schedule.TryTake(Reset.AddSeconds(59)), "40秒槽重复领取");
            Assert(schedule.TryTake(Reset.AddSeconds(61)), "跳时未领取最后槽");
            Assert(!schedule.TryTake(Reset.AddMinutes(2)), "窗口结束后补发");
            var late = Burst();
            Assert(!late.TryTake(Reset.AddMinutes(2)), "首次tick在结束后回放四个槽");
        });
        Check("时钟回拨跨槽跨日均不重复但次日新槽可领取", () =>
        {
            var schedule = Burst();
            Assert(schedule.TryTake(Reset.AddSeconds(40)), "当天40秒槽未领取");
            Assert(!schedule.TryTake(Reset.AddSeconds(20)), "当天回拨重复领取较早槽");
            Assert(!schedule.TryTake(Reset.AddDays(-1).AddSeconds(60)), "跨日回拨领取旧日期槽");
            Assert(schedule.TryTake(Reset.AddSeconds(60)), "当天后续槽被错误去重");
            Assert(schedule.TryTake(Reset.AddDays(1)), "次日首槽未领取");
            Assert(!schedule.TryTake(Reset.AddSeconds(60)), "回拨到前一日重复领取");
            Assert(schedule.TryTake(Reset.AddDays(1).AddSeconds(20)), "次日新槽被错误去重");
        });
        Check("UTC和任意本地偏移映射同一国服日期槽", () =>
        {
            var schedule = Burst();
            Assert(schedule.TryTake(Reset.ToUniversalTime()), "UTC时刻未映射04点");
            Assert(!schedule.TryTake(Reset.ToOffset(TimeSpan.FromHours(-7))), "相同时刻换本地偏移重复领取");
            Assert(schedule.TryTake(Reset.AddSeconds(20).ToOffset(TimeSpan.FromHours(5.5))), "半小时偏移未映射20秒槽");
            Assert(DailyResetRefreshSchedule.IsBurstWindow(Reset.ToOffset(TimeSpan.FromHours(14))), "远端偏移窗口判断错误");
            Assert(!DailyResetRefreshSchedule.IsBurstWindow(Reset.AddSeconds(62).ToUniversalTime()), "UTC下窗口结束边界错误");
        });
        Check("终末地边界模式仅第一分钟领取一次且不进入20秒突发", () =>
        {
            var schedule = Boundary();
            Assert(!schedule.TryTake(Reset.AddTicks(-1)), "边界模式04点前领取");
            Assert(schedule.TryTake(Reset), "边界模式04点未领取");
            foreach (var second in new[] { 0, 20, 40, 59, 60, 61, 62 })
                Assert(!schedule.TryTake(Reset.AddSeconds(second)), "边界模式重复或结束后领取：" + second);
            Assert(schedule.TryTake(Reset.AddDays(1).AddSeconds(59)), "边界模式次日首次迟到未领取");
            Assert(!schedule.TryTake(Reset.AddDays(1)), "边界模式回拨重复领取");
            Assert(!Boundary().TryTake(Reset.AddSeconds(60)), "边界模式首次tick在一分钟结束仍领取");
            Assert(Boundary().TryTake(Reset.AddSeconds(60).AddTicks(-1)), "边界模式第一分钟末尾过早结束");
        });
        return Task.FromResult<IReadOnlyList<string>>(checks.AsReadOnly());

        void Check(string name, Action test)
        {
            try { test(); }
            catch (InvalidOperationException failure) { throw new InvalidOperationException(name + "：" + failure.Message, failure); }
            checks.Add("PASS " + name);
        }
    }

    private static DailyResetRefreshSchedule Burst() => new(DailyResetRefreshMode.TwentySecondBurst);
    private static DailyResetRefreshSchedule Boundary() => new(DailyResetRefreshMode.BoundaryOnly);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
