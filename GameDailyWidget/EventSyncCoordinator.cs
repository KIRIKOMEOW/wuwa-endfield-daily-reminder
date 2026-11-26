using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

// All state commits run on the caller's dispatcher. Network awaits never hold
// a copy of the marks or sent ledger that a click or timer could change.
internal sealed class EventSyncCoordinator(EventStore store, EventFeed feed, CancellationToken cancel,
    Func<DateTimeOffset>? clock = null)
{
    private static readonly string[] Games = ["wuwa","endfield"];
    private readonly Dictionary<string,Task> _running = new();
    private readonly Dictionary<string,DateTimeOffset> _next = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToOffset(EventParser.China));
    internal HashSet<string> Busy { get; } = new();
    internal event Action? Changed;
    internal Func<string,string,CancellationToken,Task>? SetOfficialArtAsync { get; set; }

    private IEnumerable<EventArticle> ArtSources(string game)
    {
        // A version can leave the news homepage before its successor is released.
        var previous = store.Data.Articles.Where(p => p.Key.StartsWith(game + ":",StringComparison.Ordinal) &&
                p.Value.Title != null && p.Value.Published.HasValue && EventFeed.ValidId(p.Key[(game.Length+1)..]))
            .Select(p => new EventArticle(game,EventFeed.Source(game,p.Key[(game.Length+1)..],p.Value.Title!),
                p.Value.Published!.Value,p.Value.StartTime,p.Value.ImageUrl));
        return feed.ArtIndex(game).Concat(previous).DistinctBy(a => a.Source.Id).OrderByDescending(a => a.Published);
    }

    // Only the background editor calls this. An existing synchronization owns the index request.
    internal async Task<IReadOnlyList<string>> GalleryAsync(string game, CancellationToken cancellation)
    {
        if (game is not ("endfield" or "wuwa")) return [];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel,cancellation);
        var token = linked.Token;
        if (_running.TryGetValue(game,out var sync)) await sync.WaitAsync(token);
        var urls = new List<string>();
        var extra = 0;
        foreach (var article in ArtSources(game).ToList())
        {
            token.ThrowIfCancellationRequested();
            if (!feed.TryGetImage(article,store.Data,out var url))
            {
                if (game != "wuwa" || extra == 8) continue;
                extra++;
                try { url = (await feed.BodyAsync(article,token)).Article.ImageUrl; }
                catch when (!token.IsCancellationRequested) { continue; }
            }
            if (OfficialArtSources.Allowed(game,url) && !urls.Contains(url!)) urls.Add(url!);
            if (urls.Count == 8) break;
        }
        return urls;
    }

    private sealed class VersionImage(string url)
    {
        internal string Url { get; } = url;
        internal int Failures;
        internal DateTimeOffset? RetryAt;
        internal CancellationTokenSource? Download;
    }

    private readonly Dictionary<string,VersionImage> _images = new();

    private async Task DiscoverVersionImageAsync(string game)
    {
        if (SetOfficialArtAsync == null) return;
        foreach (var article in ArtSources(game).Where(OfficialArtSources.VersionNote).ToList())
        {
            if (!feed.TryGetImage(article,store.Data,out var url))
            {
                // Only discovery reads article metadata missing from an older cache.
                try { url = (await feed.BodyAsync(article,cancel)).Article.ImageUrl; }
                catch when (!cancel.IsCancellationRequested) { continue; }
                if (store.Data.Articles.TryGetValue(game + ":" + article.Source.Id,out var stamp))
                    store.Data.Articles[game + ":" + article.Source.Id] = stamp with { ArtChecked=true, ImageUrl=url };
            }
            if (!OfficialArtSources.Allowed(game,url)) continue;
            var previous = _images.GetValueOrDefault(game);
            if (previous?.Url == url) return; // Keep both successful results and outstanding backoff.
            previous?.Download?.Cancel();
            var image = new VersionImage(url!);
            _images[game] = image;
            _ = DownloadVersionImageAsync(game,image);
            return;
        }
    }

    private async Task DownloadVersionImageAsync(string game, VersionImage image)
    {
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        image.RetryAt = null;
        image.Download = transfer;
        try
        {
            await SetOfficialArtAsync!(game,image.Url,transfer.Token);
        }
        catch (Exception) when (transfer.IsCancellationRequested) { }
        catch
        {
            var minutes = image.Failures switch { 0 => 10, 1 => 30, 2 => 60, _ => 180 };
            image.Failures = Math.Min(3,image.Failures + 1);
            image.RetryAt = _clock().AddMinutes(minutes);
        }
        finally { image.Download = null; }
    }

    internal void Tick(DateTimeOffset now)
    {
        if (cancel.IsCancellationRequested) return;
        foreach (var game in Games)
        {
            if (!Busy.Contains(game) && now >= _next.GetValueOrDefault(game)) _ = Request(game,now);
            if (_images.GetValueOrDefault(game) is { RetryAt: {} due, Download: null } image && now >= due)
                _ = DownloadVersionImageAsync(game,image);
        }
    }

    internal Task SyncAsync(DateTimeOffset now) => Task.WhenAll(Request("wuwa",now),Request("endfield",now));

    private Task Request(string game, DateTimeOffset now)
    {
        if (_running.TryGetValue(game,out var task) && !task.IsCompleted) return task;
        return _running[game] = SyncGame(game,now);
    }

    private async Task SyncGame(string game, DateTimeOffset now)
    {
        var data = store.Data;
        Busy.Add(game);
        Changed?.Invoke();
        try
        {
            var (articles, coverage) = await feed.ListAsync(game, now, cancel);
            // Previously discovered articles may already have left the public list.
            var listed = articles.Select(a => a.Source.Id).ToHashSet();
            foreach (var (key, old) in data.Articles)
            {
                if (old.ParserVersion == EventParser.Revision || !key.StartsWith(game + ":",StringComparison.Ordinal)) continue;
                var id = key[(game.Length+1)..];
                if (!EventFeed.ValidId(id) || listed.Contains(id)) continue;
                var prior = data.Events.FirstOrDefault(e => e.Game == game && e.Source.Id == id);
                var published = old.Published ?? prior?.Published ?? old.StartTime;
                if (published < now.AddDays(-60) || published > now) continue;
                articles.Add(new(game,EventFeed.Source(game,id,old.Title ?? prior?.Source.Title ?? ""),published,old.StartTime));
            }
            var fetched = new List<(EventArticle Article, string[] Lines)>();
            foreach (var article in articles.OrderBy(a => a.Published))
            {
                var key = game + ":" + article.Source.Id;
                if (data.Articles.TryGetValue(key, out var stamp) && stamp.ParserVersion == EventParser.Revision &&
                    (game == "endfield" || stamp.StartTime == article.StartTime)) continue;
                fetched.Add(await feed.BodyAsync(article,cancel));
            }
            cancel.ThrowIfCancellationRequested();
            var maintenance = data.Maintenance.GetValueOrDefault(game)?.ToList() ?? new();
            foreach (var (article, lines) in fetched)
                if (EventParser.ReadMaintenance(article, lines) is {} window)
                {
                    maintenance.RemoveAll(m => m.Version == window.Version && m.Published <= window.Published);
                    if (!maintenance.Any(m => m.Version == window.Version && m.Published > window.Published)) maintenance.Add(window);
                }
            var parsed = new List<GameEvent>();
            var poolsByArticle = new Dictionary<string,List<GamePool>>();
            foreach (var (article, lines) in fetched)
            {
                var version = EventParser.VersionOf(article);
                var start = maintenance.FirstOrDefault(m => m.Version == version)?.End;
                poolsByArticle[article.Source.Id] = PoolParser.Parse(article,lines);
                if (EventFeed.EventTitle(game, article.Source.Title))
                    parsed.AddRange(EventParser.Parse(article, lines, start));
            }
            foreach (var (article, _) in fetched)
            {
                data.Events.RemoveAll(e => e.Game == game && e.Source.Id == article.Source.Id &&
                    !parsed.Any(p => p.Key == e.Key));
                data.Articles[game + ":" + article.Source.Id] = new(now,article.StartTime,EventParser.Revision,article.Source.Title,article.Published,true,article.ImageUrl,poolsByArticle[article.Source.Id]);
            }
            foreach (var item in parsed) EventParser.Merge(data.Events, item);
            data.Maintenance[game] = maintenance;
            ResolvePending(data, game, maintenance);
            PoolCatalog.Rebuild(data, game, maintenance);
            data.Sync[game] = new(now, now, null, coverage);
            store.Clean(now);
            if (game == "endfield")
                foreach (var article in feed.ArtIndex(game))
                {
                    var key = game + ":" + article.Source.Id;
                    if (article.ImageUrl != null && data.Articles.TryGetValue(key,out var stamp))
                        data.Articles[key] = stamp with { ArtChecked=true, ImageUrl=article.ImageUrl };
                }
            try { await DiscoverVersionImageAsync(game); }
            catch when (!cancel.IsCancellationRequested) { /* Metadata discovery resumes at the next announcement sync. */ }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
        catch { data.Sync[game] = new(data.Sync.GetValueOrDefault(game)?.LastSuccess, now, "同步失败"); }
        finally
        {
            Busy.Remove(game);
            if (!cancel.IsCancellationRequested)
            {
                var delay = data.Sync[game].Error == null ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(10);
                _next[game] = _clock() + delay;
                store.Save();
                Changed?.Invoke();
            }
        }
    }

    internal static void ResolvePending(EventData data, string game, List<Maintenance> maintenance)
    {
        for (var i = 0; i < data.Events.Count; i++)
        {
            var item = data.Events[i];
            if (item.Game != game || !item.EndPending) continue;
            bool Later(Maintenance m)
            {
                if (m.Version == item.Version || m.Start <= item.Start) return false;
                if (Version.TryParse(item.Version, out var current) && Version.TryParse(m.Version, out var next)) return next > current;
                return m.Published > item.Published;
            }
            var window = maintenance.Where(Later).MinBy(m => m.Start);
            if (window != null) data.Events[i] = item with { End = window.Start, EndPending = false };
        }
    }
}
