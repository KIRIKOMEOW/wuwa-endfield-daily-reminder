using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace GameDailyWidget;

// This clock is independent of request completion and the regular minute timers.
// It runs on the UI dispatcher, just like account selection and both controllers.
internal sealed class DailyResetRefreshCoordinator : IDisposable
{
    private readonly IGameController _wuwa;
    private readonly IGameController _endfield;
    private readonly bool _preview;
    private readonly Func<DateTimeOffset> _now;
    private readonly DailyResetRefreshSchedule _wuwaSchedule = new(DailyResetRefreshMode.TwentySecondBurst);
    private readonly DailyResetRefreshSchedule _endfieldSchedule = new(DailyResetRefreshMode.BoundaryOnly);
    private readonly DispatcherTimer? _clock;
    private bool _wuwaInFlight;
    private bool _endfieldInFlight;
    private bool _disposed;
    internal event Action<DateTimeOffset>? ClockTick;

    internal DailyResetRefreshCoordinator(IGameController wuwa, IGameController endfield, bool preview,
        Func<DateTimeOffset>? now = null, bool startTimer = true)
    {
        _wuwa = wuwa;
        _endfield = endfield;
        _preview = preview;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        if (preview) return;
        if (wuwa is WuwaController controller)
            controller.AutomaticRefreshSuppressed = () => DailyResetRefreshSchedule.IsBurstWindow(_now());
        if (startTimer)
        {
            _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += OnTick;
            _clock.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e) => Tick();

    internal void Tick()
    {
        if (_disposed || _preview) return;
        var now = _now();
        ClockTick?.Invoke(now);
        // Consume busy/missing-account Wuwa slots without queueing missed queries.
        if (_wuwaSchedule.TryTake(now) && !_wuwaInFlight && Ready(_wuwa))
            _ = RefreshSafelyAsync(_wuwa, true);
        // A single Endfield boundary query can wait briefly for in-flight work.
        if (!_endfieldInFlight && Ready(_endfield) && _endfieldSchedule.TryTake(now))
            _ = RefreshSafelyAsync(_endfield, false);
    }

    private static bool Ready(IGameController controller)
    {
        var state = controller.State;
        return state.LoggedIn && !state.Busy && !state.Demo;
    }

    internal static async Task RefreshIfReadyAsync(IGameController controller)
    {
        if (!Ready(controller)) return;
        try
        {
            if (controller is EndfieldController endfield) await endfield.RefreshScheduledAsync();
            else await controller.RefreshAsync();
        }
        catch { /* Each controller publishes its own failure. */ }
    }

    private async Task RefreshSafelyAsync(IGameController controller, bool wuwa)
    {
        if (wuwa) _wuwaInFlight = true;
        else _endfieldInFlight = true;
        try
        {
            await RefreshIfReadyAsync(controller);
        }
        finally
        {
            if (wuwa) _wuwaInFlight = false;
            else _endfieldInFlight = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClockTick = null;
        _clock?.Stop();
        if (_clock != null) _clock.Tick -= OnTick;
        if (!_preview && _wuwa is WuwaController controller) controller.AutomaticRefreshSuppressed = null;
    }
}
