using System;
using System.Linq;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace GameDailyWidget;

internal sealed record NativeNotificationResult(bool Submitted, string Status, string? Tag = null);

/// <summary>Uses Windows Toast/notification center. It never reads game credentials.</summary>
internal sealed class NativeDailyNotifications : IDisposable
{
    internal const string ReminderGroup = "game-daily";
    internal const string ReminderTag = "startup-daily";
    internal const string TestTag = "daily-test";
    private readonly Action<string> _activate;
    internal event Action<EventActivation>? EventActivated;
    internal event Action<string>? EventDeliveryFailed;
    private bool _registered;
    private bool _disposed;
    internal event Action<string>? DeliveryFailed;

    internal NativeDailyNotifications(Action<string> activate) => _activate = activate;

    internal bool RegisterActivation()
    {
        if (_disposed) return false;
        if (_registered) return true;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            _registered = true;
            return true;
        }
        catch { return false; }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        if (_disposed) return;
        var game = "wuwa";
        try
        {
            var arguments = ToastArguments.Parse(args.Argument);
            if (EventActivation.Parse(arguments) is {} events) { EventActivated?.Invoke(events); return; }
            if (arguments.Contains("view")) return;
            if (arguments.TryGetValue("game", out var value) && value == "endfield") game = value;
        }
        catch { return; }
        _activate(game);
    }

    internal NativeNotificationResult Show(DailyReminderMessage message, DateTimeOffset now, bool test = false, string? tag = null)
    {
        if (_disposed || !RegisterActivation()) return new(false, "Windows 通知初始化失败");
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
                return new(false, "Windows 已关闭此应用通知，请检查系统通知设置");
            var content = Content(message);
            var notification = new ToastNotification(content.GetXml())
            {
                Tag = tag ?? (test ? TestTag : ReminderTag),
                Group = ReminderGroup,
                ExpirationTime = test ? now.AddMinutes(10) : NextGameDay(now)
            };
            var day = StartupDailyReminder.GameDayKey(now);
            notification.Failed += (_, _) =>
            {
                if (!_disposed) DeliveryFailed?.Invoke(test ? "test" : day);
            };
            notifier.Show(notification);
            return new(true, "已发送 Windows 日常提醒", notification.Tag);
        }
        catch { return new(false, "Windows 通知未能发送，请检查系统通知设置"); }
    }

    internal NativeNotificationResult ShowEvent(EventNotice notice, bool demo = false)
    {
        if (_disposed || !RegisterActivation()) return new(false, "Windows 活动通知初始化失败");
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled) return new(false, "Windows 已关闭活动通知");
            var notification = new ToastNotification(EventNotification.Content(notice, demo).GetXml())
            {
                Tag = notice.Tag, Group = EventNotification.Group, ExpirationTime = notice.Expires
            };
            notification.Failed += (_, _) => { if (!_disposed) EventDeliveryFailed?.Invoke(notice.Tag); };
            notifier.Show(notification);
            return new(true, "已提交 Windows 活动通知", notice.Tag);
        }
        catch { return new(false, "活动提醒发送失败，请检查 Windows 通知设置"); }
    }

    internal bool IsInOwnNotificationHistory(string tag, string group = ReminderGroup)
    {
        try { return ToastNotificationManagerCompat.History.GetHistory().Any(item => item.Tag == tag && item.Group == group); }
        catch { return false; }
    }

    internal static ToastContent Content(DailyReminderMessage message) =>
        new ToastContentBuilder()
                .AddArgument("game", message.TargetGameKey == "endfield" ? "endfield" : "wuwa")
                .AddText(message.Title)
                .AddText(message.Body)
                .AddButton(new ToastButton().SetContent("打开组件")
                    .AddArgument("game", message.TargetGameKey == "endfield" ? "endfield" : "wuwa"))
                .GetToastContent();

    private static DateTimeOffset NextGameDay(DateTimeOffset now) => DailyReminderSchedule.NextReset(now);

    public void Dispose()
    {
        _disposed = true;
        if (_registered) ToastNotificationManagerCompat.OnActivated -= OnActivated;
        // Do not Uninstall(): notifications must remain activatable after app exit.
    }
}
