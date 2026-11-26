using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wuwa.Core;

namespace GameDailyWidget;

public sealed class WuwaController : IGameController
{
    private readonly object _gate = new();
    private readonly UnifiedSettings _settings;
    private readonly bool _preview;
    private readonly bool _demo;
    private readonly bool _persistSettings;
    private readonly IWuwaAdapter? _adapter;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _ageTimer;
    private CancellationTokenSource? _request;
    private IReadOnlyList<AccountOption> _options = Array.Empty<AccountOption>();
    private AccountOption? _selected;
    private RoleSnapshot? _snapshot;
    private GameViewState _state;
    private string? _preferredAccountKey;
    private int _refreshMinutes;
    private long _version;
    private bool _initialized;
    private bool _busy;
    private bool _scanning;
    private bool _lastFailed;
    private bool _disposed;

    private readonly Func<TimeZoneInfo> _displayZone;
    public WuwaController(UnifiedSettings settings, bool preview, bool demo, Func<TimeZoneInfo>? displayZone = null)
        : this(settings, preview, demo, preview || demo ? null : new WuwaLauncherAdapter(),
            persistSettings: !preview && !demo, displayZone: displayZone ?? (() => preview || demo ? TimeZoneInfo.Utc : TimeZoneInfo.Local)) { }

