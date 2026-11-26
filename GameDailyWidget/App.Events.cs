using System;
using System.IO;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

public partial class App
{
    private EventStore? _eventStore;
    private EventFeed? _eventFeed;
    private ArtCache? _artCache;
    private EventSyncCoordinator? _eventSync;
    private EventDeadlineReminder? _deadlineReminder;
    private const string ActivationPipe = "GameDailyWidget.Events.v1";
    private static string EventsPath => Path.Combine(UnifiedSettings.DataDirectory,"events.json");

    // Read activation before constructing a visible window. A cold background
    // claim only needs the event file and the normal single-writer lock.
    private static async Task<(string? Game, EventActivation? Event)> ReadColdActivation()
    {
        var activation = new TaskCompletionSource<(string?,EventActivation?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new NativeDailyNotifications(game => activation.TrySetResult((game,null)));
        listener.EventActivated += action => activation.TrySetResult((null,action));
        if (!listener.RegisterActivation()) throw new InvalidOperationException();
        return await activation.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private void AttachEvents(UnifiedSettings settings, string[] args)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(EventParser.China);
        var progressDemo = IsPreview && args.Contains("--events-progress");
        _eventStore = IsPreview ? EventPreviewData.Create(now,args.Contains("--events-claimed"),args.Contains("--events-failed"),args.Contains("--empty"),
            int.TryParse(Value(args,"--event-count"),out var count) ? Math.Clamp(count,1,100) : 3,progressDemo)
            : new EventStore(EventsPath);
        if (!IsPreview)
        {
            _eventFeed = new EventFeed();
            _eventSync = new(_eventStore,_eventFeed,_lifetime.Token);
            _artCache = new ArtCache();
            _widget!.BindArt(_artCache,_eventSync.GalleryAsync);
            _eventSync.SetOfficialArtAsync = _widget.SetOfficialArtAsync;
            _deadlineReminder = new(_eventStore,() => settings.EventReminderEnabled, notice =>
            {
                var result = _notifications!.ShowEvent(notice);
                if (!result.Submitted) _widget?.Note(result.Status);
                return result.Submitted;
            },_displayZone);
            _notifications!.EventActivated += QueueEventActivation;
            _notifications.EventDeliveryFailed += tag => Dispatcher.BeginInvoke(new Action(() =>
            {
                _deadlineReminder.DeliveryFailed(tag);
                _widget?.Note("Windows 未能显示活动提醒，请检查系统通知设置");
            }));
            void UpdateProgress(DateTimeOffset at) => _widget!.SetEventProgress(
                EndfieldEventProgress.Apply(_eventStore,_endfield!.EventProgress,_endfield.State,at));
            _endfield!.Changed += () => UpdateProgress(DateTimeOffset.UtcNow);
            _eventSync.Changed += () => UpdateProgress(DateTimeOffset.UtcNow);
            _resetRefresh!.ClockTick += UpdateProgress;
            _resetRefresh.ClockTick += _eventSync.Tick;
            _resetRefresh.ClockTick += _deadlineReminder.Tick;
            _ = ListenEventActivations(_lifetime.Token);
        }
        if (IsPreview) _widget!.BindArt(null,null);
        _widget!.BindEvents(_eventStore,_eventSync);
        if (progressDemo) _widget.SetEventProgress(EventPreviewData.AddProgressPreview(_eventStore,now,args.Contains("--events-auto")));
        _eventSync?.Tick(now);
    }

    private void QueueEventActivation(EventActivation action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting || _widget == null || _eventStore == null) return;
            if (action.Demo && !IsPreview)
            {
                if (action.ClaimKey == null && Environment.ProcessPath is {} exe)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--demo --events --test-secondary --face " + action.Game));
                return;
            }
            if (action.ClaimKey is {} key)
            {
                var item = _eventStore.Data.Events.FirstOrDefault(e => e.Key == key && e.Game == action.Game);
                if (item != null) _widget.SetMark(item,true,_eventStore.Data.Marks.GetValueOrDefault(key)?.Ignored == true);
                return;
            }
            _notificationActivated = true;
            _notificationGame = action.Game;
            ShowWidget();
            _widget.ShowGame(action.Game,false);
            _widget.OpenEvents();
        }));
    }

    private static async Task ForwardEvent(EventActivation action)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".",ActivationPipe,PipeDirection.Out,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1500);
            await JsonSerializer.SerializeAsync(pipe,action);
        }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    private async Task ListenEventActivations(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(ActivationPipe,PipeDirection.In,1,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancel);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var action = await JsonSerializer.DeserializeAsync<EventActivation>(pipe,cancellationToken:timeout.Token);
                if (action?.Game is "wuwa" or "endfield" && action.ClaimKey?.Length is not > 512) QueueEventActivation(action);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { }
            catch (JsonException) { }
            catch (IOException) { await Task.Delay(250); }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
