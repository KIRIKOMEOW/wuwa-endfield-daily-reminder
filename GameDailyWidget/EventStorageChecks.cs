using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

internal static partial class EventChecks
{
    internal static async Task<IReadOnlyList<string>> RunStorageAsync()
    {
        var checks = new List<string>();
        void Check(bool ok, string title)
        {
            if (!ok) throw new InvalidOperationException(title);
            checks.Add("PASS " + title);
        }
        var dir = Path.Combine(Path.GetTempPath(), "GameDailyWidget-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "events.json");
        try
        {
            await WuwaNoticeChecks.RunAsync(Check);
            var store = new EventStore(path);
            var article = Article("wuwa", "123", "3.8版本内容说明");
            var item = EventParser.Parse(article, ["[样本]签到活动", "✦活动时间：3.8版本期间"]).Single();
            store.Data.Events.Add(item);
            store.Mark(item, true, false, SampleTime);
            store.Data.Sent[item.Key + "|slot"] = SampleTime;
            store.Save();
            Check(new EventStore(path).Data.Marks[item.Key].Claimed && Directory.GetFiles(dir).Length == 1, "原子替换后可重新读取标记，无残留临时文件");
            var legacyFile = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            legacyFile["schema"] = 1;
            legacyFile.Remove("pools");
            File.WriteAllText(path, legacyFile.ToJsonString());
            var migrated = new EventStore(path);
            migrated.Save();
            var reloaded = new EventStore(path);
            Check(reloaded.Warning == null && reloaded.Data.Schema == 3 && reloaded.Data.Pools.Count == 0 &&
                reloaded.Data.Events.Single() == item && reloaded.Data.Marks[item.Key].Claimed &&
                reloaded.Data.Sent[item.Key+"|slot"] == SampleTime,
                "schema 1 缺少卡池字段可原子升级为 schema 3，保留活动、标记与发送记录");
            legacyFile["schema"] = 2;
            var previousPool = new GamePool("endfield|test","endfield","演示","干员",SampleTime,null,true,
                [new("测试干员",6,"干员")],new("1","演示公告",""),"演示",SampleTime);
            legacyFile["pools"] = System.Text.Json.JsonSerializer.SerializeToNode(new[]{previousPool},EventStore.Json);
            legacyFile.Remove("wuwaNoticePools");
            File.WriteAllText(path,legacyFile.ToJsonString());
            var previousVersion = new EventStore(path);
            previousVersion.Save();
            var restored = new EventStore(path);
            Check(restored.Warning == null && restored.Data.Schema == 3 && restored.Data.WuwaNoticePools == null &&
                System.Text.Json.JsonSerializer.Serialize(restored.Data.Pools.Single()) == System.Text.Json.JsonSerializer.Serialize(previousPool) &&
                restored.Data.Events.Single() == item && restored.Data.Marks[item.Key].Claimed && restored.Data.Sent[item.Key+"|slot"] == SampleTime,
                "schema 2 升级保留终末地卡池、活动、标记和投递记录");
            EventSyncCoordinator.ResolvePending(store.Data, "wuwa", [
                new("3.8",SampleTime,SampleTime.AddDays(1),SampleTime.AddDays(2)),
                new("3.9",SampleTime.AddDays(2),SampleTime.AddDays(3),SampleTime.AddDays(4))]);
            Check(store.Data.Events.Single().End == SampleTime.AddDays(3), "只采用下一版本维护开始时刻");
            store.Clean(SampleTime.AddDays(17));
            Check(store.Data.Events.Count == 1, "恰好十四天保留");
            store.Clean(SampleTime.AddDays(17).AddSeconds(1));
            Check(store.Data.Events.Count + store.Data.Sent.Count + store.Data.Marks.Count == 0, "超十四天删除活动及关联记录");
            foreach (var bad in new[] {"bad json", "{}", """{"schema":2}"""})
            {
                File.WriteAllText(path, bad);
                var broken = new EventStore(path);
                broken.Save();
                Check(broken.Warning != null && File.ReadAllText(path) == bad, "损坏或不支持的存档原样保留 " + checks.Count);
            }
            var memory = new EventStore();
            var future = item with { Start = SampleTime.AddDays(1), Key = "upcoming" };
            memory.Mark(future,true,false,SampleTime);
            Check(!EventStore.CanClaim(future,null,SampleTime) && !memory.Data.Marks.ContainsKey(future.Key),
                "未开始的活动隐藏勾选，通知和手动领取均无法新增标记");
            memory.Mark(future,false,true,SampleTime);
            Check(memory.Data.Marks[future.Key].Ignored, "未开始的活动仍可忽略");
            memory.Data.Marks[future.Key] = new(true,false,SampleTime);
            Check(EventStore.CanClaim(future,memory.Data.Marks[future.Key],SampleTime), "旧版预先勾选的活动仍显示可撤销的勾选框");
            memory.Mark(future,false,false,SampleTime);
            Check(!memory.Data.Marks[future.Key].Claimed && EventStore.CanClaim(future,null,future.Start),
                "允许撤销旧标记，恰好开始即可正常勾选");
            memory.Data.Events.Add(item);
            var cached = item with { Game="endfield", Key="endfield|缓存|2026-11-12", Name="缓存" };
            memory.Data.Events.Add(cached);
            memory.Data.Sync["endfield"] = new(SampleTime, SampleTime, null);
            using var handler = new FeedHandler();
            using var feed = new EventFeed(handler);
            var coordinator = new EventSyncCoordinator(memory, feed, CancellationToken.None);
            await coordinator.SyncAsync(SampleTime);
            await coordinator.SyncAsync(SampleTime.AddHours(6));
            Check(handler.BodyCalls == 1 && memory.Data.Articles.ContainsKey("wuwa:123"), "已解析的正文按版本缓存");
            Check(memory.Data.Sync["endfield"].Error != null && memory.Data.Sync["endfield"].LastSuccess == SampleTime
                && memory.Data.Events.Contains(cached) && memory.Data.Sync["wuwa"].Error == null && memory.Data.Events.Any(e => e.Name == "样本"), "单站失败保留上次成功，另一游戏正常");
            Check(handler.PrivateHeader == false && handler.Hosts.SetEquals(["media-cdn-mingchao.kurogame.com","endfield.hypergryph.com","aki-gm-resources-back.aki-game.com"]),
                "注入传输核对仅官网域名、通用 UA、无账号请求头");
            var upgrade = new EventStore();
            upgrade.Data.Events.Add(item);
            upgrade.Data.Marks[item.Key] = new(true,true,SampleTime);
            upgrade.Data.Sent[item.Key+"|slot"] = SampleTime;
            // Exact v1.4.0 stamp shape: no parser revision and no title.
            var legacy = System.Text.Json.JsonSerializer.Deserialize<ArticleStamp>(
                """{"fetchedAt":"2026-11-12T10:00:00+08:00","startTime":"2026-11-12T10:00:00+08:00"}""",EventStore.Json)!;
            upgrade.Data.Articles["wuwa:123"] = legacy;
            upgrade.Data.Articles["wuwa:124"] = legacy;
            upgrade.Data.Articles["wuwa:125"] = legacy with { StartTime=SampleTime.AddDays(-61) };
            using var upgradedTransport = new FeedHandler();
            using var upgradedFeed = new EventFeed(upgradedTransport);
            var upgradeSync = new EventSyncCoordinator(upgrade,upgradedFeed,CancellationToken.None);
            await upgradeSync.SyncAsync(SampleTime);
            await upgradeSync.SyncAsync(SampleTime.AddMinutes(1));
            Check(upgradedTransport.BodyCalls == 2 && upgrade.Data.Articles["wuwa:123"].ParserVersion == EventParser.Revision &&
                upgrade.Data.Articles["wuwa:124"].ParserVersion == EventParser.Revision && upgrade.Data.Articles["wuwa:125"].ParserVersion == 0,
                "无修订号的旧缓存升级，包括已退出首页的近六十天公告；升级后不重复请求");
            Check(upgrade.Data.Events.Any(e => e.Key == item.Key) && upgrade.Data.Marks[item.Key] == new EventMark(true,true,SampleTime) &&
                upgrade.Data.Sent[item.Key+"|slot"] == SampleTime,"升级保留原键、领完与忽略状态、发送记录");
            using var retryTransport = new FeedHandler();
            using var retryFeed = new EventFeed(retryTransport);
            var now = SampleTime;
            var scheduler = new EventSyncCoordinator(new(),retryFeed,CancellationToken.None,() => now);
            void At(TimeSpan elapsed)
            {
                now = SampleTime + elapsed;
                scheduler.Tick(now); // The injected in-memory transport completes synchronously.
            }
            At(TimeSpan.Zero);
            At(TimeSpan.FromSeconds(599));
            Check(retryTransport.EndfieldCalls == 1 && retryTransport.MenuCalls == 1,"失败后十分钟之前不触发任何重试");
            At(TimeSpan.FromMinutes(10));
            retryTransport.EndfieldHealthy = true;
            At(TimeSpan.FromMinutes(20));
            Check(retryTransport.EndfieldCalls == 3 && retryTransport.MenuCalls == 1, "连续失败每十分钟只重试失败站，恢复后成功");
            At(TimeSpan.FromHours(6));
            At(TimeSpan.FromMinutes(379));
            Check(retryTransport.EndfieldCalls == 3 && retryTransport.MenuCalls == 2 && retryTransport.BodyCalls == 1,
                "健康站原六小时周期不受另一站影响，已缓存正文不重复拉取");
            At(TimeSpan.FromMinutes(380));
            Check(retryTransport.EndfieldCalls == 4, "恢复成功后满六小时再同步");
            using var pages = new PageHandler();
            using var pagedFeed = new EventFeed(pages);
            var (archive,complete) = await pagedFeed.ListAsync("endfield",SampleTime,CancellationToken.None);
            Check(archive.Count == 2 && archive[1].Source.Id == "0102" && complete == null,
                "分页出现新 cid 时收录后续页并保留前导零");
            pages.Repeat = true;
            var (partial,coverage) = await pagedFeed.ListAsync("endfield",SampleTime,CancellationToken.None);
            Check(partial.Count == 1 && coverage != null && pages.Calls == 4,
                "官网重复首页时停止且明确标示历史覆盖范围");
        }
        finally { Directory.Delete(dir,true); }
        return checks;
    }

    private sealed class PageHandler : HttpMessageHandler
    {
        internal bool Repeat;
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)
        {
            Calls++;
            var id = Repeat || request.RequestUri!.Query == "" ? "0101" : "0102";
            var row = new { cid=id, title="演示版本活动", sticky=false, tab=id=="0102" ? "events" : "notices", displayTime=SampleTime.ToUnixTimeSeconds() };
            var json = System.Text.Json.JsonSerializer.Serialize(new { bulletins=new[] {row}, total=2 });
            var payload = System.Text.Json.JsonSerializer.Serialize("7:" + json);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content=new StringContent("<script>self.__next_f.push([1," + payload + "])</script>") });
        }
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        internal int BodyCalls, MenuCalls, EndfieldCalls;
        internal bool EndfieldHealthy;
        internal bool PrivateHeader;
        internal HashSet<string> Hosts = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var uri = request.RequestUri!;
            Hosts.Add(uri.Host);
            PrivateHeader |= request.Headers.Any(h => h.Key != "User-Agent") || request.Headers.UserAgent.ToString() != "DesktopCalendar/1.0";
            if (uri.AbsoluteUri == EventFeed.NoticeUrl) return Task.FromResult(WuwaNoticeChecks.EmptyResponse());
            if (uri.Host == "endfield.hypergryph.com")
            {
                EndfieldCalls++;
                if (!EndfieldHealthy) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                const string emptyList = """self.__next_f.push([1,"7:{\"bulletins\":[],\"total\":0}"])""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(emptyList) });
            }
            string content;
            if (uri.AbsolutePath.EndsWith("Menu.json"))
            {
                MenuCalls++;
                content = """[{"articleId":123,"articleTitle":"3.8版本内容说明","articleType":52,"createTime":"2026-11-11 10:00:00","startTime":"2026-11-12 10:00:00"}]""";
            }
            else { BodyCalls++; content = """{"articleTitle":"3.8版本内容说明","articleContent":"<p>[样本]签到活动</p><p>✦活动时间：3.8版本期间</p>"}"""; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