    // Tests inject only an in-memory adapter and disable personal settings writes.
    internal WuwaController(UnifiedSettings settings, bool preview, bool demo, IWuwaAdapter? adapter,
        bool persistSettings = false, Dispatcher? dispatcher = null, Func<TimeZoneInfo>? displayZone = null)
    {
        _displayZone = displayZone ?? (() => TimeZoneInfo.Utc);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _preview = preview || demo;
        _demo = demo;
        _persistSettings = persistSettings && !_preview;
        _adapter = _preview ? null : adapter ?? throw new ArgumentNullException(nameof(adapter));
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        // Preview never inspects even the supplied personal preference object.
        _refreshMinutes = _preview ? 5 : NormalizeInterval(settings.Wuwa.RefreshMinutes);
        _preferredAccountKey = _preview ? null : settings.Wuwa.AccountKey;
        _state = EmptyState(_preview && !_demo ? "预览 · 尚未读取账号" : "等待同步");
        _autoTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMinutes(_refreshMinutes)
        };
        _autoTimer.Tick += OnAutoRefresh;
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _ageTimer.Tick += OnAgeTick;
    }

    public string GameKey => "wuwa";
    public event Action? Changed;
    // The host temporarily owns refresh cadence during the daily reset window.
    internal Func<bool>? AutomaticRefreshSuppressed { get; set; }

    public GameViewState State
    {
        get
        {
            lock (_gate)
            {
                return AgedStateUnsafe();
            }
        }
    }

    public async Task InitializeAsync()
    {
        bool remainDisconnected;
        lock (_gate)
        {
            if (_disposed || _initialized) return;
            _initialized = true;
            remainDisconnected = !_preview && _preferredAccountKey == "";
            if (remainDisconnected)
                _state = EmptyState("已取消本组件的账号选择 · 重新扫描即可连接");
        }
        if (!_preview || _demo) StartTimers();
        if (remainDisconnected || (_preview && !_demo))
        {
            NotifyChanged();
            return;
        }
        await ScanAsync();
    }

    public Task LoginAsync(Window? owner)
    {
        // "Login" in the shared contract means scan existing launcher authorization.
        // There is deliberately no login, token renewal or Kuro community request.
        return ScanAsync(explicitScan: true);
    }

    public Task RefreshAsync() => RefreshSelectedAsync(expectedKey: null);

    public async Task SelectAccountAsync(string key)
    {
        AccountOption? selected;
        lock (_gate)
        {
            if (_disposed || _scanning) return;
            selected = _options.FirstOrDefault(option => option.Handle.Key == key);
            if (selected is null) return;
            CancelRequestUnsafe();
            ++_version;
            _busy = false;
            _selected = selected;
            _snapshot = null;
            _lastFailed = false;
            _preferredAccountKey = selected.Handle.Key;
            _state = SelectedStateUnsafe(selected.Identity is null ? "账号信息失效" : "正在确认当前账号…");
            if (selected.Identity is null)
                _state = _state with { Error = selected.Problem ?? "此账号信息不可用，请重新扫描。" };
            PersistSelection(selected.Handle.Key);
        }
        // Clear old values and copyable identity before starting the new request.
        NotifyChanged();
        if (selected.Identity is not null) await RefreshSelectedAsync(selected.Handle.Key);
    }

    public void Logout()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelRequestUnsafe();
            ++_version;
            _busy = _scanning = false;
            _options = Array.Empty<AccountOption>();
            _selected = null;
            _snapshot = null;
            _lastFailed = false;
            // Empty is a component-only disconnected sentinel, distinct from a
            // never-selected null preference. The official launcher is untouched.
            _preferredAccountKey = "";
            _state = EmptyState("已取消本组件的账号选择 · 官方启动器登录保持原状");
            PersistSelection("");
        }
        NotifyChanged();
    }

    public void SetRefreshMinutes(int minutes)
    {
        var normalized = NormalizeInterval(minutes);
        lock (_gate)
        {
            if (_disposed) return;
            _refreshMinutes = normalized;
            _state = _state with { RefreshMinutes = normalized };
            if (!_preview)
            {
                _settings.Wuwa.RefreshMinutes = normalized;
                SavePreferences();
            }
        }
        RestartRefreshTimer();
        NotifyChanged();
    }

    private async Task ScanAsync(bool explicitScan = false)
    {
        long version;
        CancellationToken cancellation;
        string? preferred;
        lock (_gate)
        {
            if (_disposed || _scanning) return;
            CancelRequestUnsafe();
            version = ++_version;
            _request = new CancellationTokenSource();
            cancellation = _request.Token;
            preferred = _selected?.Handle.Key ?? _preferredAccountKey;
            if (explicitScan && preferred == "") preferred = null;
            _options = Array.Empty<AccountOption>();
            _selected = null;
            _snapshot = null;
            _lastFailed = false;
            _scanning = _busy = true;
            _state = EmptyState("正在重新确认启动器账号…") with { Busy = true };
        }
        NotifyChanged();
        string? selectedKey = null;
        try
        {
            var discovered = _demo ? DemoAccounts() : _preview
                ? Array.Empty<AccountOption>() : await _adapter!.DiscoverAccountsAsync(cancellation);
            lock (_gate)
            {
                if (!IsCurrentUnsafe(version, cancellation)) return;
                _options = discovered.ToArray();
                _selected = _options.FirstOrDefault(option => option.Handle.Key == preferred)
                    ?? _options.FirstOrDefault(option => option.Identity is not null) ?? _options.FirstOrDefault();
                if (_selected is null)
                {
                    _state = EmptyState(_preview ? "预览 · 尚未读取账号" :
                        "未找到可用账号 · 请先登录鸣潮官方启动器，再重新扫描") with { Busy = true };
                }
                else
                {
                    selectedKey = _selected.Handle.Key;
                    _preferredAccountKey = selectedKey;
                    _state = SelectedStateUnsafe(_selected.Identity is null ? "账号信息失效" : "已确认账号 · 等待更新");
                    if (_selected.Identity is null)
                        _state = _state with { Error = _selected.Problem ?? "账号信息不可用，请更新官方启动器授权。" };
                    PersistSelection(selectedKey);
                }
            }
            NotifyChanged();
        }
        catch (OperationCanceledException) { }
        catch (LauncherException error) { FailScan(version, cancellation, error.Message); }
        catch { FailScan(version, cancellation, "暂时无法读取账号，请稍后重新扫描。"); }
        finally
        {
            lock (_gate)
            {
                if (IsCurrentUnsafe(version, cancellation))
                {
                    _scanning = _busy = false;
                    _state = _state with { Busy = false };
                }
            }
            NotifyChanged();
        }
        bool shouldRefresh;
        lock (_gate)
            shouldRefresh = IsCurrentUnsafe(version, cancellation) && _selected?.Identity is not null;
        if (shouldRefresh) await RefreshSelectedAsync(selectedKey);
    }

    private async Task RefreshSelectedAsync(string? expectedKey)
    {
        AccountOption selected;
        long version;
        CancellationToken cancellation;
        lock (_gate)
        {
            if (_disposed || _busy || _scanning || _selected?.Identity is null ||
                (expectedKey is not null && _selected.Handle.Key != expectedKey)) return;
            CancelRequestUnsafe();
            selected = _selected;
            version = ++_version;
            _request = new CancellationTokenSource();
            cancellation = _request.Token;
            _busy = true;
            _state = _state with
            {
                Busy = true,
                Status = _snapshot is null ? "正在更新鸣潮数据…" : $"正在更新 · 上次 {LocalTime(_snapshot.FetchedAtUtc)}"
            };
        }
        NotifyChanged();
        try
        {
            var snapshot = _demo ? DemoSnapshot(selected.Identity!)
                : await _adapter!.QueryAsync(selected.Handle, cancellation);
            lock (_gate)
            {
                if (!IsCurrentUnsafe(version, cancellation, selected.Handle.Key)) return;
                if (!string.Equals(snapshot.Identity.Uid, selected.Identity!.Uid, StringComparison.Ordinal))
                {
                    InvalidateAuthorizationUnsafe("账号授权已变化，请重新扫描。");
                    PersistSelection(null);
                }
                else
                {
                    _snapshot = snapshot;
                    _lastFailed = false;
                    _preferredAccountKey = selected.Handle.Key;
                    _state = SnapshotStateUnsafe(snapshot);
                    PersistSelection(selected.Handle.Key);
                }
            }
            NotifyChanged();
        }
        catch (OperationCanceledException) { }
        catch (LauncherException error)
        {
            lock (_gate)
            {
                if (!IsCurrentUnsafe(version, cancellation, selected.Handle.Key)) return;
                if (error.Status is "account_changed" or "identity_mismatch" or "account_missing")
                {
                    InvalidateAuthorizationUnsafe("账号授权已变化，请重新扫描。");
                    PersistSelection(null);
                }
                else ShowFailureUnsafe(error.Message);
            }
            NotifyChanged();
        }
        catch
        {
            lock (_gate)
            {
                if (!IsCurrentUnsafe(version, cancellation, selected.Handle.Key)) return;
                ShowFailureUnsafe("暂时无法更新，请稍后重试。");
            }
            NotifyChanged();
        }
        finally
        {
            var restart = false;
            lock (_gate)
            {
                if (IsCurrentUnsafe(version, cancellation))
                {
                    _busy = false;
                    _state = _state with { Busy = false };
                    restart = true;
                }
            }
            if (restart) RestartRefreshTimer();
            NotifyChanged();
        }
    }

    private void FailScan(long version, CancellationToken cancellation, string message)
    {
        lock (_gate)
        {
            if (!IsCurrentUnsafe(version, cancellation)) return;
            _lastFailed = true;
            _state = EmptyState("账号扫描失败") with { Error = message, Busy = true };
        }
        NotifyChanged();
    }

    private void ShowFailureUnsafe(string message)
    {
        _lastFailed = true;
        _state = _state with
        {
            Error = message,
            Stale = _snapshot is not null,
            Status = _snapshot is null ? "更新失败" : $"更新失败 · 显示上次 {LocalTime(_snapshot.FetchedAtUtc)} 的数据"
        };
    }

    private void InvalidateAuthorizationUnsafe(string message)
    {
        var previousKey = _selected?.Handle.Key;
        _options = _options.Select(option => option.Handle.Key == previousKey
            ? new AccountOption { Handle = option.Handle, Problem = message } : option).ToArray();
        _snapshot = null;
        _selected = null;
        _preferredAccountKey = null;
        _lastFailed = true;
        _state = EmptyState("账号授权已变化") with
        {
            Accounts = AccountChoicesUnsafe(), Error = message, Busy = true
        };
    }

    private GameViewState SnapshotStateUnsafe(RoleSnapshot snapshot) => new()
    {
        GameKey = GameKey,
        GameName = "鸣潮",
        Nickname = snapshot.Identity.Name,
        IdentityLabel = $"特征码 {snapshot.Identity.Uid}",
        CopyIdentity = snapshot.Identity.Uid,
        StaminaLabel = "结晶波片",
        Stamina = snapshot.Energy,
        MaxStamina = snapshot.MaxEnergy,
        FullAt = snapshot.EnergyFullAtUtc,
        Daily = snapshot.Liveness,
        MaxDaily = snapshot.LivenessMaxCount,
        DailyState = snapshot.DailyState,
        Details = BuildDetails(snapshot),
        Accounts = AccountChoicesUnsafe(),
        SelectedAccountKey = _selected?.Handle.Key,
        Status = (_demo ? "演示 · " : "") + $"已更新 {LocalTime(snapshot.FetchedAtUtc)}" +
            (snapshot.MissingFields.Count > 0 ? " · 部分字段未知" : ""),
        UpdatedAt = snapshot.FetchedAtUtc,
        Busy = _busy,
        Demo = _demo,
        LoggedIn = true,
        RefreshMinutes = _refreshMinutes
    };

    private GameViewState SelectedStateUnsafe(string status) => EmptyState(status) with
    {
        Nickname = _selected?.Identity?.Name ?? (_selected is null ? "未选择账号" : "账号信息失效"),
        IdentityLabel = _selected?.Identity is { } identity ? $"特征码 {identity.Uid}" : "特征码 —",
        CopyIdentity = _selected?.Identity?.Uid,
        Accounts = AccountChoicesUnsafe(),
        SelectedAccountKey = _selected?.Handle.Key,
        LoggedIn = _selected?.Identity is not null,
        Busy = _busy
    };

    private GameViewState EmptyState(string status) => new()
    {
        GameKey = GameKey,
        GameName = "鸣潮",
        Nickname = "未选择账号",
        IdentityLabel = "特征码 —",
        StaminaLabel = "结晶波片",
        Status = status,
        Demo = _demo,
        RefreshMinutes = _refreshMinutes
    };

    private IReadOnlyList<AccountChoice> AccountChoicesUnsafe() => _options
        .Select(option => new AccountChoice(option.Handle.Key, option.DisplayName)).ToArray();

    private IReadOnlyList<DetailLine> BuildDetails(RoleSnapshot snapshot)
    {
        var energyFull = snapshot.Energy.HasValue && snapshot.MaxEnergy is > 0 && snapshot.Energy >= snapshot.MaxEnergy;
        var storeFull = snapshot.StoreEnergy.HasValue && snapshot.MaxStoreEnergy is > 0 && snapshot.StoreEnergy >= snapshot.MaxStoreEnergy;
        return new[]
        {
            new DetailLine("波片回满时间", energyFull ? "已满" : ExactTime(snapshot.EnergyFullAtUtc),
                "服务器返回的回满时间；未推算当前波片。"),
            new DetailLine("结晶单质", Pair(snapshot.StoreEnergy, snapshot.MaxStoreEnergy),
                Current: snapshot.StoreEnergy, Maximum: snapshot.MaxStoreEnergy),
            new DetailLine("单质回满", storeFull ? "已满" : ExactTime(snapshot.StoreEnergyFullAtUtc),
                storeFull ? null : "按服务器返回时间倒计时。", storeFull ? null : snapshot.StoreEnergyFullAtUtc),
            new DetailLine("先约电台", snapshot.BattlePassLevel is { } level ? $"等级 {level}" : "未知"),
            new DetailLine("电台本周经验", Pair(snapshot.WeekExp, snapshot.WeekMaxExp),
                Current: snapshot.WeekExp, Maximum: snapshot.WeekMaxExp),
            new DetailLine("联觉等级", Number(snapshot.Identity.Level)),
            new DetailLine("周本数据", Number(snapshot.WeeklyInstCount), "该数值暂不能用于确认周本完成状态。")
        };
    }

    private GameViewState AgedStateUnsafe()
    {
        if (_snapshot is null || _busy || _lastFailed || _state.Stale ||
            DateTimeOffset.UtcNow - _snapshot.FetchedAtUtc <= TimeSpan.FromMinutes(_refreshMinutes + 1)) return _state;
        return _state with { Stale = true, Status = $"上次更新 {LocalTime(_snapshot.FetchedAtUtc)} · 数据可能过期" };
    }

    private bool IsCurrentUnsafe(long version, CancellationToken cancellation, string? expectedKey = null) =>
        !_disposed && version == _version && !cancellation.IsCancellationRequested &&
        (expectedKey is null || _selected?.Handle.Key == expectedKey);

    private void CancelRequestUnsafe()
    {
        if (_request is null) return;
        try { _request.Cancel(); } catch { }
        _request.Dispose();
        _request = null;
    }

    private void PersistSelection(string? key)
    {
        if (_preview) return;
        _settings.Wuwa.AccountKey = key;
        SavePreferences();
    }

    private void SavePreferences()
    {
        if (_persistSettings) _settings.Save();
    }

    private async void OnAutoRefresh(object? sender, EventArgs e)
    {
        try { await RefreshAutomaticallyAsync(); } catch { /* State errors are handled by the controller. */ }
    }

    internal Task RefreshAutomaticallyAsync()
        => AutomaticRefreshSuppressed?.Invoke() == true ? Task.CompletedTask : RefreshAsync();

    private void OnAgeTick(object? sender, EventArgs e)
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            var aged = AgedStateUnsafe();
            if (aged != _state) { _state = aged; changed = true; }
        }
        if (changed) NotifyChanged();
    }

    private void StartTimers() => OnDispatcher(() =>
    {
        lock (_gate)
        {
            if (_disposed) return;
            _autoTimer.Start();
            _ageTimer.Start();
        }
    });

    private void RestartRefreshTimer() => OnDispatcher(() =>
    {
        lock (_gate)
        {
            if (_disposed || !_initialized || (_preview && !_demo)) return;
            _autoTimer.Stop();
            _autoTimer.Interval = TimeSpan.FromMinutes(_refreshMinutes);
            _autoTimer.Start();
        }
    });

    private void NotifyChanged()
    {
        OnDispatcher(() =>
        {
            lock (_gate) { if (_disposed) return; }
            Changed?.Invoke();
        });
    }

    private void OnDispatcher(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_version;
            CancelRequestUnsafe();
            _busy = _scanning = false;
            _state = _state with { Busy = false };
            Changed = null;
        }
        OnDispatcher(() =>
        {
            _autoTimer.Stop();
            _ageTimer.Stop();
            _autoTimer.Tick -= OnAutoRefresh;
            _ageTimer.Tick -= OnAgeTick;
        });
        _adapter?.Dispose();
    }

    private static int NormalizeInterval(int minutes) => minutes is 1 or 5 or 10 ? minutes : 5;
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未知";
    private static string Pair(int? current, int? maximum) => $"{Number(current)} / {Number(maximum)}";
    private string LocalTime(DateTimeOffset date) => DailyReminderSchedule.Local(date,_displayZone(),"MM-dd HH:mm");
    private string ExactTime(DateTimeOffset? date) => date is {} at ? DailyReminderSchedule.Local(at,_displayZone(),"yyyy-MM-dd HH:mm:ss zzz") : "未知";

    private static IReadOnlyList<AccountOption> DemoAccounts() => new[]
    {
        new AccountOption { Handle = new("demo-wuwa-a", 1, 1, 1), Identity = new("100000001", "演示漂泊者", 72) },
        new AccountOption { Handle = new("demo-wuwa-b", 2, 1, 2), Identity = new("100000002", "另一位演示漂泊者", 61) },
        new AccountOption { Handle = new("demo-wuwa-expired", 3, 1, 3), Problem = "演示失效账号 · 请先更新官方启动器授权，再重新扫描。" }
    };

    private static RoleSnapshot DemoSnapshot(PlayerIdentity identity)
    {
        var first = identity.Uid == "100000001";
        var now = DateTimeOffset.UtcNow;
        return new RoleSnapshot
        {
            Identity = identity, FetchedAtUtc = now, Energy = first ? 128 : 240, MaxEnergy = 240,
            StoreEnergy = first ? 236 : 15, MaxStoreEnergy = 480, Liveness = first ? 80 : 100,
            LivenessMaxCount = 100, LivenessUnlock = true, BattlePassLevel = first ? 42 : 25,
            WeekExp = first ? 5600 : 8800, WeekMaxExp = 12000, WeeklyInstCount = first ? 2 : 0,
            EnergyFullAtUtc = first ? now.AddHours(11).AddMinutes(12) : null,
            StoreEnergyFullAtUtc = now.AddHours(first ? 31 : 93).AddMinutes(8),
            DailyState = first ? "incomplete" : "complete"
        };
    }
}

internal interface IWuwaAdapter : IDisposable
{
    Task<IReadOnlyList<AccountOption>> DiscoverAccountsAsync(CancellationToken cancellationToken);
    Task<RoleSnapshot> QueryAsync(AccountHandle account, CancellationToken cancellationToken);
}

internal sealed class WuwaLauncherAdapter : IWuwaAdapter
{
    private readonly LauncherClient _client = new();
    public Task<IReadOnlyList<AccountOption>> DiscoverAccountsAsync(CancellationToken cancellationToken) =>
        _client.DiscoverAccountsAsync(cancellationToken);
    public Task<RoleSnapshot> QueryAsync(AccountHandle account, CancellationToken cancellationToken) =>
        _client.QueryAsync(account, cancellationToken);
    public void Dispose() => _client.Dispose();
}
