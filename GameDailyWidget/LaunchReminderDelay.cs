using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

internal static class LaunchReminderDelay
{
    internal const int DefaultSeconds = 30;
    internal const int MinimumSeconds = 1;
    internal const int MaximumSeconds = 86400;

    internal static int NormalizeSeconds(int seconds) =>
        seconds is >= MinimumSeconds and <= MaximumSeconds ? seconds : DefaultSeconds;

    internal static TimeSpan Remaining(TimeSpan elapsed, int delaySeconds = DefaultSeconds)
    {
        var delay = TimeSpan.FromSeconds(NormalizeSeconds(delaySeconds));
        return elapsed <= TimeSpan.Zero ? delay : elapsed >= delay ? TimeSpan.Zero : delay - elapsed;
    }

    internal static async Task WaitAsync(long startedAt, CancellationToken cancellationToken,
        int delaySeconds = DefaultSeconds)
    {
        var normalizedSeconds = NormalizeSeconds(delaySeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Remaining(Stopwatch.GetElapsedTime(startedAt), normalizedSeconds);
            if (remaining <= TimeSpan.Zero) return;
            // Delay rounds to milliseconds; round up and recheck the deadline.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), cancellationToken);
        }
    }
}

public static class LaunchReminderDelayChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        var oldSettings = JsonSerializer.Deserialize<UnifiedSettings>("""
            { "Scale": 1.37, "StartupReminderEnabled": true,
              "Wuwa": { "AccountKey": "synthetic-reminder-account", "RefreshMinutes": 10 } }
            """) ?? throw new InvalidOperationException("Synthetic old settings could not be read.");
        Assert(new UnifiedSettings().StartupReminderDelaySeconds == 30 &&
            oldSettings.StartupReminderDelaySeconds == 30 && oldSettings.Scale == 1.37 &&
            oldSettings.Wuwa.AccountKey == "synthetic-reminder-account" && oldSettings.Wuwa.RefreshMinutes == 10,
            "Default or old settings without the new delay field must retain thirty seconds and other preferences.");
        foreach (var seconds in new[] { 1, 60, 3600 })
        {
            var settings = JsonSerializer.Deserialize<UnifiedSettings>(
                "{\"StartupReminderDelaySeconds\":" + seconds + "}")!;
            var restored = JsonSerializer.Deserialize<UnifiedSettings>(JsonSerializer.Serialize(settings))!;
            Assert(restored.StartupReminderDelaySeconds == seconds, "Custom delay was lost in a settings round trip.");
        }
        checks.Add("PASS 提醒延时默认30秒，缺字段旧配置兼容且1秒、60秒和3600秒设置往返保留");

        foreach (var seconds in new[] { 1, 30, 60, 3600, 86400 })
            Assert(LaunchReminderDelay.NormalizeSeconds(seconds) == seconds, "Valid reminder delay was changed.");
        foreach (var seconds in new[] { int.MinValue, -1, 0, 86401, int.MaxValue })
        {
            Assert(LaunchReminderDelay.NormalizeSeconds(seconds) == 30, "Invalid reminder delay must fall back to thirty seconds.");
            Assert(LaunchReminderDelay.Remaining(TimeSpan.Zero, seconds) == TimeSpan.FromSeconds(30),
                "Invalid delay used a different deadline rule.");
        }
        checks.Add("PASS 提醒延时允许1至86400秒，越界或非法值统一回退30秒");

        if (LaunchReminderDelay.Remaining(TimeSpan.Zero) != TimeSpan.FromSeconds(30)
            || LaunchReminderDelay.Remaining(TimeSpan.FromSeconds(12)) != TimeSpan.FromSeconds(18)
            || LaunchReminderDelay.Remaining(TimeSpan.FromMilliseconds(29999)) != TimeSpan.FromMilliseconds(1))
            throw new InvalidOperationException("Launch reminder may not fire before the thirty-second deadline.");
        if (LaunchReminderDelay.Remaining(TimeSpan.FromSeconds(30)) != TimeSpan.Zero
            || LaunchReminderDelay.Remaining(TimeSpan.FromMinutes(1)) != TimeSpan.Zero)
            throw new InvalidOperationException("Initialization must not add another delay after thirty seconds.");
        checks.Add("PASS 默认调用仍从应用启动计满30秒，初始化完成不能提前发送或叠加等待");

        foreach (var seconds in new[] { 1, 60, 3600, 86400 })
        {
            var duration = TimeSpan.FromSeconds(seconds);
            Assert(LaunchReminderDelay.Remaining(TimeSpan.FromTicks(-1), seconds) == duration &&
                LaunchReminderDelay.Remaining(TimeSpan.Zero, seconds) == duration,
                "A fresh deadline must use the full configured duration.");
            Assert(LaunchReminderDelay.Remaining(duration - TimeSpan.FromTicks(1), seconds) == TimeSpan.FromTicks(1),
                "Configured deadline became ready before its last tick.");
            Assert(LaunchReminderDelay.Remaining(duration, seconds) == TimeSpan.Zero &&
                LaunchReminderDelay.Remaining(duration + TimeSpan.FromSeconds(1), seconds) == TimeSpan.Zero,
                "Already elapsed startup time must not add another delay.");
            await LaunchReminderDelay.WaitAsync(PastOrigin(duration + TimeSpan.FromSeconds(1)), CancellationToken.None, seconds)
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert(LaunchReminderDelay.Remaining(TimeSpan.FromSeconds(12), 60) == TimeSpan.FromSeconds(48),
            "Initialization time was not counted toward the configured delay.");
        checks.Add("PASS 自定义延时沿用原启动起点，耗时计入且到点立即结束，无真实长时间等待");

        var nearDeadline = PastOrigin(TimeSpan.FromMilliseconds(980));
        await LaunchReminderDelay.WaitAsync(nearDeadline, CancellationToken.None, 1).WaitAsync(TimeSpan.FromSeconds(2));
        Assert(Stopwatch.GetElapsedTime(nearDeadline) >= TimeSpan.FromSeconds(1),
            "The real monotonic wait completed before its configured deadline.");
        checks.Add("PASS 实际短等待向上取整并重查单调时钟，不能在设定截止时间前返回");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { await LaunchReminderDelay.WaitAsync(Stopwatch.GetTimestamp(), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled) throw new InvalidOperationException("Exiting before the reminder deadline must cancel delivery.");
        using var activeCancellation = new CancellationTokenSource();
        var pending = LaunchReminderDelay.WaitAsync(Stopwatch.GetTimestamp(), activeCancellation.Token, 3600);
        Assert(!pending.IsCompleted, "A future configured deadline completed immediately.");
        activeCancellation.Cancel();
        cancelled = false;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "An active configurable reminder wait ignored exit cancellation.");
        checks.Add("PASS 软件退出可取消已启动或尚未启动的等待，不产生延迟通知");
        return checks.AsReadOnly();
    }

    private static long PastOrigin(TimeSpan elapsed) => Stopwatch.GetTimestamp() -
        checked((long)(elapsed.TotalSeconds * Stopwatch.Frequency));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
