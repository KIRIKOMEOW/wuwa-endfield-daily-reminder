using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EndfieldDesktopWidget;

namespace GameDailyWidget;

/// <summary>
/// Isolated real-controller checks using an injected clock and in-memory services.
/// These demonstrate client polling latency, not the live Skland backend's latency.
/// No real credentials, personal settings, network, Dispatcher timers or PC clock changes.
/// </summary>
public static class EndfieldResetWindowChecks
{
    private static readonly DateTimeOffset Boundary = new(2026, 10, 7, 4, 0, 0, TimeSpan.FromHours(8));

    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        await Check("终末地接口瞬时换日仍被五分钟轮询延迟至04:04", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            Assert(f.Controller.State.Daily == 100 && f.Controller.State.DailyState == "complete", "03:59未读取昨日达标快照");
            Assert(f.Controller.State.RefreshMinutes == 5, "基线不是默认五分钟");
            var previousFetch = f.Controller.State.UpdatedAt;
            foreach (var time in new[] { Boundary, Boundary.AddMinutes(1), Boundary.AddMinutes(3).AddSeconds(59) })
            {
                f.Now = time;
                Assert(f.BackendDaily == 0, "模拟接口没有在04:00立即更新");
                f.Controller.UpdateStaleState();
                Assert(f.Client.SnapshotQueries.Count == 1 && f.Controller.State.Daily == 100
                    && f.Controller.State.DailyState == "complete" && f.Controller.State.UpdatedAt == previousFetch,
                    "没有请求却变化了缓存或查询次数");
                Assert(!f.Controller.State.Stale, "换日被错误混同为十五分钟过期");
            }
            var nextPoll = f.Start.AddMinutes(f.Controller.State.RefreshMinutes);
            Assert(nextPoll == Boundary.AddMinutes(4), "五分钟基线时间错误");
            await f.PollAt(nextPoll);
            Assert(f.Controller.State.Daily == 0 && f.Controller.State.DailyState == "incomplete"
                && f.Controller.State.UpdatedAt == nextPoll && f.Client.SnapshotQueries.Count == 2,
                "下一次轮询未取得新日快照");
            Assert(nextPoll - Boundary == TimeSpan.FromMinutes(4), "客户端可见延迟错误");
            f.AssertOnlyInjectedServices();
        });
        await Check("终末地边界即刻查询消除接口零延迟下的客户端等待", async () =>
        {
            using var baseline = new Fixture();
            using var immediate = new Fixture();
            await baseline.Controller.InitializeAsync();
            await immediate.Controller.InitializeAsync();
            baseline.Now = Boundary;
            baseline.Controller.UpdateStaleState();
            await immediate.PollAt(Boundary);
            Assert(baseline.BackendDaily == 0 && immediate.BackendDaily == 0, "对照服务端不是零延迟");
            Assert(baseline.Controller.State.Daily == 100 && baseline.Client.SnapshotQueries.Count == 1,
                "五分钟基线提前查询");
            Assert(immediate.Controller.State.Daily == 0 && immediate.Controller.State.DailyState == "incomplete"
                && immediate.Controller.State.UpdatedAt == Boundary && immediate.Client.SnapshotQueries.Count == 2,
                "04点即刻查询未消除客户端等待");
            baseline.AssertOnlyInjectedServices();
            immediate.AssertOnlyInjectedServices();
        });
        await Check("终末地秒级过期检查只处理缓存年龄不会查询换日进度", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            var fetch = f.Controller.State.UpdatedAt;
            for (var seconds = 0; seconds <= 60; seconds++)
            {
                f.Now = Boundary.AddSeconds(seconds);
                f.Controller.UpdateStaleState();
            }
            Assert(f.Client.SnapshotQueries.Count == 1 && f.Controller.State.UpdatedAt == fetch
                && f.Controller.State.Daily == 100 && !f.Controller.State.Stale,
                "秒级时钟被误认为刷新数据");
            f.AssertOnlyInjectedServices();
        });
        await Check("终末地零延迟模拟接口在04点单次查询即可取得新日", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            foreach (var seconds in new[] { 0, 20, 40, 60 })
            {
                await f.PollAt(Boundary.AddSeconds(seconds));
                Assert(f.Controller.State.Daily == 0 && f.Controller.State.DailyState == "incomplete"
                    && f.Controller.State.UpdatedAt == f.Now, "零延迟模拟服务端没有立即提供新日");
            }
            Assert(f.Client.SnapshotQueries.Count == 5, "换日四次查询数量不正确");
            f.AssertOnlyInjectedServices();
        });
        await Check("终末地假设40秒后端延迟仅验证20秒重试容错而非真实延迟", async () =>
        {
            using var f = new Fixture(backendDelay: TimeSpan.FromSeconds(40));
            await f.Controller.InitializeAsync();
            foreach (var seconds in new[] { 0, 20, 40, 60 })
            {
                await f.PollAt(Boundary.AddSeconds(seconds));
                var expected = seconds < 40 ? 100 : 0;
                Assert(f.Controller.State.Daily == expected && f.Controller.State.UpdatedAt == f.Now,
                    "假设延迟下的刷新没有读取当时模拟服务端值");
            }
            Assert(f.Client.SnapshotQueries.Count == 5 && f.Controller.State.DailyState == "incomplete",
                "重试未最终取得模拟新日");
            f.AssertOnlyInjectedServices();
        });
        await Check("终末地真实BoundaryOnly调度秒tick只查询一次且恢复五分钟轮询", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            var schedule = new DailyResetRefreshSchedule(DailyResetRefreshMode.BoundaryOnly);
            var scheduledQueries = new List<DateTimeOffset>();
            for (var seconds = 0; seconds <= 120; seconds++)
            {
                f.Now = f.Start.AddSeconds(seconds);
                if (!schedule.TryTake(f.Now)) continue;
                scheduledQueries.Add(f.Now);
                await f.Controller.RefreshAsync();
                Assert(f.Controller.State.Daily == 0 && f.Controller.State.DailyState == "incomplete"
                    && f.Controller.State.UpdatedAt == f.Now, "边界查询没有显示模拟接口的新日数据");
            }
            Assert(f.Now == Boundary.AddMinutes(1) && scheduledQueries.Count == 1 && scheduledQueries[0] == Boundary,
                "BoundaryOnly没有在04:00只领取一次时隙");
            Assert(f.Client.SnapshotQueries.Count == 2 && f.Controller.State.UpdatedAt == Boundary
                && f.Controller.State.RefreshMinutes == 5, "一分钟内发生重复请求或改变了默认间隔");

            // Dispatcher timers stay disabled. Explicitly exercise the unchanged
            // ordinary five-minute tick at its original 03:59 + 5 min due time.
            var nextOrdinaryPoll = f.Start.AddMinutes(f.Controller.State.RefreshMinutes);
            f.Now = nextOrdinaryPoll;
            Assert(!schedule.TryTake(f.Now), "边界调度在普通轮询时刻再次发请求");
            await f.Controller.RefreshAsync();
            Assert(nextOrdinaryPoll == Boundary.AddMinutes(4) && f.Client.SnapshotQueries.Count == 3
                && f.Controller.State.UpdatedAt == nextOrdinaryPoll && f.Controller.State.Daily == 0
                && f.Controller.State.RefreshMinutes == 5, "边界之后普通五分钟轮询没有继续更新");
            f.AssertOnlyInjectedServices();
        });
        return results;

        async Task Check(string name, Func<Task> test)
        {
            try { await test(); results.Add("PASS " + name); }
            catch (Exception error) { results.Add("FAIL " + name + "：" + error.Message); }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public DateTimeOffset Start { get; } = Boundary.AddMinutes(-1);
        public DateTimeOffset Now { get; set; } = Boundary.AddMinutes(-1);
        public int BackendDaily => Now < Boundary + _backendDelay ? 100 : 0;
        public FakeClient Client { get; }
        public MemoryTokenStore Store { get; } = new();
        public EndfieldController Controller { get; }
        private readonly TimeSpan _backendDelay;
        private int _settingsSaves;

        public Fixture(TimeSpan? backendDelay = null)
        {
            _backendDelay = backendDelay ?? TimeSpan.Zero;
            Client = new FakeClient(() => Now, () => BackendDaily);
            // Construct preferences in memory. Never invoke UnifiedSettings.Load/Save.
            var settings = new UnifiedSettings { Endfield = new EndfieldPreferences { RefreshMinutes = 5 } };
            Controller = new EndfieldController(settings, preview: false, demo: false, Client, Store,
                login: (_, _) => throw new InvalidOperationException("隔离测试不应打开登录窗口"),
                now: () => Now, saveSettings: () => _settingsSaves++, startTimers: false);
        }

        public async Task PollAt(DateTimeOffset time)
        {
            Now = time;
            await Controller.RefreshAsync();
        }

        public void AssertOnlyInjectedServices() => Assert(Store.LoadCalls == 1 && Store.SaveCalls == 1
            && Client.LoginCalls == 1 && Client.RolesCalls == 1 && _settingsSaves == 1
            && !Controller.State.Demo && Controller.State.LoggedIn && !Controller.State.Busy && Controller.State.Error == null,
            "模拟登录/设置注入次数或控制器状态不符合预期");

        public void Dispose() => Controller.Dispose();
    }

    private sealed class MemoryTokenStore : IEndfieldTokenStore
    {
        public const string Token = "ISOLATED-NOT-A-REAL-ACCOUNT-TOKEN";
        public int LoadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public string? Load() { LoadCalls++; return Token; }
        public void Save(string? token)
        {
            Assert(token == Token, "模拟存储收到非预期凭据");
            SaveCalls++;
        }
    }

    private sealed class FakeClient(Func<DateTimeOffset> now, Func<int> daily) : IEndfieldDataClient
    {
        private readonly EndfieldRole _role = new("ISOLATED-ROLE", "ISOLATED-SERVER", "ISOLATED-USER", "隔离测试角色", "隔离测试服务器");
        private readonly SklandCredential _credential = new("ISOLATED-NOT-REAL-CRED", "ISOLATED-NOT-REAL-SIGNING-TOKEN");
        public int LoginCalls { get; private set; }
        public int RolesCalls { get; private set; }
        public List<DateTimeOffset> SnapshotQueries { get; } = new();
        public Task<SklandCredential> LoginAsync(string token, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert(token == MemoryTokenStore.Token, "模拟服务收到非模拟账号凭据");
            LoginCalls++;
            return Task.FromResult(_credential);
        }
        public Task<IReadOnlyList<EndfieldRole>> GetRolesAsync(SklandCredential credential, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert(credential == _credential, "模拟角色查询收到非模拟凭据");
            RolesCalls++;
            return Task.FromResult<IReadOnlyList<EndfieldRole>>(new[] { _role });
        }
        public Task<GameSnapshot> GetSnapshotAsync(SklandCredential credential, EndfieldRole role, Action<JsonElement> observeDetail, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert(credential == _credential && role == _role, "模拟快照查询收到非模拟身份");
            var fetchedAt = now();
            SnapshotQueries.Add(fetchedAt);
            return Task.FromResult(new GameSnapshot(206, 360, fetchedAt.AddHours(10), daily(), 100,
                0, 10, 1, null, role.Nickname, fetchedAt));
        }
        public void Dispose() { }
    }
}
