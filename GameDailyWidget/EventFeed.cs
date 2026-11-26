using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

internal sealed class EventFeed : IDisposable
{
    internal const string Cdn = "https://media-cdn-mingchao.kurogame.com/akiwebsite/website2.0/json/G152/zh/";
    internal const string News = "https://endfield.hypergryph.com/news";
    private readonly HttpClient _http;
    private readonly Dictionary<string,List<EventArticle>> _artIndex = new();
    private readonly Dictionary<string,(DateTimeOffset Start, string? Url)> _wuwaImages = new();

    internal IReadOnlyList<EventArticle> ArtIndex(string game) =>
        _artIndex.TryGetValue(game,out var index) ? index : [];

    internal bool TryGetImage(EventArticle article, EventData data, out string? image)
    {
        image = article.ImageUrl;
        if (article.Game == "endfield") return true;
        if (_wuwaImages.TryGetValue(article.Source.Id,out var known) && known.Start == article.StartTime)
        {
            image = known.Url;
            return true;
        }
        if (data.Articles.TryGetValue("wuwa:" + article.Source.Id,out var stamp) && stamp.ArtChecked && stamp.StartTime == article.StartTime)
        {
            image = stamp.ImageUrl;
            return true;
        }
        return false;
    }

    internal EventFeed(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All })
            { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopCalendar/1.0");
    }

    private async Task<string> Get(string url, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel);
    }

    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 12 } && id.All(char.IsAsciiDigit);

    internal static EventSource Source(string game, string id, string title)
    {
        if (!ValidId(id)) throw new InvalidDataException();
        return new(id, title, game == "wuwa" ? "https://mc.kurogames.com/main/news/detail/" + id : News + "/" + id);
    }

    internal static bool EventTitle(string game, string title) => Regex.IsMatch(title, game == "wuwa"
        ? "版本内容说明|活动|维护预告|结束时间" : "版本更新说明|活动|维护|版本预下载与更新预告");

    internal async Task<(List<EventArticle> Articles, string? Coverage)> ListAsync(string game, DateTimeOffset now, CancellationToken cancel)
    {
        var result = new List<EventArticle>();
        var art = new List<EventArticle>();
        var cutoff = now.AddDays(-60);
        string? coverage = null;
        if (game == "wuwa")
        {
            using var menu = JsonDocument.Parse(await Get(Cdn + "ArticleMenu.json", cancel));
            foreach (var row in menu.RootElement.EnumerateArray())
            {
                var title = row.GetProperty("articleTitle").GetString()!;
                if (row.GetProperty("articleType").GetInt32() is not (52 or 53)) continue;
                bool TryTime(string key,out DateTime date) => DateTime.TryParseExact(row.GetProperty(key).GetString(),
                    "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,DateTimeStyles.None,out date);
                if (!TryTime("createTime",out var created) || !TryTime("startTime",out var started)) continue;
                var published = new DateTimeOffset(created,EventParser.China);
                if (published > now) continue;
                var id = row.GetProperty("articleId").ToString();
                if (!ValidId(id)) continue;
                var article = new EventArticle(game, Source(game,id,title),
                    published,new DateTimeOffset(started,EventParser.China));
                art.Add(article);
                if (published >= cutoff && (EventTitle(game,title) || PoolParser.IsAnnouncement(game,title))) result.Add(article);
            }
        }
        else
        {
            var seen = new HashSet<string>();
            for (var page = 1; page <= 24; page++)
            {
                var html = await Get(page == 1 ? News : News + "?page=" + page, cancel);
                var (rows, total) = ReadList(html);
                var more = false;
                var oldest = now;
                foreach (var row in rows.EnumerateArray())
                {
                    var id = row.GetProperty("cid").GetString()!;
                    if (!ValidId(id) || !seen.Add(id)) continue;
                    more = true;
                    var time = DateTimeOffset.FromUnixTimeSeconds(row.GetProperty("displayTime").GetInt64()).ToOffset(EventParser.China);
                    if (!row.GetProperty("sticky").GetBoolean() && time < oldest) oldest = time;
                    var title = row.GetProperty("title").GetString()!;
                    var tab = row.GetProperty("tab").GetString();
                    var url = row.TryGetProperty("cover",out var cover) && cover.ValueKind == JsonValueKind.String ? cover.GetString() : null;
                    var article = new EventArticle(game, Source(game,id,title),time,time,OfficialArtSources.Allowed(game,url) ? url : null);
                    if (time <= now) art.Add(article);
                    if (tab is ("notices" or "events" or "activity" or "activities") && time >= cutoff && time <= now &&
                        (EventTitle(game,title) || PoolParser.IsAnnouncement(game,title)))
                        result.Add(article);
                }
                if (seen.Count >= total || oldest < cutoff) break;
                if (!more || page == 24)
                {
                    coverage = "历史分页未覆盖 · 保留已同步公告";
                    break;
                }
            }
        }
        _artIndex[game] = art.OrderByDescending(a => a.Published).DistinctBy(a => a.Source.Id).ToList();
        return (result.OrderBy(a => a.Published).ToList(),coverage);
    }

    internal static (JsonElement Rows, int Total) ReadList(string html)
    {
        foreach (Match script in Regex.Matches(html, """self\.__next_f\.push\(\[1,("(?:\\.|[^"\\])*")\]\)"""))
        {
            var chunk = JsonSerializer.Deserialize<string>(script.Groups[1].Value)!;
            var marker = chunk.IndexOf("{\"bulletins\":", StringComparison.Ordinal);
            if (marker < 0) continue;
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(chunk[marker..]));
            using var value = JsonDocument.ParseValue(ref reader);
            return (value.RootElement.GetProperty("bulletins").Clone(), value.RootElement.GetProperty("total").GetInt32());
        }
        throw new InvalidDataException("公告列表结构已变更");
    }

    internal async Task<(EventArticle Article, string[] Lines)> BodyAsync(EventArticle article, CancellationToken cancel)
    {
        var id = article.Source.Id;
        if (!ValidId(id)) throw new InvalidDataException();
        string body;
        if (article.Game == "wuwa")
        {
            using var json = JsonDocument.Parse(await Get(Cdn + "article/" + id + ".json", cancel));
            if (article.Source.Title.Length == 0) article = article with { Source = Source(article.Game,id,json.RootElement.GetProperty("articleTitle").GetString()!) };
            body = json.RootElement.GetProperty("articleContent").GetString()!;
            var image = OfficialArtSources.FirstWuwaImage(body);
            article = article with { ImageUrl=image };
            _wuwaImages[id] = (article.StartTime,image);
        }
        else
        {
            var html = await Get(News + "/" + id, cancel);
            if (article.Source.Title.Length == 0)
            {
                var title = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                if (!title.Success) throw new InvalidDataException("公告标题缺失");
                article = article with { Source = Source(article.Game,id,WebUtility.HtmlDecode(title.Groups[1].Value.Split('|')[0].Trim())) };
            }
            var start = Regex.Match(html, """<div\b[^>]*class="[^"]*NoticeDetail_content__[^"]*"[^>]*>""");
            if (!start.Success) throw new InvalidDataException("公告正文结构已变更");
            // The published rich text may itself contain divs.
            var offset = start.Index + start.Length;
            var depth = 1;
            var end = -1;
            foreach (Match tag in Regex.Matches(html[offset..], @"</?div\b[^>]*>", RegexOptions.IgnoreCase))
            {
                depth += tag.Value.StartsWith("</") ? -1 : 1;
                if (depth == 0) { end = tag.Index; break; }
            }
            if (end < 0) throw new InvalidDataException();
            body = html.Substring(offset, end);
        }
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException();
        return (article,EventParser.Lines(body));
    }

    public void Dispose() => _http.Dispose();
}
