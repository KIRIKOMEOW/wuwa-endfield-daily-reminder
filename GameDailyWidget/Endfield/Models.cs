using System;

namespace EndfieldDesktopWidget;

public sealed record QrLoginSession(string ScanId, string QrContent);
public sealed record SklandCredential(string Cred, string Token);
public sealed record EndfieldRole(string RoleId, string ServerId, string UserId, string Nickname, string ServerName);
public sealed record GameSnapshot(int Stamina, int MaxStamina, DateTimeOffset? StaminaFullAt,
    int DailyActivation, int MaxDailyActivation, int WeeklyScore, int WeeklyTotal,
    int BattlePassLevel, int? MaxBattlePassLevel, string Nickname, DateTimeOffset FetchedAt);

public sealed class WidgetSettings
{
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 100;
    public bool DesktopMode { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public double Scale { get; set; } = 1;
    public string GamePath { get; set; } = "";
    public string? SelectedRoleId { get; set; }
}
