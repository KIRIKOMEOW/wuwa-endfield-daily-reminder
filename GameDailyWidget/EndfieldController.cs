using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EndfieldDesktopWidget;

namespace GameDailyWidget;

// Keep the existing protocol and DPAPI store unchanged. These seams let the
// regression checks exercise session races without reading an actual account.
internal interface IEndfieldDataClient : IDisposable
{
    Task<SklandCredential> LoginAsync(string token, CancellationToken cancellation);
    Task<IReadOnlyList<EndfieldRole>> GetRolesAsync(SklandCredential credential, CancellationToken cancellation);
    Task<GameSnapshot> GetSnapshotAsync(SklandCredential credential, EndfieldRole role, Action<JsonElement> observeDetail, CancellationToken cancellation);
}

internal interface IEndfieldTokenStore
{
    string? Load();
    void Save(string? token);
}

internal sealed class ExistingEndfieldClient : IEndfieldDataClient
{
    internal SklandClient Client { get; } = new();
    public Task<SklandCredential> LoginAsync(string token, CancellationToken cancellation) =>
        Client.LoginWithAccountTokenAsync(token, cancellation);
    public Task<IReadOnlyList<EndfieldRole>> GetRolesAsync(SklandCredential credential, CancellationToken cancellation) =>
        Client.GetRolesAsync(credential, cancellation);
    public Task<GameSnapshot> GetSnapshotAsync(SklandCredential credential, EndfieldRole role, Action<JsonElement> observeDetail, CancellationToken cancellation) =>
        Client.GetSnapshotWithDetailAsync(credential, role, observeDetail, cancellation);
    public void Dispose() => Client.Dispose();
}

internal sealed class ExistingEndfieldTokenStore : IEndfieldTokenStore
{
    public string? Load() => SettingsStore.LoadAccountToken();
    public void Save(string? token) => SettingsStore.SaveAccountToken(token);
}

/// <summary>UI-thread controller; its Changed event follows the caller's WPF context.</summary>
public sealed class EndfieldController : IGameController
{
    private readonly UnifiedSettings _settings;
    private readonly bool _preview;
    private readonly IEndfieldDataClient _client;
    private readonly IEndfieldTokenStore _tokenStore;
    private readonly Func<Window?, CancellationToken, Task<string?>> _login;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action _saveSettings;
    private readonly DispatcherTimer? _refreshTimer;
    private readonly DispatcherTimer? _staleTimer;
    private CancellationTokenSource _sessionCancellation = new();
    private CancellationTokenSource? _refreshCancellation;
    private SklandCredential? _credential;
    private GameSnapshot? _snapshot;
    internal EndfieldProgressReading? EventProgress { get; private set; }
    private IReadOnlyList<EndfieldRole> _roles = Array.Empty<EndfieldRole>();
    private EndfieldRole? _selectedRole;
    private LoginWindow? _loginWindow;
    private bool _demo;
    private bool _credentialStorageEnabled;
    private bool _initialized;
    private bool _loginOpen;
    private bool _isSigningIn;
    private bool _isRefreshing;
    private bool _closed;
    private int _sessionVersion;
    private int _refreshVersion;
    private int _loginOperationVersion;
    private int _refreshMinutes;
    private string? _lastError;
    private string? _operationStatus;
    private string? _storageWarning;

    public string GameKey => "endfield";
    public GameViewState State { get; private set; } = new();
    public event Action? Changed;

    private readonly Func<TimeZoneInfo> _displayZone;
    public EndfieldController(UnifiedSettings settings, bool preview, bool demo, Func<TimeZoneInfo>? displayZone = null)
        : this(settings, preview, demo, new ExistingEndfieldClient(), new ExistingEndfieldTokenStore(), startTimers: true,
            displayZone: displayZone ?? (() => preview || demo ? TimeZoneInfo.Utc : TimeZoneInfo.Local)) { }

