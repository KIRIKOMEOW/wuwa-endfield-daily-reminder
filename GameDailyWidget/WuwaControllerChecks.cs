using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wuwa.Core;

namespace GameDailyWidget;

// Deterministic integration checks use synthetic identities and an in-memory
// adapter. No launcher cache, personal settings or account endpoint is accessed.
public static class WuwaControllerChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        await CheckPreviewAsync(); results.Add("PASS 鸣潮预览不读取授权或个人偏好");
        await CheckDemoAsync(); results.Add("PASS 鸣潮演示两账号与失效账号切换");
        await CheckSwitchClearsAsync(); results.Add("PASS 鸣潮切号立即清空旧数值");
        await CheckOldResponseAsync(); results.Add("PASS 鸣潮旧响应不覆盖新账号");
        await CheckFailureRetainsAsync(); results.Add("PASS 鸣潮同账号失败保留旧值并标过期");
        await CheckAuthorizationClearsAsync(); results.Add("PASS 鸣潮授权变化清空身份和数值");
        await CheckMismatchedUidAsync(); results.Add("PASS 鸣潮响应 UID 不一致停止展示");
        await CheckRescanClearsAsync(); results.Add("PASS 鸣潮重新扫描立即清空旧身份和数据");
        await CheckLogoutCancellationAsync(); results.Add("PASS 鸣潮组件取消选择不会被旧响应恢复");
        await CheckDisconnectedStartupAsync(); results.Add("PASS 鸣潮取消选择哨兵在重启后保持空态");
        await CheckDisposeAsync(); results.Add("PASS 鸣潮关闭取消请求且停止后续读取");
        await CheckMissingFieldsAsync(); results.Add("PASS 鸣潮缺失字段保持未知而非零");
        await CheckDetailsAsync(); results.Add("PASS 鸣潮详情保留真实单质回满时间与电台字段");
        await CheckIntervalsAsync(); results.Add("PASS 鸣潮刷新间隔独立且不触发额外登录");
        await CheckAutomaticSuppressionAsync(); results.Add("PASS 鸣潮普通自动入口在重置窗口抑制、退出恢复且手动刷新仍可用");
        await CheckScheduledWhileManualBusyAsync(); results.Add("PASS 鸣潮忙碌手动刷新遇调度不会重发或取消当前请求");
        await CheckScheduledWhileScanningAsync(); results.Add("PASS 鸣潮重新扫描期间调度不会取消授权确认或读取旧账号");
        await CheckAgeAsync(); results.Add("PASS 鸣潮沿用刷新间隔加一分钟的过期判断");
        return results.AsReadOnly();
    }

    private static async Task CheckPreviewAsync()
    {
        // A null Wuwa preference proves preview never reads settings.Wuwa.
        var settings = new UnifiedSettings { Wuwa = null! };
        var spy = new FakeAdapter();
        using var controller = new WuwaController(settings, preview: true, demo: false, spy);
        await controller.InitializeAsync();
        await controller.LoginAsync(null);
        await controller.RefreshAsync();
        controller.SetRefreshMinutes(1);
        controller.Logout();
        Assert(spy.DiscoverCalls == 0 && spy.QueryCalls == 0, "预览访问了授权适配器。");
        Assert(settings.Wuwa is null, "预览修改了个人偏好对象。");
        Assert(controller.State.Stamina is null && controller.State.Accounts.Count == 0, "空预览出现了账号数据。");
    }

    private static async Task CheckDemoAsync()
    {
        var settings = new UnifiedSettings { Wuwa = null! };
        var spy = new FakeAdapter();
        using var controller = new WuwaController(settings, preview: false, demo: true, spy);
        await controller.InitializeAsync();
        Assert(controller.State.Demo && controller.State.Accounts.Count == 3, "演示账号数量不正确。");
        await controller.SelectAccountAsync("demo-wuwa-b");
        Assert(controller.State.DailyState == "complete" && controller.State.Stamina == 240, "第二演示账号数据不正确。");
        await controller.SelectAccountAsync("demo-wuwa-expired");
        Assert(controller.State.Stamina is null && controller.State.CopyIdentity is null &&
            !controller.State.LoggedIn && controller.State.Error is not null, "失效演示账号残留了旧值。");
        Assert(spy.DiscoverCalls == 0 && spy.QueryCalls == 0 && settings.Wuwa is null, "演示接触了真实授权或偏好。");
    }

    private static async Task CheckSwitchClearsAsync()
    {
        var fake = new FakeAdapter();
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        var pending = Completion<RoleSnapshot>();
        fake.EnqueueQuery("b", _ => pending.Task);
        var switching = controller.SelectAccountAsync("b");
        Assert(controller.State.Stamina is null && controller.State.Daily is null &&
            controller.State.UpdatedAt is null && controller.State.CopyIdentity == B.Identity!.Uid,
            "切号期间旧指标或旧身份未清除。");
        pending.SetResult(Snapshot(B, energy: 70));
        await switching;
        Assert(controller.State.Stamina == 70 && controller.State.SelectedAccountKey == "b", "新账号未正确显示。");
    }

    private static async Task CheckOldResponseAsync()
    {
        var settings = new UnifiedSettings();
        var fake = new FakeAdapter();
        using var controller = NewController(fake, settings);
        await controller.InitializeAsync();
        var old = Completion<RoleSnapshot>();
        fake.EnqueueQuery("a", _ => old.Task); // Intentionally ignores cancellation.
        var oldRefresh = controller.RefreshAsync();
        var oldToken = fake.LastQueryToken;
        var newer = Completion<RoleSnapshot>();
        fake.EnqueueQuery("b", _ => newer.Task);
        var switching = controller.SelectAccountAsync("b");
        Assert(oldToken.IsCancellationRequested, "切号没有取消旧请求。");
        var switchedToken = fake.LastQueryToken;
        var switchedCalls = fake.QueryCalls;
        await controller.RefreshAsync();
        await controller.RefreshAutomaticallyAsync();
        Assert(fake.QueryCalls == switchedCalls && !switchedToken.IsCancellationRequested && controller.State.Busy,
            "切号中调度取消或重发了新账号请求。");
        newer.SetResult(Snapshot(B, energy: 70));
        await switching;
        old.SetResult(Snapshot(A, energy: 199));
        await oldRefresh;
        Assert(controller.State.Stamina == 70 && controller.State.CopyIdentity == B.Identity!.Uid &&
            settings.Wuwa.AccountKey == "b", "旧响应覆盖了新账号状态或偏好。");
    }

    private static async Task CheckFailureRetainsAsync()
    {
        var fake = new FakeAdapter();
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        var previous = controller.State;
        fake.EnqueueQuery("a", _ => Task.FromException<RoleSnapshot>(
            new LauncherException("network_error", "模拟网络失败")));
        await controller.RefreshAsync();
        Assert(controller.State.Stamina == previous.Stamina && controller.State.UpdatedAt == previous.UpdatedAt &&
            controller.State.LoggedIn && controller.State.Stale && controller.State.Error == "模拟网络失败" &&
            !controller.State.Busy, "同账号失败未保留旧值并注明失败。");
    }

    private static async Task CheckAuthorizationClearsAsync()
    {
        var settings = new UnifiedSettings();
        var fake = new FakeAdapter();
        using var controller = NewController(fake, settings);
        await controller.InitializeAsync();
        fake.EnqueueQuery("a", _ => Task.FromException<RoleSnapshot>(
            new LauncherException("account_changed", "模拟授权变化")));
        await controller.RefreshAsync();
        AssertCleared(controller.State, "授权变化");
        Assert(settings.Wuwa.AccountKey is null && controller.State.Error is not null, "授权变化未清除选择。");
    }

    private static async Task CheckMismatchedUidAsync()
    {
        var fake = new FakeAdapter();
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        fake.EnqueueQuery("a", _ => Task.FromResult(Snapshot(B, energy: 77)));
        await controller.RefreshAsync();
        AssertCleared(controller.State, "响应 UID 不一致");
    }

    private static async Task CheckRescanClearsAsync()
    {
        var fake = new FakeAdapter();
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        var discovery = Completion<IReadOnlyList<AccountOption>>();
        fake.Discovery = _ => discovery.Task;
        var scanning = controller.LoginAsync(null);
        Assert(controller.State.Stamina is null && controller.State.CopyIdentity is null &&
            controller.State.Accounts.Count == 0 && controller.State.Busy, "重新扫描时旧身份和数值未立即清除。");
        discovery.SetResult(new[] { B });
        await scanning;
        Assert(controller.State.CopyIdentity == B.Identity!.Uid && controller.State.SelectedAccountKey == "b",
            "重新扫描未使用新发现的账号。");
    }

    private static async Task CheckLogoutCancellationAsync()
    {
        var settings = new UnifiedSettings();
        var fake = new FakeAdapter();
        using var controller = NewController(fake, settings);
        await controller.InitializeAsync();
        var old = Completion<RoleSnapshot>();
        fake.EnqueueQuery("a", _ => old.Task);
        var refreshing = controller.RefreshAsync();
        var token = fake.LastQueryToken;
        controller.Logout();
        AssertCleared(controller.State, "组件取消选择");
        Assert(token.IsCancellationRequested && settings.Wuwa.AccountKey == "", "取消选择没有取消请求或保存空串哨兵。");
        old.SetResult(Snapshot(A, energy: 198));
        await refreshing;
        AssertCleared(controller.State, "旧响应晚到");
        var calls = fake.QueryCalls;
        await controller.RefreshAsync();
        Assert(fake.QueryCalls == calls, "取消选择后仍自动读取账号。");
    }

    private static async Task CheckDisconnectedStartupAsync()
    {
        var settings = new UnifiedSettings();
        settings.Wuwa.AccountKey = "";
        var fake = new FakeAdapter();
        using var controller = NewController(fake, settings);
        await controller.InitializeAsync();
        Assert(fake.DiscoverCalls == 0 && fake.QueryCalls == 0 && !controller.State.LoggedIn,
            "持久取消选择后初始化仍读取了账号。");
        await controller.LoginAsync(null);
        Assert(fake.DiscoverCalls == 1 && controller.State.LoggedIn, "显式重新扫描没有恢复组件选择。");
    }

    private static async Task CheckDisposeAsync()
    {
        var fake = new FakeAdapter();
        var controller = NewController(fake);
        await controller.InitializeAsync();
        var original = controller.State.Stamina;
        var old = Completion<RoleSnapshot>();
        fake.EnqueueQuery("a", _ => old.Task);
        var refresh = controller.RefreshAsync();
        var token = fake.LastQueryToken;
        controller.Dispose();
        Assert(token.IsCancellationRequested && fake.Disposed, "关闭未取消请求或释放适配器。");
        old.SetResult(Snapshot(A, energy: 196));
        await refresh;
        Assert(controller.State.Stamina == original && !controller.State.Busy, "关闭后旧响应仍改变了快照。");
        var queryCalls = fake.QueryCalls;
        var discoveryCalls = fake.DiscoverCalls;
        await controller.InitializeAsync();
        await controller.RefreshAsync();
        await controller.LoginAsync(null);
        await controller.SelectAccountAsync("b");
        Assert(fake.QueryCalls == queryCalls && fake.DiscoverCalls == discoveryCalls, "已关闭的组件仍读取账号。");
    }

    private static async Task CheckMissingFieldsAsync()
    {
        var fake = new FakeAdapter();
        fake.EnqueueQuery("a", _ => Task.FromResult(new RoleSnapshot
        {
            Identity = A.Identity!, FetchedAtUtc = DateTimeOffset.UtcNow, MaxEnergy = 240,
            LivenessMaxCount = 100, DailyState = "unknown", MissingFields = new[] { "Energy", "Liveness" }
        }));
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        Assert(controller.State.Stamina is null && controller.State.Daily is null &&
            controller.State.DailyState == "unknown", "缺失字段被填为零或错误判定完成。");
    }

    private static async Task CheckDetailsAsync()
    {
        var snapshot = Snapshot(A);
        var fake = new FakeAdapter();
        fake.EnqueueQuery("a", _ => Task.FromResult(snapshot));
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        var storeRecovery = controller.State.Details.Single(line => line.Label == "单质回满");
        Assert(storeRecovery.FullAt == snapshot.StoreEnergyFullAtUtc, "单质详情丢失服务器回满时间。");
        var store = controller.State.Details.Single(line => line.Label == "结晶单质");
        var experience = controller.State.Details.Single(line => line.Label == "电台本周经验");
        Assert(store.Current == 15 && store.Maximum == 480 && experience.Current == 8800 && experience.Maximum == 12000,
            "单质或电台详情进度值不正确。");
        Assert(controller.State.Details.Any(line => line.Label == "先约电台" && line.Value.Contains("25", StringComparison.Ordinal)) &&
            controller.State.Details.Any(line => line.Label == "电台本周经验" && line.Value == "8800 / 12000"),
            "电台详情没有使用真实字段。");
    }

    private static async Task CheckIntervalsAsync()
    {
        var settings = new UnifiedSettings();
        var fake = new FakeAdapter();
        using var controller = NewController(fake, settings);
        await controller.InitializeAsync();
        var requests = fake.QueryCalls + fake.DiscoverCalls;
        controller.SetRefreshMinutes(1);
        Assert(controller.State.RefreshMinutes == 1 && settings.Wuwa.RefreshMinutes == 1, "一分钟设置不一致。");
        controller.SetRefreshMinutes(10);
        Assert(controller.State.RefreshMinutes == 10, "十分钟设置不一致。");
        controller.SetRefreshMinutes(3);
        Assert(controller.State.RefreshMinutes == 5 && fake.QueryCalls + fake.DiscoverCalls == requests,
            "间隔更新触发额外读取或没有回退到五分钟。");
    }

    private static async Task CheckAutomaticSuppressionAsync()
    {
        var settings = new UnifiedSettings();
        settings.Wuwa.RefreshMinutes = 10;
        var fake = new FakeAdapter();
        using var controller = NewController(fake, settings);
        await controller.InitializeAsync();
        var suppress = true;
        controller.AutomaticRefreshSuppressed = () => suppress;
        var queries = fake.QueryCalls;
        var discoveries = fake.DiscoverCalls;
        await controller.RefreshAutomaticallyAsync();
        await controller.RefreshAutomaticallyAsync();
        Assert(fake.QueryCalls == queries && fake.DiscoverCalls == discoveries && !controller.State.Busy,
            "重置窗口中的普通自动入口仍然发起了读取。");
        await controller.RefreshAsync();
        Assert(fake.QueryCalls == queries + 1, "普通自动入口抑制也阻止了手动或快速调度刷新。");
        suppress = false;
        await controller.RefreshAutomaticallyAsync();
        Assert(fake.QueryCalls == queries + 2 && fake.DiscoverCalls == discoveries &&
            settings.Wuwa.RefreshMinutes == 10 && controller.State.RefreshMinutes == 10,
            "离开重置窗口后没有恢复普通自动读取或修改了分钟偏好。");
    }

    private static async Task CheckScheduledWhileManualBusyAsync()
    {
        var fake = new FakeAdapter();
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        var pending = Completion<RoleSnapshot>();
        fake.EnqueueQuery("a", _ => pending.Task); // Ignores cancellation to expose any accidental replacement.
        var manual = controller.RefreshAsync();
        var token = fake.LastQueryToken;
        var queries = fake.QueryCalls;
        for (var slot = 0; slot < 4; slot++) await controller.RefreshAsync();
        await controller.RefreshAutomaticallyAsync();
        Assert(fake.QueryCalls == queries && !token.IsCancellationRequested && controller.State.Busy,
            "调度在手动刷新忙碌时重发请求或取消了原请求。");
        pending.SetResult(Snapshot(A, energy: 31));
        await manual;
        Assert(fake.QueryCalls == queries && controller.State.Stamina == 31 && !controller.State.Busy,
            "忙碌期间跳过的调度在响应后补发或使原响应丢失。");
    }

    private static async Task CheckScheduledWhileScanningAsync()
    {
        var fake = new FakeAdapter();
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        var pending = Completion<IReadOnlyList<AccountOption>>();
        var discoveryToken = CancellationToken.None;
        fake.Discovery = token => { discoveryToken = token; return pending.Task; };
        var scanning = controller.LoginAsync(null);
        var queries = fake.QueryCalls;
        var discoveries = fake.DiscoverCalls;
        for (var slot = 0; slot < 4; slot++) await controller.RefreshAsync();
        await controller.RefreshAutomaticallyAsync();
        Assert(!discoveryToken.IsCancellationRequested && controller.State.Busy &&
            fake.QueryCalls == queries && fake.DiscoverCalls == discoveries && controller.State.CopyIdentity is null,
            "扫描期间调度重发、取消了扫描或恢复了旧账号。");
        pending.SetResult(new[] { B });
        await scanning;
        Assert(fake.QueryCalls == queries + 1 && controller.State.SelectedAccountKey == "b" &&
            controller.State.CopyIdentity == B.Identity!.Uid && !controller.State.Busy,
            "扫描后没有只更新确认的新账号或存在调度补发。");
    }

    private static async Task CheckAgeAsync()
    {
        var fake = new FakeAdapter();
        fake.EnqueueQuery("a", _ => Task.FromResult(new RoleSnapshot
        {
            Identity = A.Identity!, FetchedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-7),
            Energy = 10, MaxEnergy = 240, Liveness = 20, LivenessMaxCount = 100,
            LivenessUnlock = true, DailyState = "incomplete"
        }));
        using var controller = NewController(fake);
        await controller.InitializeAsync();
        Assert(controller.State.Stale && controller.State.Status.Contains("可能过期", StringComparison.Ordinal),
            "超过刷新间隔加一分钟的快照未标记过期。");
    }

    private static void AssertCleared(GameViewState state, string operation) => Assert(
        state.Stamina is null && state.Daily is null && state.CopyIdentity is null && !state.LoggedIn &&
        state.UpdatedAt is null && state.Details.Count == 0, operation + "后残留旧身份或指标。");

    private static WuwaController NewController(FakeAdapter fake, UnifiedSettings? settings = null) =>
        new(settings ?? new UnifiedSettings(), preview: false, demo: false, fake);

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static readonly AccountOption A = new()
    {
        Handle = new("a", 1, 1, 1), Identity = new("100000011", "合成账号甲", 80)
    };
    private static readonly AccountOption B = new()
    {
        Handle = new("b", 2, 1, 2), Identity = new("100000012", "合成账号乙", 60)
    };

    private static RoleSnapshot Snapshot(AccountOption option, int energy = 10) => new()
    {
        Identity = option.Identity!, FetchedAtUtc = DateTimeOffset.UtcNow,
        Energy = energy, MaxEnergy = 240, StoreEnergy = 15, MaxStoreEnergy = 480,
        EnergyFullAtUtc = DateTimeOffset.UtcNow.AddHours(5), StoreEnergyFullAtUtc = DateTimeOffset.UtcNow.AddHours(93),
        Liveness = 20, LivenessMaxCount = 100, LivenessUnlock = true, DailyState = "incomplete",
        BattlePassLevel = 25, WeekExp = 8800, WeekMaxExp = 12000, WeeklyInstCount = 2
    };

    private sealed class FakeAdapter : IWuwaAdapter
    {
        private readonly Dictionary<string, Queue<Func<CancellationToken, Task<RoleSnapshot>>>> _queries = new();
        public Func<CancellationToken, Task<IReadOnlyList<AccountOption>>> Discovery { get; set; } =
            _ => Task.FromResult<IReadOnlyList<AccountOption>>(new[] { A, B });
        public int DiscoverCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public CancellationToken LastQueryToken { get; private set; }
        public bool Disposed { get; private set; }

        public void EnqueueQuery(string key, Func<CancellationToken, Task<RoleSnapshot>> query)
        {
            if (!_queries.TryGetValue(key, out var queue)) _queries[key] = queue = new();
            queue.Enqueue(query);
        }

        public Task<IReadOnlyList<AccountOption>> DiscoverAccountsAsync(CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            return Discovery(cancellationToken);
        }

        public Task<RoleSnapshot> QueryAsync(AccountHandle account, CancellationToken cancellationToken)
        {
            QueryCalls++;
            LastQueryToken = cancellationToken;
            if (_queries.TryGetValue(account.Key, out var queue) && queue.Count > 0)
                return queue.Dequeue()(cancellationToken);
            return Task.FromResult(Snapshot(account.Key == "a" ? A : B));
        }

        public void Dispose() => Disposed = true;
    }
}
