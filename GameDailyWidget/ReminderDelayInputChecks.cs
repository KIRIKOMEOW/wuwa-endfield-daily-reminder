using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GameDailyWidget;

/// <summary>Pure parser checks: no windows, settings files, credentials or external requests.</summary>
public static class ReminderDelayInputChecks
{
    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        foreach (var expected in new[] { 1, 30, 60, 86400 })
        {
            Assert(ReminderDelayWindow.TryReadSeconds(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), out var actual)
                && actual == expected, "Valid reminder delay was rejected or changed.");
            results.Add($"PASS 提醒延时输入接受 {expected} 秒");
        }
        Assert(ReminderDelayWindow.TryReadSeconds(" \t60\r\n ", out var trimmed) && trimmed == 60,
            "Surrounding whitespace must be accepted without changing seconds.");
        results.Add("PASS 提醒延时输入允许首尾空白");
        Reject(new string?[] { null, "", " ", "\t\r\n" });
        results.Add("PASS 提醒延时输入拒绝空值和纯空白");
        Reject(new[] { "-1", "-30", "-2147483648" });
        results.Add("PASS 提醒延时输入拒绝负数");
        Reject(new[] { "1.5", "30.0", "1,5" });
        results.Add("PASS 提醒延时输入拒绝小数");
        Reject(new[] { "2147483648", "999999999999999999999999999999" });
        results.Add("PASS 提醒延时输入拒绝整数溢出");
        Reject(new[] { "0", "86401", "2147483647" });
        results.Add("PASS 提醒延时输入拒绝0和超过86400秒的值");
        Reject(new[] { "+30", "1e2", "30秒", "60 0", "３０", "0x1E" });
        results.Add("PASS 提醒延时输入拒绝非十进制整数格式");
        Assert(ReminderDelayWindow.TryReadSeconds("00030", out var padded) && padded == 30,
            "Leading zeros must retain the integer value.");
        results.Add("PASS 提醒延时输入允许前导零且仍按整数秒解释");
        return Task.FromResult<IReadOnlyList<string>>(results.AsReadOnly());
    }

    private static void Reject(IEnumerable<string?> inputs)
    {
        foreach (var input in inputs)
            Assert(!ReminderDelayWindow.TryReadSeconds(input, out var seconds) && seconds == 0,
                "Invalid reminder input was accepted or returned a usable value.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
