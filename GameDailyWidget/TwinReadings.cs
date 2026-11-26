using System;
using System.Collections.Generic;
using System.Linq;

namespace GameDailyWidget;

internal static class TwinReadings
{
    internal static bool HasAccount(GameViewState s) => s.LoggedIn || s.Demo && s.Accounts.Count != 0;

    internal static double? Fraction(int? current, int? max) => current.HasValue && current >= 0 && max > 0
        ? Math.Clamp((double)current.Value / max.Value, 0, 1) : null;
    internal static double? DailyFraction(GameViewState s) => HasAccount(s) && s.DailyState is "complete" or "incomplete"
        ? Fraction(s.Daily, s.MaxDaily) : null;

    internal static string Countdown(DateTimeOffset now)
    {
        var t=TimeSpan.FromSeconds(Math.Ceiling((DailyReminderSchedule.NextReset(now)-now).TotalSeconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
    internal static string ResetCaption(DateTimeOffset now, TimeZoneInfo zone)
    {
        var reset = DailyReminderSchedule.NextReset(now);
        return zone.GetUtcOffset(reset) == DailyReminderSchedule.China ? "北京时间 04:00" :
            "北京 04:00 · 本地 " + DailyReminderSchedule.Local(reset, zone);
    }
}
