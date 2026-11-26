namespace Wuwa.Core;

// An opaque local selector. Key is a path/index hash, never an authorization value.
public sealed record AccountHandle(string Key, int Index, int CacheNumber, int AccountNumber);

public sealed record PlayerIdentity(string Uid, string Name, int? Level);

public sealed class AccountOption
{
    public required AccountHandle Handle { get; init; }
    public PlayerIdentity? Identity { get; init; }
    public string? Problem { get; init; }
    public string DisplayName => Identity is not null
        ? $"{Identity.Name}（UID：{Identity.Uid}）"
        : $"本地缓存 {Handle.CacheNumber} / 账号项 {Handle.AccountNumber}：{Problem ?? "身份待确认"}";
}

public sealed class RoleSnapshot
{
    public required PlayerIdentity Identity { get; init; }
    public DateTimeOffset FetchedAtUtc { get; init; }
    public int? Energy { get; init; }
    public int? MaxEnergy { get; init; }
    public int? StoreEnergy { get; init; }
    public int? MaxStoreEnergy { get; init; }
    public int? Liveness { get; init; }
    public int? LivenessMaxCount { get; init; }
    public int? WeeklyInstCount { get; init; }
    public int? BattlePassLevel { get; init; }
    public int? WeekExp { get; init; }
    public int? WeekMaxExp { get; init; }
    public bool? LivenessUnlock { get; init; }
    public DateTimeOffset? EnergyFullAtUtc { get; init; }
    public DateTimeOffset? StoreEnergyFullAtUtc { get; init; }
    public string DailyState { get; init; } = "unknown";
    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();
}

public sealed class LauncherException : Exception
{
    public string Status { get; }
    public LauncherException(string status, string safeMessage) : base(safeMessage) => Status = status;
}
