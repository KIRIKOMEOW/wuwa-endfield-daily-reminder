using System;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

/// <summary>A one-shot reminder whose configurable deadline keeps the original launch timestamp.</summary>
internal sealed class LaunchReminderTimer : IDisposable
{
    private readonly long _startedAt;
    private readonly Action _onDue;
    private readonly CancellationToken _lifetime;
    private readonly Func<long, CancellationToken, int, Task> _wait;
    private readonly Action? _onError;
    private CancellationTokenSource? _pending;
    private Task _completion = Task.CompletedTask;
    private long _revision;
    private bool _finished;
    private bool _disposed;

    internal LaunchReminderTimer(long startedAt, Action onDue, CancellationToken lifetime,
        Func<long, CancellationToken, int, Task>? wait = null, Action? onError = null)
    {
        _startedAt = startedAt;
        _onDue = onDue ?? throw new ArgumentNullException(nameof(onDue));
        _lifetime = lifetime;
        _wait = wait ?? ((start, token, seconds) => LaunchReminderDelay.WaitAsync(start, token, seconds));
        _onError = onError;
    }

    // Tests can await the specific run they captured, including a superseded run
    // whose injected waiter deliberately ignores cancellation.
    internal Task CompletionForChecks => _completion;

    internal void UpdateDelay(int seconds)
    {
        if (_disposed || _finished || _lifetime.IsCancellationRequested) return;
        var revision = ++_revision;
        var previous = _pending;
        _pending = null;
        Cancel(previous);
        // Cancel callbacks may synchronously dispose or reschedule this timer.
        if (_disposed || _finished || _lifetime.IsCancellationRequested || revision != _revision) return;
        CancellationTokenSource source;
        try { source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime); }
        catch
        {
            _completion = Task.CompletedTask;
            ReportError();
            return;
        }
        _pending = source;
        _completion = RunAsync(revision, source, LaunchReminderDelay.NormalizeSeconds(seconds));
    }

    private async Task RunAsync(long revision, CancellationTokenSource source, int seconds)
    {
        try
        {
            if (!IsCurrent(revision, source)) return;
            await _wait(_startedAt, source.Token, seconds);
            if (!IsCurrent(revision, source)) return;
            // Set this before user code: reentrant updates must never send twice.
            _finished = true;
            _onDue();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (IsCurrent(revision, source)) ReportError();
        }
        finally
        {
            if (ReferenceEquals(_pending, source)) _pending = null;
            source.Dispose();
        }
    }

    private bool IsCurrent(long revision, CancellationTokenSource source) =>
        !_disposed && revision == _revision && ReferenceEquals(_pending, source) &&
        !_lifetime.IsCancellationRequested && !source.IsCancellationRequested;

    private void ReportError()
    {
        // An error handler must not fault an unobserved background run.
        try { _onError?.Invoke(); }
        catch { }
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
        // Each RunAsync owns and disposes its source in finally.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_revision;
        Cancel(_pending);
    }
}
