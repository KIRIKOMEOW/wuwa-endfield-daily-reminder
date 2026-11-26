using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

internal static class OfficialArtSourceChecks
{
    private const string Wuwa = "https://media-cdn-mingchao.kurogame.com/";
    private const string Endfield = "https://web.hycdn.cn/";
    private static readonly DateTimeOffset Day = new(2030,1,22,14,0,0,EventParser.China);

    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        void Check(bool ok,string text)
        {
            if (!ok) throw new InvalidOperationException(text);
            checks.Add("PASS " + text);
        }
        var image = Wuwa + "preview.png?a=1&b=2";
        Check(OfficialArtSources.FirstWuwaImage("<img data-src='not-used'><IMG title=\"fake src='not-used'\" SRC='" +
            image.Replace("&","&amp;") + "'><img src='" + Wuwa + "another.png'>") == image,
            "鸣潮正文读取第一个实际图片 src，支持大小写、引号与 HTML 实体");
        Check(new[] {"http://web.hycdn.cn/test","https://web.hycdn.cn.invalid/a","https://web.hycdn.cn:444/a",
            "https://name@web.hycdn.cn/a","//web.hycdn.cn/a",Wuwa + "cross.jpg"}.All(s => !OfficialArtSources.Allowed("endfield",s)) &&
            OfficialArtSources.FirstWuwaImage("<img src='javascript:invalid'>") == null,
            "图片只接受对应游戏的 HTTPS 官方域名，拒绝凭据、其它端口和混用域名");

