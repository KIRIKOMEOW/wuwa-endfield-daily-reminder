using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GameDailyWidget;

internal sealed record EventSource(string Id, string Title, string Url);
internal sealed record EventArticle(string Game, EventSource Source, DateTimeOffset Published, DateTimeOffset StartTime, string? ImageUrl = null);
internal sealed record PoolItem(string Name, int Stars, string Category);
internal sealed record GamePool(string Key, string Game, string Name, string Kind, DateTimeOffset? Start,
    DateTimeOffset? End, bool TimePending, List<PoolItem> Items, EventSource Source, string Version, DateTimeOffset Published,
    bool StartsAfterMaintenance = false, bool EndsBeforeMaintenance = false,
    bool NewUp = false, bool NoticeTime = false, bool InclusiveEndMinute = false);
internal sealed record EventCurrency(string Name, int? Amount);
internal sealed record GameEvent(string Key, string Game, string Name, string Kind, DateTimeOffset Start,
    DateTimeOffset? End, bool EndPending, DateTimeOffset? ClaimDeadline, EventCurrency Currency,
    EventSource Source, string Version, DateTimeOffset Published)
{
    [JsonIgnore] public DateTimeOffset? Deadline => ClaimDeadline ?? End;
    [JsonIgnore] public bool Unknown => End == null && !EndPending;
}
internal sealed record Maintenance(string Version, DateTimeOffset Published, DateTimeOffset Start, DateTimeOffset End);
internal sealed record EventMark(bool Claimed, bool Ignored, DateTimeOffset At, bool Automatic = false, bool ManualOverride = false);
internal sealed record ArticleStamp(DateTimeOffset FetchedAt, DateTimeOffset StartTime, int ParserVersion = 0,
    string? Title = null, DateTimeOffset? Published = null, bool ArtChecked = false, string? ImageUrl = null, List<GamePool>? ParsedPools = null,
    List<string>? NewWuwaItems = null);
internal sealed record EventSyncState(DateTimeOffset? LastSuccess, DateTimeOffset LastAttempt, string? Error, string? Coverage = null);
internal sealed class EventData
{
    public int Schema { get; set; } = 3;
    public List<GamePool>? WuwaNoticePools { get; set; }
    public List<GamePool> Pools { get; init; } = new();
    public Dictionary<string, EventSyncState> Sync { get; init; } = new();
    public Dictionary<string, ArticleStamp> Articles { get; init; } = new();
    public Dictionary<string, List<Maintenance>> Maintenance { get; init; } = new();
    public List<GameEvent> Events { get; init; } = new();
    public Dictionary<string, EventMark> Marks { get; init; } = new();
    public Dictionary<string, DateTimeOffset> Sent { get; init; } = new();
}