    internal EndfieldController(UnifiedSettings settings, bool preview, bool demo,
        IEndfieldDataClient client, IEndfieldTokenStore tokenStore,
        Func<Window?, CancellationToken, Task<string?>>? login = null,
        Func<DateTimeOffset>? now = null, Action? saveSettings = null, bool startTimers = false, Func<TimeZoneInfo>? displayZone = null)
    {
        _displayZone = displayZone ?? (() => TimeZoneInfo.Utc);
        _settings = settings;
        _preview = preview;
        _demo = demo;
        _credentialStorageEnabled = !preview && !demo;
        _client = client;
        _tokenStore = tokenStore;
        _login = login ?? ShowLoginWindowAsync;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _saveSettings = saveSettings ?? settings.Save;
        _refreshMinutes = preview || demo ? 5 : NormalizeMinutes(settings.Endfield.RefreshMinutes);
        if (startTimers && !preview)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_refreshMinutes) };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _staleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _staleTimer.Tick += StaleTimer_Tick;
        }
        if (_demo) SetDemoData(0);
        State = BuildState();
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _closed) return;
        _initialized = true;
        _refreshTimer?.Start();
        _staleTimer?.Start();
        if (_preview || _demo) { Publish(); return; }
        try
        {
            var token = _tokenStore.Load();
            if (!string.IsNullOrWhiteSpace(token)) await SignInAsync(token);
            else Publish();
        }
        catch
        {
            if (_closed) return;
            _lastError = "无法读取本机登录信息，请重新登录森空岛。";
            Publish();
        }
    }

    public Task RefreshAsync()
    {
        if (_closed) return Task.CompletedTask;
        if (_preview || _demo) { Publish(); return Task.CompletedTask; }
        return RefreshCoreAsync(true);
    }

    public async Task SelectAccountAsync(string key)
    {
        if (_closed) return;
        var role = _roles.FirstOrDefault(item => RoleKey(item) == key);
        if (role == null || (_selectedRole != null && RoleKey(_selectedRole) == key)) return;
        // Invalidate both the in-flight snapshot and its finally block before
        // publishing the new identity with empty measurements.
        ResetSession(clearCredential: false);
        _selectedRole = role;
        _snapshot = null;
        if (_demo)
        {
            SetDemoData(_roles.ToList().IndexOf(role));
            Publish();
            return;
        }
        SaveSelectedRole(role);
        Publish();
        await RefreshCoreAsync(true);
    }

    public async Task LoginAsync(Window? owner)
    {
        if (_closed || _preview || _loginOpen) return;
        // Clear demonstration measurements before a real login dialog can open.
        // A cancelled dialog must never leave demo numbers labelled as real data.
        ResetSession();
        _demo = false;
        _snapshot = null;
        _roles = Array.Empty<EndfieldRole>();
        _selectedRole = null;
        _loginOpen = true;
        var loginOperation = ++_loginOperationVersion;
        _isSigningIn = true;
        _operationStatus = "等待森空岛扫码登录";
        var version = _sessionVersion;
        var cancellation = _sessionCancellation.Token;
        Publish();
        try
        {
            var token = await _login(owner, cancellation);
            if (cancellation.IsCancellationRequested || version != _sessionVersion || _closed) return;
            if (!string.IsNullOrWhiteSpace(token)) await SignInAsync(token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _sessionVersion || _closed) return;
            _lastError = SafeError(ex);
        }
        finally
        {
            if (loginOperation == _loginOperationVersion) _loginOpen = false;
            // SignInAsync owns the newer session's busy state after a token is accepted.
            if (version == _sessionVersion && !_closed)
            {
                _isSigningIn = false;
                _operationStatus = null;
            }
            if (!_closed && loginOperation == _loginOperationVersion) Publish();
        }
    }

    private Task<string?> ShowLoginWindowAsync(Window? owner, CancellationToken cancellation)
    {
        if (_preview || _closed || cancellation.IsCancellationRequested) return Task.FromResult<string?>(null);
        if (_client is not ExistingEndfieldClient client)
            throw new InvalidOperationException("登录窗口不可用，请稍后重试。");
        var window = new LoginWindow(client.Client);
        // The existing manual-token panel is taller than its QR panel. Adapt only
        // this host's window height without changing the preserved login source.
        window.SizeToContent = SizeToContent.Height;
        _loginWindow = window;
        // The host is always a normal top-level window and can own this dialog.
        if (owner is { IsVisible: true }) window.Owner = owner;
        try
        {
            var result = window.ShowDialog();
            return Task.FromResult(result == true && !cancellation.IsCancellationRequested ? window.AccountToken : null);
        }
        finally
        {
            if (ReferenceEquals(_loginWindow, window)) _loginWindow = null;
        }
    }

    private async Task SignInAsync(string token)
    {
        if (_preview || _closed) return;
        ResetSession();
        _demo = false;
        _snapshot = null;
        _roles = Array.Empty<EndfieldRole>();
        _selectedRole = null;
        _isSigningIn = true;
        _operationStatus = "正在登录森空岛…";
        var version = _sessionVersion;
        var cancellation = _sessionCancellation.Token;
        Publish();
        try
        {
            var credential = await _client.LoginAsync(token, cancellation);
            if (cancellation.IsCancellationRequested || version != _sessionVersion || _closed) return;
            var roles = await _client.GetRolesAsync(credential, cancellation);
            if (cancellation.IsCancellationRequested || version != _sessionVersion || _closed) return;
            if (roles.Count == 0) throw new InvalidOperationException("这个森空岛账号没有绑定终末地角色。");
            _credential = credential;
            _roles = roles.ToArray();
            _selectedRole = FindPreferredRole(_roles);
            _credentialStorageEnabled = true;
            try { _tokenStore.Save(token); }
            catch { _storageWarning = "登录信息未能保存在本机，下次启动请重新登录。"; }
            SaveSelectedRole(_selectedRole);
            Publish();
            await RefreshCoreAsync(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _sessionVersion || _closed) return;
            _credential = null;
            _snapshot = null;
            _roles = Array.Empty<EndfieldRole>();
            _selectedRole = null;
            _lastError = SafeError(ex);
        }
        finally
        {
            if (version == _sessionVersion && !_closed)
            {
                _isSigningIn = false;
                _operationStatus = null;
                Publish();
            }
        }
    }

    private async Task RefreshCoreAsync(bool force)
    {
        if (_closed || _preview || _demo || _credential == null || _selectedRole == null) return;
        if (_isRefreshing && !force) return;
        _refreshCancellation?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token);
        _refreshCancellation = request;
        var version = ++_refreshVersion;
        var sessionVersion = _sessionVersion;
        var credential = _credential;
        var role = _selectedRole;
        var roleKey = RoleKey(role);
        _isRefreshing = true;
        _operationStatus = "正在同步森空岛…";
        Publish();
        try
        {
            IReadOnlyList<EndfieldProgress> progress = [];
            var snapshot = await _client.GetSnapshotAsync(credential, role,
                detail => progress = EndfieldEventProgress.Parse(detail,role.RoleId), request.Token);
            if (!IsCurrentRequest(request, version, sessionVersion, roleKey)) return;
            _snapshot = snapshot;
            EventProgress = new(roleKey,snapshot.FetchedAt,progress);
            _lastError = null;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentRequest(request, version, sessionVersion, roleKey)) return;
            // The only snapshot retained here belongs to the still-selected role.
            EventProgress = null;
            _lastError = SafeError(ex);
        }
        finally
        {
            var current = IsCurrentRequest(request, version, sessionVersion, roleKey);
            if (ReferenceEquals(_refreshCancellation, request)) _refreshCancellation = null;
            request.Dispose();
            if (current)
            {
                _isRefreshing = false;
                _operationStatus = _isSigningIn ? "正在登录森空岛…" : null;
                Publish();
            }
        }
    }

    private bool IsCurrentRequest(CancellationTokenSource request, int version, int sessionVersion, string roleKey) =>
        !request.IsCancellationRequested && !_closed && version == _refreshVersion && sessionVersion == _sessionVersion
        && _selectedRole != null && RoleKey(_selectedRole) == roleKey;

    public void Logout()
    {
        if (_closed) return;
        var clearStoredCredential = !_preview && _credentialStorageEnabled && !_demo;
        var wasDemo = _demo;
        ResetSession();
        _loginOperationVersion++;
        _loginOpen = false;
        CloseLoginWindow();
        _demo = false;
        _snapshot = null;
        _roles = Array.Empty<EndfieldRole>();
        _selectedRole = null;
        if (clearStoredCredential)
        {
            try { _tokenStore.Save(null); }
            catch { _lastError = "已退出当前会话，但本机登录信息未能清除，请检查文件权限。"; }
        }
        if (!_preview && !wasDemo && _credentialStorageEnabled)
        {
            _settings.Endfield.SelectedRoleId = null;
            _settings.Endfield.SelectedRoleKey = null;
            _saveSettings();
        }
        Publish();
    }

    public void SetRefreshMinutes(int minutes)
    {
        if (_closed) return;
        _refreshMinutes = NormalizeMinutes(minutes);
        if (_refreshTimer != null) _refreshTimer.Interval = TimeSpan.FromMinutes(_refreshMinutes);
        if (!_preview && !_demo)
        {
            _settings.Endfield.RefreshMinutes = _refreshMinutes;
            _saveSettings();
        }
        Publish();
    }

    private static int NormalizeMinutes(int minutes) => minutes is 1 or 5 or 10 ? minutes : 5;

    private EndfieldRole FindPreferredRole(IReadOnlyList<EndfieldRole> roles)
    {
        var exact = roles.FirstOrDefault(role => RoleKey(role) == _settings.Endfield.SelectedRoleKey);
        if (exact != null) return exact;
        // A legacy roleId without a server is only usable when unambiguous.
        if (string.IsNullOrWhiteSpace(_settings.Endfield.SelectedRoleKey))
        {
            var legacy = roles.Where(role => role.RoleId == _settings.Endfield.SelectedRoleId).ToArray();
            if (legacy.Length == 1) return legacy[0];
        }
        return roles[0];
    }

    private void SaveSelectedRole(EndfieldRole role)
    {
        if (_preview || _demo) return;
        _settings.Endfield.SelectedRoleId = role.RoleId;
        _settings.Endfield.SelectedRoleKey = RoleKey(role);
        _saveSettings();
    }

    internal static string RoleKey(EndfieldRole role)
    {
        // The selection key contains no token/cred. Length-safe serialization also
        // prevents an identical roleId on another server/account from colliding.
        var identity = JsonSerializer.Serialize(new[] { role.UserId, role.ServerId, role.RoleId });
        return "endfield:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private void SetDemoData(int index)
    {
        var now = _now();
        var roles = new[]
        {
            new EndfieldRole("DEMO-1001", "demo-server-1", "demo-user", "演示管理员", "演示服务器 A"),
            new EndfieldRole("DEMO-1002", "demo-server-2", "demo-user", "演示管理员二", "演示服务器 B")
        };
        _roles = roles;
        _selectedRole = roles[Math.Clamp(index, 0, roles.Length - 1)];
        _snapshot = index == 1
            ? new GameSnapshot(360, 360, now, 100, 100, 7, 10, 36, null, _selectedRole.Nickname, now)
            : new GameSnapshot(206, 360, now.AddHours(18).AddMinutes(28), 0, 100, 0, 10, 60, 60, _selectedRole.Nickname, now);
    }

    private GameViewState BuildState()
    {
        var data = _snapshot;
        var role = _selectedRole;
        var busy = _isSigningIn || _isRefreshing || _loginOpen;
        var stale = !_demo && data != null && _now() - data.FetchedAt > TimeSpan.FromMinutes(15);
        var status = busy ? _operationStatus ?? "正在登录森空岛…"
            : _demo ? "演示数据 · 未连接游戏账号"
            : _lastError != null ? data != null ? "同步失败 · 已保留上次数据" : _credential == null ? "登录未完成，请重新登录" : "暂时无法同步"
            : data != null ? stale ? "数据已过期 · 请同步" : "已同步森空岛"
            : _credential != null ? "等待同步森空岛" : "等待登录森空岛";
        var details = new List<DetailLine>
        {
            new("每周事务", data == null ? "-- / --" : Fraction(data.WeeklyScore, data.WeeklyTotal),
                data == null ? "等待同步" : data.WeeklyTotal <= 0 ? "事务总进度暂未提供" : data.WeeklyScore >= data.WeeklyTotal ? "本周已完成" : "本周未完成",
                Current: data?.WeeklyScore, Maximum: data?.WeeklyTotal is > 0 ? data.WeeklyTotal : null),
            new("通行证等级", data == null ? "Lv. --" : data.MaxBattlePassLevel is > 0 ? $"Lv. {data.BattlePassLevel} / {data.MaxBattlePassLevel}" : $"Lv. {data.BattlePassLevel}",
                data == null ? "等待同步" : data.MaxBattlePassLevel is not > 0 ? "接口未提供等级上限" : data.BattlePassLevel >= data.MaxBattlePassLevel ? "通行证已满级" : "当前赛季进度",
                Current: data?.BattlePassLevel, Maximum: data?.MaxBattlePassLevel),
            RecoveryDetail(data),
            new("角色 ID", role?.RoleId ?? "--", "游戏角色 ID；不是森空岛用户 ID"),
            new("服务器", role?.ServerName ?? "--", role == null ? null : "服务器 ID：" + role.ServerId)
        };
        if (data != null) details.Add(new("上次更新", DailyReminderSchedule.Local(data.FetchedAt,_displayZone(),"MM/dd HH:mm:ss"),
            _lastError != null ? "同步失败，保留该时间的数据" : stale ? "数据已过期" : $"每 {_refreshMinutes} 分钟自动同步"));
        if (_storageWarning != null) details.Add(new("本机登录", "下次启动需重新登录", _storageWarning));
        return new GameViewState
        {
            GameKey = GameKey, GameName = "终末地", StaminaLabel = "理智",
            Nickname = data?.Nickname ?? role?.Nickname ?? "未登录",
            IdentityLabel = role == null ? "" : "角色 ID " + role.RoleId,
            CopyIdentity = role?.RoleId,
            Stamina = data?.Stamina, MaxStamina = data?.MaxStamina is > 0 ? data.MaxStamina : null,
            FullAt = data?.StaminaFullAt, Daily = data?.DailyActivation,
            MaxDaily = data?.MaxDailyActivation is > 0 ? data.MaxDailyActivation : null,
            DailyState = data == null || data.MaxDailyActivation <= 0 ? "unknown" : data.DailyActivation >= data.MaxDailyActivation ? "complete" : "incomplete",
            Details = details.ToArray(),
            Accounts = _roles.Select(item => new AccountChoice(RoleKey(item), item.Nickname + " · " + item.ServerName)).ToArray(),
            SelectedAccountKey = role == null ? null : RoleKey(role), Status = status, Error = _lastError,
            UpdatedAt = data?.FetchedAt, Busy = busy, Demo = _demo, LoggedIn = !_demo && _credential != null,
            Stale = stale, RefreshMinutes = _refreshMinutes
        };
    }

    private DetailLine RecoveryDetail(GameSnapshot? data)
    {
        if (data == null) return new("预计回满时间", "--", "登录后显示恢复时间");
        if (data.MaxStamina > 0 && data.Stamina >= data.MaxStamina)
            return new("预计回满时间", "理智已回满", "可以出发了");
        if (data.StaminaFullAt is not { } fullAt)
            return new("预计回满时间", "--", "接口暂未提供恢复时间");
        return new("预计回满时间", DailyReminderSchedule.Local(fullAt,_displayZone(),"yyyy/MM/dd HH:mm:ss"),
            fullAt <= _now() ? "预计已回满，刷新以确认最新理智" : "森空岛提供的预计时间", fullAt);
    }

    private static string Fraction(int current, int maximum) => maximum > 0 ? $"{current} / {maximum}" : current.ToString(CultureInfo.InvariantCulture);

    private static string SafeError(Exception error)
    {
        // Only the existing client's deliberately safe user messages are displayed.
        // Arbitrary exception text can contain a request URL or account token.
        var text = error is InvalidOperationException ? error.Message : "";
        var known = new[]
        {
            "这个森空岛账号没有绑定终末地角色。", "登录凭证无效，请重新扫码登录。",
            "登录凭证无效或访问被拒绝，请重新扫码登录。", "登录凭证已失效，请重新扫码登录。",
            "请求过于频繁，请稍后再试。", "连接森空岛超时，请检查网络后重试。",
            "无法连接森空岛，请检查网络后重试。", "森空岛返回的数据不完整，请稍后重试；接口格式可能已改变。",
            "请填写完整的账号 Token，或使用扫码登录。", "登录窗口不可用，请稍后重试。"
        };
        if (known.Contains(text, StringComparer.Ordinal)) return text;
        if (Regex.IsMatch(text, @"\A(?:账号授权|获取森空岛凭证|读取森空岛账号|读取绑定角色|刷新登录凭证|读取终末地状态)失败（(?:HTTP \d{3}|接口代码 -?\d{1,10})），请稍后重试或重新登录。\z")) return text;
        return "暂时无法同步，请检查网络后重试或重新登录森空岛。";
    }

    private void Publish()
    {
        if (_closed) return;
        State = BuildState();
        Changed?.Invoke();
    }

    private void ResetSession(bool clearCredential = true)
    {
        EventProgress = null;
        _sessionVersion++;
        _refreshVersion++;
        _sessionCancellation.Cancel();
        _sessionCancellation.Dispose();
        _sessionCancellation = new CancellationTokenSource();
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        _isRefreshing = false;
        _isSigningIn = false;
        _operationStatus = null;
        _lastError = null;
        _storageWarning = null;
        if (clearCredential) _credential = null;
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _ = RefreshScheduledAsync();
    }

    // Scheduled work must not force-cancel manual refresh, login or role selection.
    internal Task RefreshScheduledAsync()
        => _loginOpen || _isSigningIn ? Task.CompletedTask : RefreshCoreAsync(false);

    private void StaleTimer_Tick(object? sender, EventArgs e)
        => UpdateStaleState();

    internal void UpdateStaleState()
    {
        if (_closed || _demo || _snapshot == null) return;
        var stale = _now() - _snapshot.FetchedAt > TimeSpan.FromMinutes(15);
        if (stale != State.Stale) Publish();
    }

    private void CloseLoginWindow()
    {
        var window = _loginWindow;
        _loginWindow = null;
        if (window == null) return;
        if (window.Dispatcher.CheckAccess()) window.Close();
        else window.Dispatcher.BeginInvoke(new Action(window.Close));
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        EventProgress = null;
        _sessionVersion++;
        _refreshVersion++;
        _loginOperationVersion++;
        _refreshTimer?.Stop();
        _staleTimer?.Stop();
        if (_refreshTimer != null) _refreshTimer.Tick -= RefreshTimer_Tick;
        if (_staleTimer != null) _staleTimer.Tick -= StaleTimer_Tick;
        _sessionCancellation.Cancel();
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        _sessionCancellation.Dispose();
        CloseLoginWindow();
        _credential = null;
        _snapshot = null;
        _roles = Array.Empty<EndfieldRole>();
        _selectedRole = null;
        _isSigningIn = _isRefreshing = _loginOpen = false;
        _demo = false;
        _lastError = _operationStatus = null;
        State = BuildState();
        _client.Dispose();
    }
}
