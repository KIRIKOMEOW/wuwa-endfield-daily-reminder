using System;
using System.Globalization;

namespace GameDailyWidget;

public sealed record DailyReminderMessage(string Title, string Body, string TargetGameKey);

/// <summary>
    /// Pure launch reminder policy. Activity reaching its threshold does not imply
/// that every task was performed or that rewards were claimed.
/// </summary>
public static class StartupDailyReminder
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private static readonly TimeSpan ResetAt = TimeSpan.FromHours(4);

    /// <summary>Both supported China servers change their game day at 04:00 UTC+8.</summary>
    public static string GameDayKey(DateTimeOffset now) =>
        now.ToOffset(ChinaOffset).Subtract(ResetAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Manual and automatic launches share this condition. The host schedules
    /// one reminder 30 seconds after each launch and uses Compose(force: true).
    /// There is no game-day suppression or dependence on a startup command flag.
    /// </summary>
    public static bool ShouldSend(bool preview, bool enabled) => !preview && enabled;

    public static DailyReminderMessage? Compose(
        GameViewState wuwa, GameViewState endfield, DateTimeOffset now, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(wuwa);
        ArgumentNullException.ThrowIfNull(endfield);
        var first = Classify(wuwa, "wuwa", now);
        var second = Classify(endfield, "endfield", now);
        if (!force && IsResolved(first) && IsResolved(second)) return null;

        // Prefer an actionable known shortfall, then an unknown/unlogged game.
        var target = first == Progress.Incomplete ? "wuwa"
            : second == Progress.Incomplete ? "endfield"
            : !IsResolved(first) ? "wuwa"
            : !IsResolved(second) ? "endfield"
            : "wuwa";
        return new DailyReminderMessage(
            force ? "今日进度概览" : "双游戏日常提醒",
            $"鸣潮：{Describe(first, wuwa)}\n终末地：{Describe(second, endfield)}",
            target);
    }

    private enum Progress { Unknown, Unlogged, Incomplete, Complete, Locked }

    internal static DailyReminderMessage? ComposeBeforeReset(
        GameViewState wuwa, GameViewState endfield, DateTimeOffset slot, DateTimeOffset now, TimeZoneInfo zone, string? missed = null)
    {
        // Require a sample from this check, including an ordinary poll that
        // was already running when the reminder minute began.
        bool Pending(GameViewState state, string game) =>
            state.UpdatedAt >= slot && Classify(state, game, now) == Progress.Incomplete;

        var first = Pending(wuwa, "wuwa");
        var second = Pending(endfield, "endfield");
        if (!first && !second) return null;
        var lines = new System.Collections.Generic.List<string>(2);
        if (first) lines.Add($"鸣潮：活跃度 {wuwa.Daily}/{wuwa.MaxDaily}");
        if (second) lines.Add($"终末地：活跃度 {endfield.Daily}/{endfield.MaxDaily}");
        if (missed != null) lines.Add("补发 " + missed + " 的提醒");
        lines.Add("刷新：" + DailyReminderSchedule.ResetText(now,zone));
        return new DailyReminderMessage($"日常还没清完 · 距刷新还有 {DailyReminderSchedule.Remaining(now)}",
            string.Join("\n", lines), first ? "wuwa" : "endfield");
    }

    internal static bool ResolvedSince(GameViewState state,string game,DateTimeOffset since,DateTimeOffset now) =>
        state.UpdatedAt >= since && IsResolved(Classify(state,game,now));

    private static bool IsResolved(Progress progress) => progress is Progress.Complete or Progress.Locked;

    private static Progress Classify(GameViewState state, string expectedGame, DateTimeOffset now)
    {
        if (!string.Equals(state.GameKey, expectedGame, StringComparison.Ordinal)) return Progress.Unknown;
        if (state.Demo) return Progress.Unknown;
        if (!state.LoggedIn) return Progress.Unlogged;
        if (state.Busy || state.Stale || state.Error != null || state.UpdatedAt is not { } updated)
            return Progress.Unknown;
        var age = now - updated;
        if (age < TimeSpan.Zero || age >= Freshness || GameDayKey(updated) != GameDayKey(now))
            return Progress.Unknown;

        // Only Wuwa exposes an authoritative daily-feature lock flag. Locked
        // activity has no meaningful progress maximum and must not be called pending.
        if (expectedGame == "wuwa" && state.DailyState == "locked") return Progress.Locked;
        if (state.Daily is not >= 0 || state.MaxDaily is not > 0) return Progress.Unknown;
        var reached = state.Daily.Value >= state.MaxDaily.Value;
        return state.DailyState switch
        {
            "complete" when reached => Progress.Complete,
            "incomplete" when !reached => Progress.Incomplete,
            _ => Progress.Unknown
        };
    }

    private static string Describe(Progress progress, GameViewState state) => progress switch
    {
        Progress.Unlogged => "未登录，请打开组件确认",
        Progress.Incomplete => $"今日待清（活跃度 {Number(state.Daily!.Value)}/{Number(state.MaxDaily!.Value)}）",
        Progress.Complete => $"今日活跃度已达标（活跃度 {Number(state.Daily!.Value)}/{Number(state.MaxDaily!.Value)}）",
        Progress.Locked => "日常活跃度未解锁",
        _ => "进度待确认，请打开组件确认"
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
