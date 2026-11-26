using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GameDailyWidget;

internal sealed record EventNotice(string Title, string Body, string Game, string? ClaimKey, string Tag, DateTimeOffset Expires);
internal sealed class EventDeadlineReminder(EventStore store, Func<bool> enabled, Func<EventNotice, bool> send, Func<TimeZoneInfo> zone)
{
    private static readonly int[] Hours = [72, 24, 3];
    private readonly Dictionary<string, int> _attempts = new();
    private readonly Dictionary<string, (DateTimeOffset At, string[] Keys)> _submissions = new();

    internal static IEnumerable<(int Hours, string Key)> Due(GameEvent item, DateTimeOffset now)
    {
        if (item.Start > now || item.Unknown || item.Deadline is not {} deadline || deadline <= now) yield break;
        foreach (var hours in Hours)
            if (deadline - now <= TimeSpan.FromHours(hours))
                yield return (hours, item.Key + "|" + deadline.ToOffset(EventParser.China).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) + "|" + hours + "h");
    }

    internal void Tick(DateTimeOffset now)
    {
        if (!enabled()) return;
        var data = store.Data;
        var rows = new List<(GameEvent Item, string[] Keys)>();
        foreach (var item in data.Events)
        {
            var mark = data.Marks.GetValueOrDefault(item.Key);
            if (mark is { Ignored: true } or { Claimed: true }) continue;
            var due = Due(item, now).Where(d => !data.Sent.ContainsKey(d.Key)).ToArray();
            if (due.Length == 0 || _attempts.GetValueOrDefault(due[^1].Key) >= 3) continue;
            rows.Add((item, due.Select(d => d.Key).ToArray()));
        }
        if (rows.Count == 0) return;
        rows.Sort((a,b) => Nullable.Compare(a.Item.Deadline, b.Item.Deadline));
        var remaining = rows[0].Item.Deadline!.Value - now;
        var title = remaining < TimeSpan.FromHours(3) ? "剩不到 3 小时" :
            remaining < TimeSpan.FromHours(24) ? "剩不到 1 天" :
            remaining < TimeSpan.FromHours(72) ? "剩不到 3 天" : "剩 3 天";
        var body = rows.Take(4).Select(r => Describe(r.Item,zone())).ToList();
        if (rows.Count > 4) body.Add($"另有 {rows.Count - 4} 个");
        var keys = rows.SelectMany(r => r.Keys).ToArray();
        var tag = "evt-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",keys.Order()))))[..12];
        var notice = new EventNotice("限时活动 · " + title, string.Join("\n", body), rows[0].Item.Game,
            rows.Count == 1 ? rows[0].Item.Key : null, tag, rows.Max(r => r.Item.Deadline!.Value));
        foreach (var row in rows) _attempts[row.Keys[^1]] = _attempts.GetValueOrDefault(row.Keys[^1]) + 1;
        if (!send(notice)) return;
        foreach (var key in keys) data.Sent[key] = now;
        _submissions[tag] = (now, keys);
        store.Save();
    }

    internal void DeliveryFailed(string tag)
    {
        if (!_submissions.Remove(tag, out var receipt)) return;
        foreach (var key in receipt.Keys)
            if (store.Data.Sent.GetValueOrDefault(key) == receipt.At) store.Data.Sent.Remove(key);
        store.Save();
    }

    internal static string DeadlineTip(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = DailyReminderSchedule.Local(at,zone,"MM-dd HH:mm");
        return zone.GetUtcOffset(at) == EventParser.China ? local : $"{local}（北京时间 {at.ToOffset(EventParser.China):MM-dd HH:mm}）";
    }

    private static string Describe(GameEvent item, TimeZoneInfo zone)
    {
        var reward = item.Currency.Amount is {} n ? $" · {item.Currency.Name}×{n}" : "";
        var action = item.ClaimDeadline != null ? "领奖截止" : "结束";
        return $"{(item.Game == "wuwa" ? "鸣潮" : "终末地")}「{item.Name}」本地 {DailyReminderSchedule.Local(item.Deadline!.Value,zone,"MM/dd HH:mm")} {action}{reward}";
    }
}