        using var transport = new Fixture();
        using var feed = new EventFeed(transport);
        var store = new EventStore();
        var now = Day;
        var coordinator = new EventSyncCoordinator(store,feed,CancellationToken.None,() => now);
        var applied = new List<string>();
        using var images = new HttpClient(transport,disposeHandler:false);
        coordinator.SetOfficialArtAsync = async (_,url,token) =>
        {
            if (url != Endfield+"previous.jpg") // Simulate a local cache hit for the Endfield cover.
            {
                using var response = await images.GetAsync(url,token);
                response.EnsureSuccessStatusCode();
            }
            applied.Add(url);
        };
        await coordinator.SyncAsync(now);
        Check(applied.Contains(Endfield+"previous.jpg") && !applied.Contains(Endfield+"news.jpg"),
            "从新闻 RSC 的版本公告选封面，最新无合法封面时退到上一版本");
        var previous = Wuwa+"version-1.jpg";
        var newest = Wuwa+"version-2.jpg";
        Check(applied.Contains(previous), "先安装当前可用版本图");
        transport.PublishLatest = true;
        var discovery = Day.AddHours(6);
        void Tick(int minutes)
        {
            now = discovery.AddMinutes(minutes);
            coordinator.Tick(now);
        }
        Tick(0);
        Check(applied.Contains(previous) && !applied.Contains(newest) && store.Data.Sync["wuwa"].Error == null &&
            transport.ExtraBodies == 0 && transport.NewImageRequests == 1 && coordinator.Busy.Count == 0,
            "新版本图片失败时保留上次成功图，活动成功且不提前请求图库正文");
        var feedRequests = transport.NewsRequests;
        foreach (var (minute,attempts) in new[] { (9,1),(10,2),(39,2),(40,3),(99,3),(100,4),(279,4),(280,5),(359,5) })
        {
            Tick(minute);
            if (transport.NewImageRequests != attempts || transport.NewsRequests != feedRequests)
                throw new InvalidOperationException("图片重试未遵守独立时钟或意外拉取公告");
        }
        Check(transport.WuwaMenus == 2 && transport.EndfieldMenus == 2 && transport.VersionBodies == 2,
            "图片按 10、30、60、180 分钟重试，期间两款游戏列表和正文请求数不变");
        Tick(360);
        Check(transport.WuwaMenus == 3 && transport.EndfieldMenus == 3 && transport.NewImageRequests == 5,
            "活动成功满六小时才读列表；相同版本图片保持原重试计划");
        Tick(459);
        transport.ImageHealthy = true;
        Tick(460);
        Check(transport.NewImageRequests == 6 && applied.Count(url => url == newest) == 1 &&
            transport.VersionBodies == 2 && transport.WuwaMenus == 3,
            "之后每三小时仅重试图片，恢复后应用当前版本");
        Tick(640);
        Tick(720);
        Check(transport.WuwaMenus == 4 && transport.EndfieldMenus == 4 && transport.NewImageRequests == 6 &&
            applied.Count(url => url == Endfield+"previous.jpg") == 1,
            "已下载或命中缓存后停止重试，跨下一轮活动同步也不重复提交同一图片");
        var gallery = await coordinator.GalleryAsync("wuwa",CancellationToken.None);
        Check(transport.ExtraBodies == 8 && gallery.Count == 8 && gallery[0] == Wuwa+"gallery-12.jpg",
            "打开鸣潮背景选择器后最多补八篇近期正文，顺序按公告时间");
        await coordinator.GalleryAsync("wuwa",CancellationToken.None);
        Check(transport.ExtraBodies == 8,"重复打开选择器复用同批公告图片元数据");
        var before = transport.Requests;
        var covers = await coordinator.GalleryAsync("endfield",CancellationToken.None);
        Check(covers.SequenceEqual([Endfield+"news.jpg",Endfield+"previous.jpg"]) && transport.Requests == before,
            "终末地候选直接来自列表封面，不产生额外正文请求");
        Check(!transport.Unexpected, "活动与背景来源检查仅使用注入的内存传输，未发出外部请求");
        using var replacementTransport = new Fixture();
        using var replacementFeed = new EventFeed(replacementTransport);
        using var lifetime = new CancellationTokenSource();
        var replacement = new EventSyncCoordinator(new EventStore(),replacementFeed,lifetime.Token,() => now);
        CancellationToken oldTransfer = default;
        var appliedNew = 0;
        replacement.SetOfficialArtAsync = (_,url,token) =>
        {
            if (url == newest) appliedNew++;
            if (url != previous) return Task.CompletedTask;
            oldTransfer = token;
            return Task.Delay(Timeout.InfiniteTimeSpan,token);
        };
        await replacement.SyncAsync(now).WaitAsync(TimeSpan.FromSeconds(2));
        replacementTransport.PublishLatest = true;
        now = now.AddHours(6);
        replacement.Tick(now);
        Check(replacement.Busy.Count == 0 && oldTransfer.IsCancellationRequested && appliedNew == 1,
            "在途图片不占用活动同步；更新的版本地址取消旧图片请求");
        return checks;
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal int Requests, WuwaMenus, EndfieldMenus, ExtraBodies, VersionBodies;
        internal bool Unexpected, PublishLatest, ImageHealthy;
        internal int NewsRequests, NewImageRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)
        {
            Requests++;
            var url = request.RequestUri!;
            if (url.AbsolutePath.EndsWith(".jpg",StringComparison.Ordinal))
            {
                bool latest = url.AbsoluteUri == Wuwa + "version-2.jpg";
                if (latest) NewImageRequests++;
                return Task.FromResult(new HttpResponseMessage(latest && !ImageHealthy
                    ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            }
            NewsRequests++;
            string content;
            if (url.AbsoluteUri == EventFeed.Cdn+"ArticleMenu.json")
            {
                WuwaMenus++;
                content = JsonSerializer.Serialize(Enumerable.Range(1,12).Where(i => i != 2 || PublishLatest).Select(i => new {
                    articleId=i,articleType=52,articleTitle=i <= 2 ? $"「演示{i}」3.{i}版本内容说明" : $"近期公告{i}",
                    createTime=$"2030-01-{i:00} 10:00:00",startTime=$"2030-01-{i:00} 10:00:00" }));
            }
            else if (url.AbsoluteUri.StartsWith(EventFeed.Cdn+"article/",StringComparison.Ordinal))
            {
                var id = int.Parse(System.IO.Path.GetFileNameWithoutExtension(url.AbsolutePath));
                if (id <= 2) VersionBodies++; else ExtraBodies++;
                var prefix = id <= 2 ? "version" : "gallery";
                content = JsonSerializer.Serialize(new {articleContent=$"<p>演示公告</p><img src='{Wuwa}{prefix}-{id}.jpg'>"});
            }
            else if (url.AbsoluteUri == EventFeed.News)
            {
                EndfieldMenus++;
                var bulletins = new[] {
                    new {cid="300",title="近期新闻",tab="news",cover=Endfield+"news.jpg",sticky=false,displayTime=Day.ToUnixTimeSeconds()},
                    new {cid="200",title="「最新」版本更新说明",tab="notices",cover="https://example.invalid/image.png",sticky=false,displayTime=Day.AddDays(-1).ToUnixTimeSeconds()},
                    new {cid="100",title="「上一版」版本更新说明",tab="notices",cover=Endfield+"previous.jpg",sticky=false,displayTime=Day.AddDays(-2).ToUnixTimeSeconds()} };
                var payload = JsonSerializer.Serialize("3:" + JsonSerializer.Serialize(new {bulletins,total=3}));
                content = "<script>self.__next_f.push([1," + payload + "])</script>";
            }
            else if (url.AbsoluteUri is EventFeed.News+"/200" or EventFeed.News+"/100")
                content = "<div class=\"NoticeDetail_content__example\"><p>演示公告</p></div>";
            else { Unexpected = true; throw new InvalidOperationException("意外的模拟传输请求"); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(content)});
        }
    }
}