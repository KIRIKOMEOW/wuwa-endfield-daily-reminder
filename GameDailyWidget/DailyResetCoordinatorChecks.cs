using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace GameDailyWidget;

internal static class DailyResetCoordinatorChecks
{
    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        var boundary = new DateTimeOffset(2026, 10, 7, 4, 0, 0, TimeSpan.FromHours(8));
        var now = boundary;
        using var wuwa = new FakeController("wuwa");
        using var endfield = new FakeController("endfield");
        using (var host = new DailyResetRefreshCoordinator(wuwa, endfield, false, () => now, false))
        {
            for (var seconds = 0; seconds <= 120; seconds++) { now = boundary.AddSeconds(seconds); host.Tick(); }
            Assert(wuwa.Calls == 4 && endfield.Calls == 1, "每秒tick未产生鸣潮四次/终末地一次刷新");
            Assert(wuwa.State.RefreshMinutes == 5 && endfield.State.RefreshMinutes == 5, "加速改写了正常间隔");
            results.Add("PASS 真实宿主秒tick：鸣潮00/20/40/60四次、终末地一次，常规偏好仍为5分钟");
        }

        now = boundary;
        using var slow = new FakeController("wuwa");
        using var other = new FakeController("endfield");
        var pending = new TaskCompletionSource();
        slow.Work = () => pending.Task;
        using (var host = new DailyResetRefreshCoordinator(slow, other, false, () => now, false))
        {
            host.Tick();
            now = boundary.AddSeconds(20); host.Tick();
            Assert(slow.Calls == 1 && other.Calls == 1, "慢请求阻塞另一游戏或发生重叠");
            pending.SetResult();
            await Task.Yield();
            slow.Work = null;
            now = boundary.AddSeconds(21); host.Tick();
            Assert(slow.Calls == 1, "忙时跳过的时隙被排队补发");
            now = boundary.AddSeconds(40); host.Tick();
            Assert(slow.Calls == 2, "响应完成改变了04:00:40锚点");
            results.Add("PASS 慢请求不重叠、不排队补发、不移动20秒锚点，另一游戏独立刷新");
        }

        now = boundary;
        using var failing = new FakeController("wuwa") { Work = () => Task.FromException(new InvalidOperationException("synthetic failure")) };
        using var healthy = new FakeController("endfield");
        using (var host = new DailyResetRefreshCoordinator(failing, healthy, false, () => now, false))
        {
            host.Tick(); now = boundary.AddSeconds(20); host.Tick();
            Assert(failing.Calls == 2 && healthy.Calls == 1, "失败跨游戏传播或阻止下一时隙");
            results.Add("PASS 一个游戏请求失败不阻止另一游戏或下一时隙");
        }

        now = boundary;
        using var busy = new FakeController("wuwa") { State = new() { LoggedIn = true, Busy = true } };
        using var loggingIn = new FakeController("endfield") { State = new() { LoggedIn = true, Busy = true } };
        using (var host = new DailyResetRefreshCoordinator(busy, loggingIn, false, () => now, false))
        {
            host.Tick();
            Assert(busy.Calls == 0 && loggingIn.Calls == 0, "换日取消或重发正在进行的登录/手动操作");
            busy.State = busy.State with { Busy = false };
            loggingIn.State = loggingIn.State with { Busy = false };
            now = boundary.AddSeconds(3); host.Tick();
            Assert(busy.Calls == 0 && loggingIn.Calls == 1, "终末地未在忙状态结束后执行单次边界查询");
            now = boundary.AddSeconds(20); host.Tick();
            Assert(busy.Calls == 1 && loggingIn.Calls == 1, "鸣潮未恢复下个锚点或终末地重复请求");
            results.Add("PASS 忙状态不会被打断；终末地一分钟内等待空闲，鸣潮使用下一20秒时隙");
        }

        now = boundary;
        using var anonymous = new FakeController("wuwa") { State = new() { LoggedIn = false } };
        using var demo = new FakeController("endfield") { State = new() { LoggedIn = true, Demo = true } };
        using (var host = new DailyResetRefreshCoordinator(anonymous, demo, false, () => now, false))
        {
            host.Tick();
            Assert(anonymous.Calls == 0 && demo.Calls == 0 && anonymous.LoginCalls == 0 && demo.LoginCalls == 0,
                "换日自动登录或查询演示/未登录状态");
            results.Add("PASS 换日不自动登录、不查询未登录或演示数据");
        }

        now = boundary;
        using var previewWuwa = new FakeController("wuwa");
        using var previewEndfield = new FakeController("endfield");
        using (var host = new DailyResetRefreshCoordinator(previewWuwa, previewEndfield, true, () => now, false)) host.Tick();
        Assert(previewWuwa.Calls == 0 && previewEndfield.Calls == 0, "预览调用数据模块");
        results.Add("PASS 预览完全关闭换日调度");

        now = boundary;
        using var stoppedWuwa = new FakeController("wuwa");
        using var stoppedEndfield = new FakeController("endfield");
        var stopped = new DailyResetRefreshCoordinator(stoppedWuwa, stoppedEndfield, false, () => now, false);
        stopped.Dispose(); stopped.Tick();
        Assert(stoppedWuwa.Calls == 0 && stoppedEndfield.Calls == 0, "退出后仍发起换日查询");
        results.Add("PASS 退出后换日调度停止");
        return results;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class FakeController(string gameKey) : IGameController
    {
        public string GameKey => gameKey;
        public GameViewState State { get; set; } = new() { LoggedIn = true, RefreshMinutes = 5 };
        public event Action? Changed { add { } remove { } }
        public int Calls { get; private set; }
        public int LoginCalls { get; private set; }
        public Func<Task>? Work { get; set; }
        public Task RefreshAsync() { Calls++; return Work?.Invoke() ?? Task.CompletedTask; }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task SelectAccountAsync(string key) => Task.CompletedTask;
        public Task LoginAsync(Window? owner) { LoginCalls++; return Task.CompletedTask; }
        public void Logout() { }
        public void SetRefreshMinutes(int minutes) => State = State with { RefreshMinutes = minutes };
        public void Dispose() { }
    }
}
