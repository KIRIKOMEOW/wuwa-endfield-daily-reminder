using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

internal sealed record WuwaNotice(string Id, string Title, string Content, DateTimeOffset DisplayStart, DateTimeOffset DisplayEnd);

internal sealed partial class EventFeed
{
    // Kuro's public in-game notice board, also used by cyrahs/game-cal.
    internal const string NoticeUrl = "https://aki-gm-resources-back.aki-game.com/gamenotice/G152/76402e5b20be2c39f095a152090afddc/zh-Hans.json";
    internal const int NoticeLimit = 5 * 1024 * 1024;

    internal static Uri NoticeAddress(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? NoticeUrl : configured;
        if (!Regex.IsMatch(value, @"\Ahttps://aki-gm-resources-back\.aki-game\.com/gamenotice/G152/[A-Za-z0-9_-]{1,128}/zh-Hans\.json\z"))
            throw new InvalidDataException("游戏内公告地址无效");
        return new Uri(value);
    }

    internal async Task<List<WuwaNotice>> NoticesAsync(string? configured, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.GetAsync(NoticeAddress(configured), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType;
        if (!(string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase) ||
              type?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true) || response.Content.Headers.ContentLength > NoticeLimit)
            throw new InvalidDataException("游戏内公告类型或大小无效");
        await response.Content.LoadIntoBufferAsync(NoticeLimit, timeout.Token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
        return ReadNotices(json.RootElement);
    }

    internal static List<WuwaNotice> ReadNotices(JsonElement root)
    {
        var notices = new List<WuwaNotice>();
        foreach (var section in new[] { "recommend", "game", "activity" })
        {
            foreach (var row in root.GetProperty(section).EnumerateArray())
            {
                var category = row.GetProperty("category").GetInt32();
                var tag = row.GetProperty("tag").GetInt32();
                var title = row.GetProperty("tabTitle").GetString() ?? throw new InvalidDataException();
                if (category != 3 || tag != 7 || !title.Contains("唤取") || row.GetProperty("permanent").GetInt32() == 1) continue;
                var id = row.GetProperty("id").ToString();
                var content = row.GetProperty("content").GetString() ?? throw new InvalidDataException();
                var start = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("startTimeMs").GetInt64()).ToOffset(EventParser.China);
                var end = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("endTimeMs").GetInt64()).ToOffset(EventParser.China);
                if (!ValidId(id) || end <= start) throw new InvalidDataException("游戏内公告元数据无效");
                notices.Add(new(id, Regex.Replace(title, @"[\r\n]+", ""), content, start, end));
            }
        }
        return notices;
    }
}
