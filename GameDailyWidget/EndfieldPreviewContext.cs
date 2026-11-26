// The existing login window only reads this preview flag. No original App entry,
// mutex, tray or shutdown code is compiled into the unified host.
namespace EndfieldDesktopWidget;
internal static class App
{
    public static bool IsPreview => GameDailyWidget.App.IsPreview;
}
