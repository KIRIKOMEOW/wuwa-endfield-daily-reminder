using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using EndfieldDesktopWidget;

namespace GameDailyWidget;

public sealed class WuwaPreferences
{
    public string? AccountKey { get; set; }
    public int RefreshMinutes { get; set; } = 5;
    public string GamePath { get; set; } = "";
}

public sealed class EndfieldPreferences
{
    public string? SelectedRoleId { get; set; }
    public string? SelectedRoleKey { get; set; }
    public int RefreshMinutes { get; set; } = 5;
    public string GamePath { get; set; } = "";
}

public sealed class UnifiedSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool AlwaysOnTop { get; set; }
    public double Scale { get; set; } = 1;
    public string CurrentGame { get; set; } = "wuwa";
    public bool StartWithWindows { get; set; }
    public ArtFramingNotice WuwaFraming {get;set;}=new();
    public ArtFramingNotice EndfieldFraming {get;set;}=new();
    internal ArtFramingNotice FramingFor(string game)=>game=="wuwa"?WuwaFraming:EndfieldFraming;
    public string? WuwaNoticeUrl { get; set; }
    public bool CompletionArtEnabled { get; set; } = true;
    public System.Collections.Generic.List<GuideAuthor> GuideAuthors { get; set; } = GuideAuthor.Defaults();
    public bool EventReminderEnabled { get; set; } = true;
    public bool StartupReminderEnabled { get; set; } = true;
    public int StartupReminderDelaySeconds { get; set; } = LaunchReminderDelay.DefaultSeconds;
    public string? LastStartupReminderDay { get; set; }
    [JsonConverter(typeof(ReminderTimesConverter))]
    public System.Collections.Generic.List<string>? DailyReminderTimes { get; set; }
    public string? HandledReminderDay { get; set; }
    public System.Collections.Generic.List<string>? HandledReminderTimes { get; set; }
    public DateTimeOffset? LastPreResetReminderSlot { get; set; }
    public bool EdgeAutoHide { get; set; } = true;
    public string? DockedEdge { get; set; }
    public GameArtSettings WuwaArt {get;set;} = new();
    public GameArtSettings EndfieldArt {get;set;} = new();
    internal GameArtSettings ArtFor(string game) => game == "endfield" ? EndfieldArt : WuwaArt;
    public WuwaPreferences Wuwa { get; set; } = new();
    public EndfieldPreferences Endfield { get; set; } = new();
    [JsonIgnore] public bool IsPreview { get; private set; }
    [JsonIgnore] public bool LastSaveSucceeded { get; private set; } = true;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameDailyWidget");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static UnifiedSettings Load(bool preview = false)
    {
        if (preview) return new UnifiedSettings { IsPreview = true };
        UnifiedSettings settings;
        if (File.Exists(SettingsPath))
        {
            try { settings = JsonSerializer.Deserialize<UnifiedSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
            catch { settings = new(); }
        }
        else settings = ImportLegacy();
        settings.Normalize();
        settings.StartWithWindows = StartupRegistration.IsEnabled();
        return settings;
    }

    private static UnifiedSettings ImportLegacy()
    {
        var settings = new UnifiedSettings();
        // Import ordinary preferences only. Endfield account.dat stays in its original
        // DPAPI store; this host never serializes credentials into its settings.
        var legacyEndfieldPath = Path.Combine(SettingsStore.DataDirectory, "settings.json");
        if (File.Exists(legacyEndfieldPath))
        {
            var endfield = SettingsStore.Load();
            settings.Endfield.GamePath = endfield.GamePath;
            settings.Endfield.SelectedRoleId = endfield.SelectedRoleId;
            settings.Left = endfield.Left;
            settings.Top = endfield.Top;
            settings.Scale = endfield.Scale;
            settings.AlwaysOnTop = endfield.AlwaysOnTop;
        }
        var legacyWuwaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WuwaDailyWidget", "settings.json");
        try
        {
            if (!File.Exists(legacyWuwaPath)) return settings;
            using var doc = JsonDocument.Parse(File.ReadAllText(legacyWuwaPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("AccountKey", out var key) && key.ValueKind == JsonValueKind.String) settings.Wuwa.AccountKey = key.GetString();
            if (root.TryGetProperty("RefreshMinutes", out var interval) && interval.TryGetInt32(out var minutes)) settings.Wuwa.RefreshMinutes = minutes;
            if (!File.Exists(legacyEndfieldPath) && root.TryGetProperty("AlwaysOnTop", out var top) && top.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.AlwaysOnTop = top.GetBoolean();
            }
            if (!File.Exists(legacyEndfieldPath) && root.TryGetProperty("Left", out var left) && left.TryGetDouble(out var legacyX)
                && root.TryGetProperty("Top", out var legacyTop) && legacyTop.TryGetDouble(out var legacyY)
                && double.IsFinite(legacyX) && double.IsFinite(legacyY))
            {
                var name = root.TryGetProperty("ScreenName", out var screenName) && screenName.ValueKind == JsonValueKind.String ? screenName.GetString() : null;
                var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name)
                    ?? System.Windows.Forms.Screen.PrimaryScreen;
                var dpi = screen != null ? LegacyMonitorScale(screen) : (1d, 1d);
                settings.Left = legacyX * dpi.Item1;
                settings.Top = legacyY * dpi.Item2;
            }
        }
        catch { /* Invalid legacy preferences must not prevent either game from starting. */ }
        // The original programs' startup registrations are preserved. Starting this
        // separate product automatically requires its own explicit settings action.
        return settings;
    }

    private static (double, double) LegacyMonitorScale(System.Windows.Forms.Screen screen)
    {
        try
        {
            var point = new NativePoint { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 };
            var monitor = MonitorFromPoint(point, 2);
            if (GetDpiForMonitor(monitor, 0, out var x, out var y) == 0 && x > 0 && y > 0) return (x / 96d, y / 96d);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return (1, 1);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);

    private void Normalize()
    {
        WuwaFraming ??=new(); EndfieldFraming ??=new();
        WuwaFraming.Handled ??=new(); EndfieldFraming.Handled ??=new();
        Wuwa ??= new(); Endfield ??= new();
        GuideAuthors ??= GuideAuthor.Defaults();
        GuideAuthors.RemoveAll(a => a?.Name == null || a.Mid == null || !GuideAuthor.TryCreate(a.Name,a.Mid,a.Game,out _));
        WuwaArt ??= new(); EndfieldArt ??= new();
        foreach (var art in new[]{WuwaArt,EndfieldArt})
        {
            art.Intensity=Math.Clamp(art.Intensity,0,100);
            if (!ArtCache.Valid(art.Selected)) art.Selected=null;
            art.Crops ??= new();
        }
        if (Left is { } x && !double.IsFinite(x)) Left = null;
        if (Top is { } y && !double.IsFinite(y)) Top = null;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .5, 2.5) : 1;
        StartupReminderDelaySeconds = LaunchReminderDelay.NormalizeSeconds(StartupReminderDelaySeconds);
        if (CurrentGame != "endfield") CurrentGame = "wuwa";
        if (Wuwa.RefreshMinutes is not (1 or 5 or 10)) Wuwa.RefreshMinutes = 5;
        if (Endfield.RefreshMinutes is not (1 or 5 or 10)) Endfield.RefreshMinutes = 5;
    }

    public void Save()
    {
        if (IsPreview) return;
        try
        {
            Normalize();
            Directory.CreateDirectory(DataDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, true);
            LastSaveSucceeded = true;
        }
        catch { LastSaveSucceeded = false; }
    }

    public static bool SetStartup(bool enabled)
    {
        if (App.IsPreview) return false;
        return StartupRegistration.SetEnabled(enabled, StartupRegistration.ResolveExecutable());
    }
}
