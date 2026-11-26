using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameDailyWidget;

internal interface IDailyHistoryStore
{
    string? Load();
    void Save(string json);
}

internal sealed class DailyHistoryFileStore : IDailyHistoryStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameDailyWidget", "daily-history.json");

    public string? Load() => File.Exists(_path) ? File.ReadAllText(_path) : null;

    public void Save(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal sealed record DailyResetSpec(TimeSpan ServerOffset, TimeSpan ResetAt)
{
    internal DateOnly DayAt(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(ServerOffset).DateTime.Subtract(ResetAt));
}

/// <summary>
/// Counts consecutive game days that this application actually observed at the
/// daily activity threshold. Missing days remain unknown, never inferred failures
/// or completed days. This module makes no API requests and stores no identity text.
/// </summary>
public sealed class DailyStreakTracker
{
    private const string LocalSource = "local-observation";
    private readonly bool _preview;
    private readonly IDailyHistoryStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly IReadOnlyDictionary<string, DailyResetSpec> _resets;
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    private DailyHistoryDocument _history = new();
    private bool _loaded;
    private bool _loadFailed;
    private bool _saveFailed;

    // This product currently reads the Chinese servers. A fixed UTC offset avoids
    // dependence on the Windows time zone or daylight-saving clock changes.
    internal static IReadOnlyDictionary<string, DailyResetSpec> ChineseResets { get; } =
        new Dictionary<string, DailyResetSpec>(StringComparer.Ordinal)
        {
            ["wuwa"] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(4)),
            ["endfield"] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(4))
        };

    public DailyStreakTracker(bool preview)
        : this(preview, new DailyHistoryFileStore(), () => DateTimeOffset.UtcNow, ChineseResets) { }

    internal DailyStreakTracker(bool preview, IDailyHistoryStore store, Func<DateTimeOffset> now,
        IReadOnlyDictionary<string, DailyResetSpec>? resets = null)
    {
        _preview = preview;
        _store = store;
        _now = now;
        _resets = resets ?? ChineseResets;
    }

    public void Observe(GameViewState state)
    {
        // These guards precede even lazy storage loading, so preview/demo cannot
        // inspect a real account's history merely by changing a UI selection.
        if (_preview || state.Demo || !state.LoggedIn || state.Busy || state.Error != null || state.Stale
            || state.UpdatedAt is not { } fetchedAt || !TryAccount(state, out var key, out var reset)) return;
        var now = _now();
        if (fetchedAt > now || now - fetchedAt > TimeSpan.FromMinutes(15)
            || reset.DayAt(fetchedAt) != reset.DayAt(now)) return;
        if (state.DailyState is not ("complete" or "incomplete") || state.Daily is not { } daily || daily < 0
            || state.MaxDaily is not > 0) return;
        var complete = state.DailyState == "complete" && daily >= state.MaxDaily.Value;
        // A contradictory state is unknown rather than evidence of either result.
        if ((state.DailyState == "complete") != (daily >= state.MaxDaily.Value)) return;
        EnsureLoaded();
        if (_lastSeen.TryGetValue(key, out var seen) && fetchedAt <= seen) return;
        _lastSeen[key] = fetchedAt;
        if (!_history.Accounts.TryGetValue(key, out var account))
        {
            account = new DailyAccountHistory { GameKey = state.GameKey };
            _history.Accounts.Add(key, account);
        }
        var date = DateKey(reset.DayAt(fetchedAt));
        if (account.Days.TryGetValue(date, out var previous))
        {
            // Observed completion is irreversible within a game day. A subsequent
            // lower/stale value cannot revoke it; repeated refreshes do not write.
            if (previous.Complete || !complete)
            {
                if (_saveFailed) Save();
                return;
            }
        }
        account.Days[date] = new DailyObservation { Complete = complete, ObservedAtUtc = fetchedAt.ToUniversalTime() };
        Save();
    }

    public string Describe(GameViewState state)
    {
        if (state.Demo) return "连续达标 3 天 · 演示记录";
        if (_preview) return "连续天数待记录 · 界面预览";
        if (!state.LoggedIn) return "登录后记录连续达标天数";
        if (!TryAccount(state, out var key, out var reset)) return "身份未确认 · 连续天数待记录";
        EnsureLoaded();
        var suffix = _loadFailed ? " · 历史不可读，仅本次会话记录"
            : _saveFailed ? " · 未能保存，仅本次会话记录" : " · 本地记录";
        if (!_history.Accounts.TryGetValue(key, out var account) || account.Days.Count == 0)
            return "连续天数待记录 · 从首次实际确认开始" + (_loadFailed ? suffix : "");
        var today = reset.DayAt(_now());
        var todayKey = DateKey(today);
        account.Days.TryGetValue(todayKey, out var todayObservation);
        if (todayObservation?.Complete == true)
        {
            var count = CountEndingAt(account, today);
            var interrupted = HasEarlierObservation(account, today.AddDays(-count - 1));
            return $"连续达标 {count} 天" + (interrupted ? " · 记录中断后重新计数" : "") + suffix;
        }
        var todayStatus = todayObservation != null ? "今日尚未达标" : "今日待确认";
        var yesterday = today.AddDays(-1);
        var previousCount = CountEndingAt(account, yesterday);
        if (previousCount > 0)
            return $"连续达标 {previousCount} 天（截至昨日） · {todayStatus}" + suffix;
        if (HasEarlierObservation(account, yesterday))
            return "记录中断（此前日期未确认达标） · " + todayStatus + suffix;
        return (todayObservation == null ? "连续天数待记录" : "连续达标 0 天") + " · " + todayStatus + suffix;
    }

    private bool TryAccount(GameViewState state, out string key, out DailyResetSpec reset)
    {
        key = "";
        reset = null!;
        if (!_resets.TryGetValue(state.GameKey, out reset!)) return false;
        string? identity = null;
        if (state.GameKey == "wuwa")
        {
            // CopyIdentity is the verified game UID. SelectedAccountKey belongs to
            // an authorization-cache entry and changes when its authorization rotates.
            var uid = state.CopyIdentity?.Trim();
            if (!string.IsNullOrWhiteSpace(uid) && uid.Length <= 32 && uid.All(c => c is >= '0' and <= '9')) identity = uid;
        }
        else if (state.GameKey == "endfield")
        {
            identity = state.SelectedAccountKey;
            if (string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(state.CopyIdentity))
            {
                var server = state.Details.FirstOrDefault(detail => detail.Label == "服务器 ID")?.Value;
                if (string.IsNullOrWhiteSpace(server))
                {
                    var hint = state.Details.FirstOrDefault(detail => detail.Label == "服务器")?.Hint;
                    const string prefix = "服务器 ID：";
                    if (hint?.StartsWith(prefix, StringComparison.Ordinal) == true) server = hint[prefix.Length..];
                }
                if (!string.IsNullOrWhiteSpace(server)) identity = JsonSerializer.Serialize(new[] { server, state.CopyIdentity });
            }
        }
        if (string.IsNullOrWhiteSpace(identity)) return false;
        var serialized = JsonSerializer.Serialize(new[] { state.GameKey, identity.Trim() });
        key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
        return true;
    }

    private void EnsureLoaded()
    {
        if (_loaded || _preview) return;
        _loaded = true;
        try
        {
            var json = _store.Load();
            if (string.IsNullOrWhiteSpace(json)) return;
            var loaded = JsonSerializer.Deserialize<DailyHistoryDocument>(json);
            if (loaded == null || loaded.Version != 1 || loaded.Source != LocalSource || loaded.Accounts == null)
                throw new JsonException("Unsupported local observation history.");
            foreach (var pair in loaded.Accounts)
            {
                if (!IsHash(pair.Key) || pair.Value?.Days == null || !_resets.TryGetValue(pair.Value.GameKey, out var reset))
                    throw new JsonException("Invalid local observation identity.");
                foreach (var day in pair.Value.Days)
                {
                    if (!DateOnly.TryParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        || day.Value == null || day.Value.ObservedAtUtc == default || reset.DayAt(day.Value.ObservedAtUtc) != date)
                        throw new JsonException("Invalid local observation date.");
                    if (!_lastSeen.TryGetValue(pair.Key, out var previous) || previous < day.Value.ObservedAtUtc)
                        _lastSeen[pair.Key] = day.Value.ObservedAtUtc;
                }
            }
            _history = loaded;
        }
        catch
        {
            // Preserve a damaged/unreadable file instead of silently overwriting
            // its possible history. New observations remain visible in memory.
            _loadFailed = true;
            _history = new();
            _lastSeen.Clear();
        }
    }

    private void Save()
    {
        if (_preview || _loadFailed) return;
        try
        {
            _store.Save(JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true }));
            _saveFailed = false;
        }
        catch { _saveFailed = true; }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string DateKey(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static int CountEndingAt(DailyAccountHistory account, DateOnly date)
    {
        var count = 0;
        while (account.Days.TryGetValue(DateKey(date), out var observation) && observation.Complete)
        {
            count++;
            if (date == DateOnly.MinValue) break;
            date = date.AddDays(-1);
        }
        return count;
    }

    private static bool HasEarlierObservation(DailyAccountHistory account, DateOnly atOrBefore) =>
        account.Days.Keys.Any(key => DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture) <= atOrBefore);

    internal sealed class DailyHistoryDocument
    {
        public int Version { get; set; } = 1;
        public string Source { get; set; } = LocalSource;
        public Dictionary<string, DailyAccountHistory> Accounts { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class DailyAccountHistory
    {
        public string GameKey { get; set; } = "";
        public Dictionary<string, DailyObservation> Days { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class DailyObservation
    {
        // false means "not confirmed at the observed time", not a final daily failure.
        public bool Complete { get; set; }
        public DateTimeOffset ObservedAtUtc { get; set; }
    }
}
