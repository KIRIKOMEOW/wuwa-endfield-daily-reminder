using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace GameDailyWidget;

public sealed record AccountChoice(string Key, string Label);
public sealed record DetailLine(string Label, string Value, string? Hint = null, DateTimeOffset? FullAt = null,
    int? Current = null, int? Maximum = null);
public sealed record GameViewState
{
    public string GameKey { get; init; } = "";
    public string GameName { get; init; } = "";
    public string Nickname { get; init; } = "未登录";
    public string IdentityLabel { get; init; } = "";
    public string? CopyIdentity { get; init; }
    public string StaminaLabel { get; init; } = "体力";
    public int? Stamina { get; init; }
    public int? MaxStamina { get; init; }
    public DateTimeOffset? FullAt { get; init; }
    public int? Daily { get; init; }
    public int? MaxDaily { get; init; }
    public string DailyState { get; init; } = "unknown";
    public IReadOnlyList<DetailLine> Details { get; init; } = Array.Empty<DetailLine>();
    public IReadOnlyList<AccountChoice> Accounts { get; init; } = Array.Empty<AccountChoice>();
    public string? SelectedAccountKey { get; init; }
    public string Status { get; init; } = "等待同步";
    public string? Error { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool Busy { get; init; }
    public bool Demo { get; init; }
    public bool LoggedIn { get; init; }
    public bool Stale { get; init; }
    public int RefreshMinutes { get; init; } = 5;
}

public interface IGameController : IDisposable
{
    string GameKey { get; }
    GameViewState State { get; }
    event Action? Changed;
    Task InitializeAsync();
    Task RefreshAsync();
    Task SelectAccountAsync(string key);
    Task LoginAsync(Window? owner);
    void Logout();
    void SetRefreshMinutes(int minutes);
}
