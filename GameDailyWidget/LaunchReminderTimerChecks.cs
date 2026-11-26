using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

/// <summary>Controlled task checks with no real delays, files, accounts or network.</summary>
public static class LaunchReminderTimerChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        var due = 0;
        var errors = 0;
        var waits = new ManualWaits();
        using (var timer = new LaunchReminderTimer(123456, () => ++due, CancellationToken.None,
            waits.Wait, () => ++errors))
        {
            timer.UpdateDelay(30);
            var oldRun = timer.CompletionForChecks;
            timer.UpdateDelay(60);
            var currentRun = timer.CompletionForChecks;
            Assert(waits.Calls.Count == 2 && waits.Calls[0].StartedAt == 123456 &&
                waits.Calls[1].StartedAt == 123456 && waits.Calls[0].Seconds == 30 && waits.Calls[1].Seconds == 60,
                "更新延时没有保持同一个启动起点或正确的新值");
            Assert(waits.Calls[0].Token.IsCancellationRequested && !waits.Calls[1].Token.IsCancellationRequested,
                "更新延时未取消旧等待或误取消新等待");
            // The fake waiter ignores cancellation, so the revision guard must act.
            waits.Calls[0].Completion.SetResult();
            await oldRun;
            Assert(due == 0 && errors == 0 && !currentRun.IsCompleted,
                "忽略取消的旧完成触发提醒、错误，或清除了当前等待");
            waits.Calls[1].Completion.SetResult();
            await currentRun;
            timer.UpdateDelay(3600);
            Assert(due == 1 && errors == 0 && waits.Calls.Count == 2,
                "新等待未触发一次，或触发后修改延时重复创建等待");
        }
        checks.Add("PASS 延时30改60保持启动起点、取消旧等待，旧完成无效且新完成只提醒一次");

        due = errors = 0;
        waits = new ManualWaits();
        var disposedTimer = new LaunchReminderTimer(77, () => ++due, CancellationToken.None,
            waits.Wait, () => ++errors);
        disposedTimer.UpdateDelay(30);
        var disposedRun = disposedTimer.CompletionForChecks;
        disposedTimer.Dispose();
        disposedTimer.Dispose();
        Assert(waits.Calls[0].Token.IsCancellationRequested, "释放没有取消当前等待");
        waits.Calls[0].Completion.SetResult();
        await disposedRun;
        disposedTimer.UpdateDelay(1);
        Assert(due == 0 && errors == 0 && waits.Calls.Count == 1, "释放后的完成或更新仍触发工作");
        checks.Add("PASS Dispose幂等取消当前等待，忽略取消的迟到完成和后续更新均无效");

        due = errors = 0;
        waits = new ManualWaits();
        using (var lifetime = new CancellationTokenSource())
        using (var timer = new LaunchReminderTimer(88, () => ++due, lifetime.Token, waits.Wait, () => ++errors))
        {
            timer.UpdateDelay(60);
            var run = timer.CompletionForChecks;
            lifetime.Cancel();
            Assert(waits.Calls[0].Token.IsCancellationRequested, "生命周期取消没有透传");
            waits.Calls[0].Completion.SetResult();
            await run;
            timer.UpdateDelay(1);
            Assert(due == 0 && errors == 0 && waits.Calls.Count == 1, "生命周期结束后仍提醒或创建等待");
            using var alreadyCancelled = new LaunchReminderTimer(88, () => ++due, lifetime.Token, waits.Wait);
            alreadyCancelled.UpdateDelay(30);
            Assert(waits.Calls.Count == 1, "预取消的生命周期仍创建等待");
        }
        checks.Add("PASS 生命周期取消透传且抑制迟到完成，预取消不创建等待也不报错");

        due = errors = 0;
        waits = new ManualWaits();
        using (var timer = new LaunchReminderTimer(99, () => ++due, CancellationToken.None,
            waits.Wait, () => ++errors))
        {
            timer.UpdateDelay(30);
            var staleRun = timer.CompletionForChecks;
            timer.UpdateDelay(60);
            var currentRun = timer.CompletionForChecks;
            waits.Calls[0].Completion.SetException(new InvalidOperationException("synthetic stale error"));
            await staleRun;
            Assert(errors == 0, "旧等待异常污染当前计时");
            waits.Calls[1].Completion.SetException(new InvalidOperationException("synthetic current error"));
            await currentRun;
            Assert(errors == 1 && due == 0 && currentRun.Status == TaskStatus.RanToCompletion,
                "当前异常没有只报告一次或泄漏为失败任务");
            timer.UpdateDelay(30);
            var cancelledRun = timer.CompletionForChecks;
            waits.Calls[2].Completion.SetCanceled();
            await cancelledRun;
            Assert(errors == 1 && due == 0, "取消被当成错误或到期");
        }
        checks.Add("PASS 旧异常被隔离、当前异常只报告一次且不泄漏，取消不报错");

        due = errors = 0;
        waits = new ManualWaits();
        LaunchReminderTimer? reentrant = null;
        using (reentrant = new LaunchReminderTimer(111, () =>
        {
            ++due;
            reentrant!.UpdateDelay(60);
            throw new InvalidOperationException("synthetic due callback error");
        }, CancellationToken.None, waits.Wait, () =>
        {
            ++errors;
            throw new InvalidOperationException("synthetic error callback error");
        }))
        {
            reentrant.UpdateDelay(0);
            Assert(waits.Calls[0].Seconds == 30, "非法延时未使用统一默认值");
            waits.Calls[0].Completion.SetResult();
            await reentrant.CompletionForChecks;
            reentrant.UpdateDelay(1);
            Assert(due == 1 && errors == 1 && waits.Calls.Count == 1,
                "到期前未先标记完成，重入重复等待或回调异常泄漏");
        }
        var syncErrors = 0;
        using (var timer = new LaunchReminderTimer(111, () => ++due, CancellationToken.None,
            (_, _, _) => throw new InvalidOperationException("synthetic synchronous wait error"), () => ++syncErrors))
        {
            timer.UpdateDelay(30);
            await timer.CompletionForChecks;
            Assert(syncErrors == 1, "同步等待异常没有被单次捕获");
        }
        checks.Add("PASS 到期先标记完成阻止重入，统一规范延时，同步和用户回调异常均受控");
        return checks.AsReadOnly();
    }

    private sealed class ManualWaits
    {
        internal List<WaitCall> Calls { get; } = new();
        internal Task Wait(long startedAt, CancellationToken token, int seconds)
        {
            var call = new WaitCall(startedAt, token, seconds);
            Calls.Add(call);
            return call.Completion.Task;
        }
    }

    private sealed record WaitCall(long StartedAt, CancellationToken Token, int Seconds)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
