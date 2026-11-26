using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GameDailyWidget;

// All transitions run on the widget dispatcher. Reserve a slot before starting
// either request so a restart cannot repeat a completed or in-flight check.
internal sealed class PreResetDailyReminder(IGameController wuwa, IGameController endfield,
    UnifiedSettings settings, Func<DailyReminderMessage, DateTimeOffset, DateTimeOffset, bool> send,
    Func<TimeZoneInfo> zone)
{
    private bool _first = true;
    private DateTimeOffset _reset;
    private TimeZoneInfo? _zone;
    private List<DailySlot> _slots = [];
    private DailySlot? _pending;
    private bool _catchUp;
    private DateTimeOffset _started;
    private Task _refresh = Task.CompletedTask;
    // A wake-up catch-up usually starts before the network reconnects, and the
    // regular five-minute poll is too slow to land inside the five-minute wait.
    private static readonly TimeSpan[] CatchUpRetries =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(105), TimeSpan.FromSeconds(225)];
    private int _retries;

    internal void SettingsChanged(DateTimeOffset now)
    {
        _pending = null;
        _zone = null;
        // An edit does not retroactively schedule earlier local times.
        if (settings.LastPreResetReminderSlot == null || settings.LastPreResetReminderSlot < now)
            settings.LastPreResetReminderSlot = now.AddTicks(-1);
        settings.Save();
    }

    internal void InvalidateSchedule() => _zone = null;

    internal void Tick(DateTimeOffset now)
    {
        var currentZone = zone();
        var reset = DailyReminderSchedule.NextReset(now);
        if (_reset != reset || !ReferenceEquals(currentZone,_zone))
        {
            if (_reset != reset) _pending = null;
            _zone = currentZone;
            _reset = reset;
            _slots = DailyReminderSchedule.Slots(now,currentZone,settings.DailyReminderTimes);
            if (_pending != null && !_slots.Contains(_pending)) _pending = null;
        }
        var day = StartupDailyReminder.GameDayKey(now);
        bool AlreadyChecked(DailySlot s) => s.At <= settings.LastPreResetReminderSlot ||
            (settings.HandledReminderDay == day && settings.HandledReminderTimes?.Contains(s.Id) == true);
        var due = _slots.Where(s => s.At <= now && !AlreadyChecked(s)).ToList();
        var startup = _first;
        _first = false;
        if (due.Count > 0)
        {
            var slot = due[^1];
            _catchUp = now - slot.At >= TimeSpan.FromMinutes(1);
            if (settings.HandledReminderDay != day || settings.HandledReminderTimes == null)
            {
                settings.HandledReminderDay = day;
                settings.HandledReminderTimes = [];
            }
            settings.HandledReminderTimes.AddRange(due.Select(s => s.Id));
            settings.LastPreResetReminderSlot = slot.At;
            settings.Save();
            _pending = null;
            if (startup && _catchUp && settings.StartupReminderEnabled) return;
            _pending = slot;
            _started = now;
            _retries = 0;
            _refresh = Task.WhenAll(DailyResetRefreshCoordinator.RefreshIfReadyAsync(wuwa),
                DailyResetRefreshCoordinator.RefreshIfReadyAsync(endfield));
        }
        if (_pending is not {} pending) return;
        var age = now - _started;
        if (age < TimeSpan.Zero || (_catchUp ? age >= TimeSpan.FromMinutes(5) : age >= TimeSpan.FromMinutes(1)) ||
            _slots.Any(s => s.At > pending.At && s.At <= now))
        {
            _pending = null;
            return;
        }
        var first = wuwa.State;
        var second = endfield.State;
        if ((!_refresh.IsCompleted || first.Busy || second.Busy) && age < TimeSpan.FromSeconds(25)) return;
        var missed = !_catchUp ? null : pending.Id.StartsWith("before-",StringComparison.Ordinal)
            ? DailyReminderSchedule.Local(pending.At,currentZone) : pending.Id;
        var message = StartupDailyReminder.ComposeBeforeReset(first,second,_started,now,currentZone,missed);
        if (message != null)
        {
            _pending = null;
            send(message,now,pending.At);
        }
        else
        {
            var wuwaDone = StartupDailyReminder.ResolvedSince(first,"wuwa",_started,now);
            var endfieldDone = StartupDailyReminder.ResolvedSince(second,"endfield",_started,now);
            if (!_catchUp || (wuwaDone && endfieldDone)) _pending = null;
            else if (_refresh.IsCompleted && _retries < CatchUpRetries.Length && age >= CatchUpRetries[_retries])
            {
                // Retry only the game still missing a trusted reading; busy or
                // signed-out controllers are skipped by RefreshIfReadyAsync.
                _retries++;
                _refresh = Task.WhenAll(
                    wuwaDone ? Task.CompletedTask : DailyResetRefreshCoordinator.RefreshIfReadyAsync(wuwa),
                    endfieldDone ? Task.CompletedTask : DailyResetRefreshCoordinator.RefreshIfReadyAsync(endfield));
            }
        }
    }
}
