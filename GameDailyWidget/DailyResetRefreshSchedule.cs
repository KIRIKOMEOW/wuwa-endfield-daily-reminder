using System;

namespace GameDailyWidget;

internal enum DailyResetRefreshMode
{
    BoundaryOnly,
    TwentySecondBurst
}

/// <summary>Claims the latest reset slot in UTC+8 without replaying missed slots.</summary>
internal sealed class DailyResetRefreshSchedule
{
    private static readonly TimeSpan ServerOffset = TimeSpan.FromHours(8);
    private const long ResetTicks = 4 * TimeSpan.TicksPerHour;
    private const long SlotTicks = 20 * TimeSpan.TicksPerSecond;
    private const long BoundaryWindowTicks = 60 * TimeSpan.TicksPerSecond;
    private const long BurstWindowTicks = 62 * TimeSpan.TicksPerSecond;
    private readonly DailyResetRefreshMode _mode;
    private long _highWatermark = -1;

    internal DailyResetRefreshSchedule(DailyResetRefreshMode mode)
    {
        if (mode is not (DailyResetRefreshMode.BoundaryOnly or DailyResetRefreshMode.TwentySecondBurst))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
    }

    internal bool TryTake(DateTimeOffset now)
    {
        var serverNow = now.ToOffset(ServerOffset);
        var elapsedTicks = serverNow.TimeOfDay.Ticks - ResetTicks;
        var windowTicks = _mode == DailyResetRefreshMode.TwentySecondBurst
            ? BurstWindowTicks : BoundaryWindowTicks;
        if (elapsedTicks < 0 || elapsedTicks >= windowTicks) return false;

        var slot = _mode == DailyResetRefreshMode.TwentySecondBurst ? elapsedTicks / SlotTicks : 0;
        var key = (long)DateOnly.FromDateTime(serverNow.Date).DayNumber * 4 + slot;
        if (key <= _highWatermark) return false;
        _highWatermark = key;
        return true;
    }

    internal static bool IsBurstWindow(DateTimeOffset now)
    {
        var elapsedTicks = now.ToOffset(ServerOffset).TimeOfDay.Ticks - ResetTicks;
        return elapsedTicks >= 0 && elapsedTicks < BurstWindowTicks;
    }
}
