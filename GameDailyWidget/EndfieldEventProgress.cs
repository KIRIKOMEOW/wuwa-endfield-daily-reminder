using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace GameDailyWidget;

internal sealed record EndfieldProgress(string Name, string? Alias, DateTimeOffset Start, DateTimeOffset End,
    string Text, bool Complete);

internal sealed record EndfieldProgressReading(string RoleKey, DateTimeOffset FetchedAt,
    IReadOnlyList<EndfieldProgress> Items)
{
    internal bool IsTrusted(GameViewState state, DateTimeOffset now) =>
        state.GameKey == "endfield" && state.LoggedIn && !state.Demo && !state.Busy && !state.Stale &&
        state.Error == null && state.SelectedAccountKey == RoleKey && state.UpdatedAt == FetchedAt &&
        FetchedAt <= now && now - FetchedAt < TimeSpan.FromMinutes(15) &&
        StartupDailyReminder.GameDayKey(FetchedAt) == StartupDailyReminder.GameDayKey(now);
}

internal static class EndfieldEventProgress
{
    private static JsonElement Field(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var field) ? field : default;

    private static IEnumerable<JsonElement> Rows(JsonElement item, string name)
    {
        var list = Field(item,name);
        if (list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray()) yield return row;
    }

    private static string? Name(JsonElement item, string field)
    {
        var value = Field(item,field);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim() : null;
    }

    private static long? Number(JsonElement item, string field)
    {
        var value = Field(item,field);
        return long.TryParse(value.ToString(),NumberStyles.None,CultureInfo.InvariantCulture,out var n) ? n : null;
    }

    private static DateTimeOffset? Time(JsonElement item, string field)
    {
        var seconds = Number(item,field);
        return seconds is > 0 and <= 253402300799L ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    }

    internal static IReadOnlyList<EndfieldProgress> Parse(JsonElement detail, string expectedRole)
    {
        var result = new List<EndfieldProgress>();
        var role = Field(Field(detail,"base"),"roleId");
        if (string.IsNullOrEmpty(expectedRole) || role.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)
            || role.ToString() != expectedRole) return result;

        void Add(string? name, string? alias, DateTimeOffset? start, DateTimeOffset? end, string text, bool complete = false)
        {
            if (name != null && start.HasValue && end > start)
                result.Add(new(name,alias,start.Value,end.Value,text,complete));
        }
        foreach (var group in Rows(Field(detail,"indieHard"),"indieHardGroups"))
        {
            if (Field(group,"isInActivity").ValueKind != JsonValueKind.True) continue;
            var dungeons = Rows(group,"dungeonGroups").ToArray();
            JsonValueKind Pass(JsonElement d, string difficulty) => Field(Field(d,difficulty),"isPass").ValueKind;
            if (dungeons.Length == 0 || dungeons.Any(d => Pass(d,"normalDungeon") is not (JsonValueKind.True or JsonValueKind.False)
                || Pass(d,"hardDungeon") is not (JsonValueKind.True or JsonValueKind.False))) continue;
            var normal = dungeons.Count(d => Pass(d,"normalDungeon") == JsonValueKind.True);
            var hard = dungeons.Count(d => Pass(d,"hardDungeon") == JsonValueKind.True);
            var name = Name(group,"name");
            Add(Name(group,"activityName") ?? name,name,Time(group,"activityStartTs"),Time(group,"activityEndTs"),
                $"普通 {normal}/{dungeons.Length} · 苦难 {hard}/{dungeons.Length}");
        }
        var war = Field(detail,"warEchoes");
        var activity = Name(Field(war,"activity"),"name") ?? "战争回响";
        foreach (var season in Rows(war,"seasons"))
            if (Name(season,"name") is {} seasonName && Number(season,"stars") is {} stars)
                Add(activity + "·" + seasonName,null,Time(season,"startTs"),Time(season,"endTs"),$"进度 {stars} 星");

        foreach (var contract in Rows(detail,"crisisContract"))
        {
            var missions = new[] { "weeklyMission","indicatorMission","stageMission" }.Select(k => Field(contract,k))
                .Select(m => (Count:Number(m,"count"),Total:Number(m,"total"))).ToArray();
            if (missions.Any(m => m.Total is not (> 0 and <= int.MaxValue) || m.Count == null || m.Count > m.Total)) continue;
            var end = Time(contract,"endAtTs");
            var gameplayEnd = Time(contract,"gameplayEndAtTs");
            if (end == null || gameplayEnd == null) continue;
            Add(Name(contract,"name"),null,Time(contract,"startAtTs"),gameplayEnd < end ? gameplayEnd : end,
                $"进度 {missions.Sum(m => m.Count)}/{missions.Sum(m => m.Total)}",missions.All(m => m.Count == m.Total));
        }
        return result;
    }

    private static string ProgressName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(EventParser.Normalize(name),"[•・‧･∙⋅]","·");

    internal static Dictionary<string,string> Apply(EventStore store, EndfieldProgressReading? reading,
        GameViewState state, DateTimeOffset now)
    {
        var labels = new Dictionary<string,string>();
        if (reading == null || !reading.IsTrusted(state,now)) return labels;
        var links = (from item in store.Data.Events
                     where item.Game == "endfield" && !item.Unknown
                     let name = ProgressName(item.Name)
                     from row in reading.Items
                     where (name == ProgressName(row.Name) ||
                         (row.Alias != null && name == ProgressName(row.Alias))) &&
                         item.Start < row.End && (item.End == null || row.Start < item.End)
                     select (Item:item, Row:row)).ToList();
        foreach (var (item,row) in links)
        {
            if (links.Count(l => l.Item.Key == item.Key) != 1 || links.Count(l => l.Row == row) != 1 ||
                item.Start > reading.FetchedAt || row.Start > reading.FetchedAt || now >= row.End || now >= item.End) continue;
            labels.Add(item.Key,row.Text);
            if (row.Complete) store.MarkAutomatically(item,now);
        }
        return labels;
    }
}
