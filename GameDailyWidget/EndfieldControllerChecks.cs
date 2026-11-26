using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EndfieldDesktopWidget;

namespace GameDailyWidget;

/// <summary>Deterministic checks: injected clients/stores only; no personal files or network.</summary>
public static class EndfieldControllerChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        await Check("终末地同步失败保留同角色旧快照", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            var previous = f.Controller.State;
            f.Client.Snapshot = (_, _) => Task.FromException<GameSnapshot>(new InvalidOperationException("登录凭证已失效，请重新扫码登录。"));
            await f.Controller.RefreshAsync();
            var state = f.Controller.State;
            Assert(state.Stamina == previous.Stamina && state.UpdatedAt == previous.UpdatedAt && state.CopyIdentity == previous.CopyIdentity, "失败丢失旧快照或身份");
            Assert(f.Controller.EventProgress == null, "失败时仍有可用的玩法读数");
            Assert(state.Status.Contains("已保留上次数据", StringComparison.Ordinal) && state.Error != null && !state.Busy, "旧数据没有明确失败状态");
        });
        await Check("终末地切角色立即清空并隔离忽略取消的旧请求", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            var requests = new List<PendingSnapshot>();
            f.Client.Snapshot = (role, token) => AddPending(requests, role, token);
            var oldRefresh = f.Controller.RefreshAsync();
            var secondKey = f.Controller.State.Accounts[1].Key;
            Assert(f.Controller.EventProgress?.Items.Count == 1 && f.Client.SnapshotCalls == 2, "同一次快照应携带玩法读数");
            var switching = f.Controller.SelectAccountAsync(secondKey);
            Assert(requests.Count == 2 && requests[0].Cancellation.IsCancellationRequested, "切换没有取消上一角色请求");
            var empty = f.Controller.State;
            Assert(empty.SelectedAccountKey == secondKey && empty.Stamina == null && empty.Daily == null && empty.FullAt == null && empty.UpdatedAt == null, "切换后仍显示上一角色数值");
            Assert(empty.CopyIdentity == f.Client.Roles[1].RoleId && empty.Nickname == f.Client.Roles[1].Nickname && empty.Busy, "新身份未立即显示");
            // Intentionally return success despite the cancelled token.
            requests[0].Completion.SetResult(f.Snapshot(requests[0].Role, 999));
            await oldRefresh;
            Assert(f.Controller.EventProgress == null, "旧请求迟到后污染玩法读数");
            Assert(f.Controller.State.Stamina == null && f.Controller.State.Busy && f.Controller.State.SelectedAccountKey == secondKey, "旧请求或 finally 覆盖新角色");
            requests[1].Completion.SetResult(f.Snapshot(requests[1].Role, 222));
            await switching;
            Assert(f.Controller.EventProgress?.RoleKey == secondKey && f.Controller.EventProgress.Items.Count == 1, "切换后读数身份不符");
            Assert(f.Controller.State.Stamina == 222 && !f.Controller.State.Busy && f.Controller.State.SelectedAccountKey == secondKey, "新角色未正确完成");
        });
        await Check("终末地连续刷新隔离旧错误和 finally", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            var requests = new List<PendingSnapshot>();
            f.Client.Snapshot = (role, token) => AddPending(requests, role, token);
            var old = f.Controller.RefreshAsync();
            var current = f.Controller.RefreshAsync();
            Assert(requests[0].Cancellation.IsCancellationRequested, "后续刷新未取消旧请求");
            requests[0].Completion.SetException(new InvalidOperationException("登录凭证已失效，请重新扫码登录。"));
            await old;
            Assert(f.Controller.State.Error == null && f.Controller.State.Busy, "旧请求错误或 finally 污染新请求状态");
            requests[1].Completion.SetResult(f.Snapshot(requests[1].Role, 223));
            await current;
            Assert(f.Controller.State.Stamina == 223 && !f.Controller.State.Busy, "新请求数据未提交");
        });
        await Check("终末地退出清空显示且保留鸣潮设置", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            var requests = new List<PendingSnapshot>();
            f.Client.Snapshot = (role, token) => AddPending(requests, role, token);
            var pending = f.Controller.RefreshAsync();
            f.Controller.Logout();
            Assert(requests[0].Cancellation.IsCancellationRequested, "退出未取消请求");
            AssertEmpty(f.Controller.State);
            Assert(f.Store.SavedTokens.Last() == null, "退出未清除模拟登录存储");
            Assert(f.Settings.Wuwa.AccountKey == "wuwa-sentinel" && f.Settings.Wuwa.RefreshMinutes == 10 && f.Settings.Wuwa.GamePath == "wuwa-sentinel.exe", "终末地退出修改了鸣潮设置");
            Assert(f.Settings.CurrentGame == "wuwa" && f.Settings.Endfield.SelectedRoleKey == null && f.Settings.Endfield.SelectedRoleId == null, "退出错误修改选择设置");
            requests[0].Completion.SetResult(f.Snapshot(requests[0].Role, 999));
            await pending;
            AssertEmpty(f.Controller.State);
        });
        await Check("终末地演示角色可切换且真实登录前先清空", async () =>
        {
            using var f = new Fixture(demo: true);
            await f.Controller.InitializeAsync();
            await f.Controller.SelectAccountAsync(f.Controller.State.Accounts[1].Key);
            Assert(f.Controller.State.Demo && f.Controller.State.DailyState == "complete" && f.Controller.State.Stamina == 360, "演示角色切换不生效");
            Assert(f.Controller.State.Details.Single(item => item.Label == "通行证等级").Hint!.Contains("未提供", StringComparison.Ordinal), "未知通行证上限被编造");
            var loginResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Login = (_, _) =>
            {
                Assert(!f.Controller.State.Demo && f.Controller.State.Stamina == null && f.Controller.State.Daily == null && f.Controller.State.FullAt == null, "真实登录入口仍保留演示值");
                return loginResult.Task;
            };
            var login = f.Controller.LoginAsync(null);
            Assert(f.Controller.State.Busy && f.Controller.State.Accounts.Count == 0, "登录过程保留演示账号");
            loginResult.SetResult(null);
            await login;
            AssertEmpty(f.Controller.State);
            Assert(f.Store.Reads == 0 && f.Store.SavedTokens.Count == 0 && f.Client.NetworkCalls == 0 && f.SettingsSaves == 0, "演示或取消登录触及持久存储/接口");
        });
        await Check("终末地演示可进入真实会话且不读旧授权", async () =>
        {
            using var f = new Fixture(demo: true);
            f.Login = (_, _) => Task.FromResult<string?>("fake-new-account-token");
            await f.Controller.InitializeAsync();
            await f.Controller.LoginAsync(null);
            Assert(f.Controller.State.LoggedIn && !f.Controller.State.Demo && f.Controller.State.Stamina == 206 && !f.Controller.State.Busy, "新登录没有替换演示数据");
            Assert(f.Store.Reads == 0 && f.Store.SavedTokens.Count == 1 && f.Client.NetworkCalls == 3, "真实登录错误读取旧授权或调用次数不符");
        });
        await Check("终末地空态预览和演示预览均零读写零网络", async () =>
        {
            foreach (var demo in new[] { false, true })
            {
                using var f = new Fixture(preview: true, demo: demo);
                await f.Controller.InitializeAsync();
                Assert(f.Controller.State.Demo == demo && (f.Controller.State.Stamina != null) == demo, "preview 强制制造演示数据或丢失演示标签");
                if (demo) await f.Controller.SelectAccountAsync(f.Controller.State.Accounts[1].Key);
                await f.Controller.RefreshAsync();
                f.Controller.SetRefreshMinutes(1);
                await f.Controller.LoginAsync(null);
                f.Controller.Logout();
                AssertEmpty(f.Controller.State);
                Assert(f.Store.Reads == 0 && f.Store.SavedTokens.Count == 0 && f.Client.NetworkCalls == 0 && f.LoginCalls == 0 && f.SettingsSaves == 0, "preview 触及登录/存储/接口");
                Assert(f.Settings.Endfield.SelectedRoleKey == "saved-role-key" && f.Settings.Endfield.SelectedRoleId == "saved-role-id" && f.Settings.Endfield.RefreshMinutes == 10, "preview 修改个人设置");
                f.Controller.Dispose();
                Assert(f.Client.DisposeCalls == 1, "preview 未释放 client");
            }
        });
        await Check("终末地缺失上限/准确回满时间/15分钟过期", async () =>
        {
            using var f = new Fixture();
            var fullAt = f.Now.AddHours(4).AddSeconds(37);
            f.Client.Snapshot = (role, _) => Task.FromResult(new GameSnapshot(71, 360, fullAt, 15, 0, 3, 0, 9, null, role.Nickname, f.Now));
            await f.Controller.InitializeAsync();
            var state = f.Controller.State;
            Assert(state.MaxDaily == null && state.DailyState == "unknown", "未知每日上限被推断为完成");
            Assert(state.FullAt == fullAt && state.Details.Single(item => item.Label == "预计回满时间").FullAt == fullAt, "回满时间被推算或丢失精度");
            Assert(state.Details.Single(item => item.Label == "通行证等级").Value == "Lv. 9", "通行证上限被写死");
            Assert(state.Details.Single(item => item.Label == "通行证等级").Current == 9 && state.Details.Single(item => item.Label == "通行证等级").Maximum == null, "未知通行证上限产生了伪造进度条");
            Assert(state.Details.Single(item => item.Label == "每周事务").Current == 3 && state.Details.Single(item => item.Label == "每周事务").Maximum == null, "未知每周上限产生了伪造进度条");
            Assert(state.CopyIdentity == f.Client.Roles[0].RoleId && !state.IdentityLabel.Contains(f.Client.Roles[0].UserId, StringComparison.Ordinal), "森空岛用户ID被混作游戏身份");
            Assert(state.Accounts.Select(item => item.Key).Distinct().Count() == 2, "不同服务器的同角色ID键冲突");
            var fetchedAt = state.UpdatedAt;
            var calls = f.Client.NetworkCalls;
            var changes = 0;
            f.Controller.Changed += () => changes++;
            f.Now = f.Now.AddMinutes(16);
            f.Controller.UpdateStaleState();
            f.Controller.UpdateStaleState();
            Assert(f.Controller.State.Stale && f.Controller.State.Status.Contains("过期", StringComparison.Ordinal) && f.Controller.State.UpdatedAt == fetchedAt, "旧数据未标记过期");
            Assert(f.Client.NetworkCalls == calls && changes == 1, "时钟进行了网络请求或重复发布未变化状态");
        });
        await Check("终末地 Dispose 取消登录并阻止迟到结果", async () =>
        {
            using var f = new Fixture(demo: true);
            var loginResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken loginCancellation = default;
            f.Login = (_, token) => { loginCancellation = token; return loginResult.Task; };
            var login = f.Controller.LoginAsync(null);
            f.Controller.Dispose();
            Assert(loginCancellation.IsCancellationRequested && f.Client.DisposeCalls == 1, "Dispose 未取消登录或释放 client");
            loginResult.SetResult("fake-late-token");
            await login;
            AssertEmpty(f.Controller.State);
            Assert(f.Client.NetworkCalls == 0 && f.Store.SavedTokens.Count == 0, "Dispose 后迟到登录启动真实操作");
        });
        await Check("终末地退出登录窗口即时清空且旧 finally 不覆盖新登录", async () =>
        {
            using var f = new Fixture(demo: true);
            var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            CancellationToken oldCancellation = default;
            f.Login = (_, token) =>
            {
                if (++calls == 1) { oldCancellation = token; return first.Task; }
                return second.Task;
            };
            var oldLogin = f.Controller.LoginAsync(null);
            f.Controller.Logout();
            Assert(oldCancellation.IsCancellationRequested, "退出没有取消登录窗口会话");
            AssertEmpty(f.Controller.State);
            var newLogin = f.Controller.LoginAsync(null);
            Assert(calls == 2 && f.Controller.State.Busy, "退出后不能打开新登录");
            first.SetResult("fake-ignored-old-token");
            await oldLogin;
            Assert(f.Controller.State.Busy && f.Client.NetworkCalls == 0, "旧登录 finally 覆盖新登录状态");
            second.SetResult(null);
            await newLogin;
            AssertEmpty(f.Controller.State);
        });
        await Check("终末地退出隔离未完成的账号授权", async () =>
        {
            using var f = new Fixture();
            var credential = new TaskCompletionSource<SklandCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken cancellation = default;
            f.Client.Login = token => { cancellation = token; return credential.Task; };
            var initializing = f.Controller.InitializeAsync();
            Assert(f.Controller.State.Busy, "授权未显示 busy");
            f.Controller.Logout();
            Assert(cancellation.IsCancellationRequested, "退出未取消账号授权");
            credential.SetResult(new SklandCredential("fake-cred", "fake-sign-token"));
            await initializing;
            AssertEmpty(f.Controller.State);
            Assert(f.Client.RolesCalls == 0 && f.Client.SnapshotCalls == 0 && f.Store.SavedTokens.All(item => item == null), "迟到授权结果继续读取或保存账号");
        });
        await Check("终末地错误详情不暴露任意异常中的授权", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            const string secret = "fake-private-token-must-never-render";
            f.Client.Snapshot = (_, _) => Task.FromException<GameSnapshot>(new InvalidOperationException("https://example.invalid?token=" + secret));
            await f.Controller.RefreshAsync();
            Assert(f.Controller.State.Error != null && !f.Controller.State.Error.Contains(secret, StringComparison.Ordinal), "错误详情泄露任意异常文本");
        });
        await Check("终末地保留原接口数据不完整提示", async () =>
        {
            using var f = new Fixture();
            await f.Controller.InitializeAsync();
            const string message = "森空岛返回的数据不完整，请稍后重试；接口格式可能已改变。";
            f.Client.Snapshot = (_, _) => Task.FromException<GameSnapshot>(new InvalidOperationException(message));
            await f.Controller.RefreshAsync();
            Assert(f.Controller.State.Error == message && f.Controller.State.Status.Contains("已保留上次数据", StringComparison.Ordinal), "字段缺失错误被改成普通网络错误");
        });
        return results;

        async Task Check(string name, Func<Task> action)
        {
            try { await action(); results.Add("PASS " + name); }
            catch (Exception ex) { results.Add("FAIL " + name + "：" + ex.Message); }
        }
    }

    private static Task<GameSnapshot> AddPending(List<PendingSnapshot> requests, EndfieldRole role, CancellationToken token)
    {
        var request = new PendingSnapshot(role, token);
        requests.Add(request);
        return request.Completion.Task;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEmpty(GameViewState state) => Assert(!state.LoggedIn && !state.Demo && !state.Busy
        && state.Stamina == null && state.MaxStamina == null && state.Daily == null && state.MaxDaily == null
        && state.FullAt == null && state.UpdatedAt == null && state.Accounts.Count == 0 && state.SelectedAccountKey == null
        && state.CopyIdentity == null && state.IdentityLabel == "" && state.Nickname == "未登录", "会话退出/取消后显示没有完整清空");

    private sealed class PendingSnapshot(EndfieldRole role, CancellationToken cancellation)
    {
        public EndfieldRole Role { get; } = role;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<GameSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Fixture : IDisposable
    {
        public UnifiedSettings Settings { get; } = new()
        {
            CurrentGame = "wuwa",
            Wuwa = new WuwaPreferences { AccountKey = "wuwa-sentinel", RefreshMinutes = 10, GamePath = "wuwa-sentinel.exe" },
            Endfield = new EndfieldPreferences { SelectedRoleId = "saved-role-id", SelectedRoleKey = "saved-role-key", RefreshMinutes = 10 }
        };
        public FakeClient Client { get; } = new();
        public FakeStore Store { get; } = new();
        public EndfieldController Controller { get; }
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        public int SettingsSaves { get; private set; }
        public int LoginCalls { get; private set; }
        public Func<Window?, CancellationToken, Task<string?>> Login { get; set; } = (_, _) => Task.FromResult<string?>(null);

        public Fixture(bool preview = false, bool demo = false)
        {
            Client.Snapshot = (role, _) => Task.FromResult(Snapshot(role, 206));
            Controller = new EndfieldController(Settings, preview, demo, Client, Store,
                (owner, token) => { LoginCalls++; return Login(owner, token); }, () => Now, () => SettingsSaves++);
        }

        public GameSnapshot Snapshot(EndfieldRole role, int stamina) =>
            new(stamina, 360, Now.AddHours(18).AddMinutes(28), 0, 100, 0, 10, 60, null, role.Nickname, Now);
        public void Dispose() => Controller.Dispose();
    }

    private sealed class FakeStore : IEndfieldTokenStore
    {
        public int Reads { get; private set; }
        public List<string?> SavedTokens { get; } = new();
        public string? Load() { Reads++; return "fake-account-token"; }
        public void Save(string? token) => SavedTokens.Add(token);
    }

    private sealed class FakeClient : IEndfieldDataClient
    {
        public IReadOnlyList<EndfieldRole> Roles { get; } = new[]
        {
            new EndfieldRole("ROLE-1001", "server-a", "skland-user-a", "管理员甲", "服务器甲"),
            new EndfieldRole("ROLE-1001", "server-b", "skland-user-a", "管理员乙", "服务器乙")
        };
        public Func<CancellationToken, Task<SklandCredential>>? Login { get; set; }
        public Func<EndfieldRole, CancellationToken, Task<GameSnapshot>> Snapshot { get; set; } = null!;
        public int LoginCalls { get; private set; }
        public int RolesCalls { get; private set; }
        public int SnapshotCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int NetworkCalls => LoginCalls + RolesCalls + SnapshotCalls;
        public Task<SklandCredential> LoginAsync(string token, CancellationToken cancellation)
        {
            LoginCalls++;
            return Login?.Invoke(cancellation) ?? Task.FromResult(new SklandCredential("fake-cred", "fake-sign-token"));
        }
        public Task<IReadOnlyList<EndfieldRole>> GetRolesAsync(SklandCredential credential, CancellationToken cancellation)
        {
            RolesCalls++;
            return Task.FromResult(Roles);
        }
        public async Task<GameSnapshot> GetSnapshotAsync(SklandCredential credential, EndfieldRole role, Action<JsonElement> observeDetail, CancellationToken cancellation)
        {
            SnapshotCalls++;
            var snapshot = await Snapshot(role,cancellation);
            observeDetail(JsonSerializer.SerializeToElement(new
            {
                @base = new { roleId=role.RoleId },
                warEchoes = new { seasons=new[] { new {name="演示赛季",stars=2,
                    startTs=snapshot.FetchedAt.AddDays(-1).ToUnixTimeSeconds(),endTs=snapshot.FetchedAt.AddDays(1).ToUnixTimeSeconds()} } }
            }));
            return snapshot;
        }
        public void Dispose() => DisposeCalls++;
    }
}
