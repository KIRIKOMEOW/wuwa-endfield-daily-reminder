using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameDailyWidget;

internal sealed record DailySlot(string Id, DateTimeOffset At);

internal static class DailyReminderSchedule
{
    internal static readonly TimeSpan China = TimeSpan.FromHours(8);
    internal static DateTimeOffset NextReset(DateTimeOffset now)
    {
        var china = now.ToOffset(China);
        var reset = new DateTimeOffset(china.Date.AddHours(4), China);
        return now < reset ? reset : reset.AddDays(1);
    }

    internal static string? Normalize(string? input)
    {
        return TimeOnly.TryParseExact(input?.Trim(),["H:mm","HH:mm"],CultureInfo.InvariantCulture,
            DateTimeStyles.None,out var value) ? value.ToString("HH:mm",CultureInfo.InvariantCulture) : null;
    }

    internal static string Local(DateTimeOffset at, TimeZoneInfo zone, string format = "HH:mm") =>
        TimeZoneInfo.ConvertTime(at,zone).ToString(format,CultureInfo.InvariantCulture);

    internal static string ResetText(DateTimeOffset now, TimeZoneInfo zone)
    {
        var reset = NextReset(now);
        return zone.GetUtcOffset(reset) == China ? "北京时间 04:00" : $"本地 {Local(reset,zone)}（北京时间 04:00）";
    }

    internal static string Remaining(DateTimeOffset now)
    {
        int minutes = (int)Math.Ceiling((NextReset(now) - now).TotalMinutes);
        if (minutes < 60) return $"{minutes} 分钟";
        return minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes / 60} 小时 {minutes % 60} 分";
    }

    internal static List<DailySlot> Slots(DateTimeOffset now, TimeZoneInfo zone, List<string>? times)
    {
        var end = NextReset(now);
        var start = end.AddDays(-1);
        if (times == null)
            return new[] {30,15,5}.Select(m => new DailySlot("before-" + m,end.AddMinutes(-m))).ToList();
        var slots = new List<DailySlot>();
        var firstDate = TimeZoneInfo.ConvertTime(start,zone).Date;
        var lastDate = TimeZoneInfo.ConvertTime(end,zone).Date;
        foreach (var time in times)
        {
            if (!TimeOnly.TryParseExact(time,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var clock)) continue;
            for (var day = firstDate; day <= lastDate; day = day.AddDays(1))
            {
                var wall = DateTime.SpecifyKind(day.Add(clock.ToTimeSpan()),DateTimeKind.Unspecified);
                while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(1);
                var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
                var at = new DateTimeOffset(wall,offset);
                if (at <= start || at >= end) continue;
                slots.Add(new(time,at));
                break;
            }
        }
        return slots.OrderBy(s => s.At).ToList();
    }

    internal static List<string> DisplayTimes(List<string>? times, DateTimeOffset now, TimeZoneInfo zone) =>
        times?.ToList() ?? Slots(now,zone,null).Select(slot => Local(slot.At,zone)).ToList();
}

// A malformed preference must not discard the rest of settings.json.
internal sealed class ReminderTimesConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return null;
        var times = new List<string>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && DailyReminderSchedule.Normalize(item.GetString()) is {} time &&
                times.Count < 5 && !times.Contains(time)) times.Add(time);
        }
        return root.GetArrayLength() > 0 && times.Count == 0 ? null : times;
    }
    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer,value);
}
