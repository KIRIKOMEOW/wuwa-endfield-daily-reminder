using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace GameDailyWidget;

internal sealed record EventActivation(string Game, string? ClaimKey, bool Demo)
{
    internal static EventActivation? Parse(ToastArguments args)
    {
        if (!args.TryGetValue("view", out var view) || view != "events" ||
            !args.TryGetValue("game", out var game) || game is not ("wuwa" or "endfield")) return null;
        var demo = args.TryGetValue("demo", out var value) && value == "1";
        if (!args.TryGetValue("action", out var action)) return new(game, null, demo);
        if (action != "claim" || !args.TryGetValue("key", out var key) || key.Length > 512 ||
            !key.StartsWith(game + "|", StringComparison.Ordinal)) throw new FormatException();
        return new(game, key, demo);
    }
}

internal static class EventNotification
{
    internal const string Group = "game-events";

    internal static object ProbeReport(NativeNotificationResult result, bool inHistory, bool failed, string tag, bool verificationOnly = false) => new
    {
        status = result.Submitted && inHistory && !failed ? "passed" : "failed",
        result.Submitted, notificationStatus = result.Status, nativeToastInOwnWindowsHistory = inHistory,
        verificationOnly, deliveryFailed = failed, group = Group, tag, realCredentialsRead = false, personalPreferencesSaved = false, testData = true
    };

    internal static ToastContent Content(EventNotice notice, bool demo)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("game",notice.Game).AddArgument("view","events")
            .AddText(demo ? "演示 · " + notice.Title : notice.Title)
            .AddText(notice.Body);
        if (demo) builder.AddArgument("demo","1");
        builder.AddButton(new ToastButton().SetContent("打开活动").AddArgument("view","events"));
        if (notice.ClaimKey is {} key)
            builder.AddButton(new ToastButton().SetContent("已领完").AddArgument("action","claim")
                .AddArgument("key",key).SetBackgroundActivation());
        return builder.GetToastContent();
    }
}
