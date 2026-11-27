using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameDailyWidget;

internal static class WuwaNoticeChecks
{
    internal static HttpResponseMessage EmptyResponse() => JsonResponse("""{"recommend":[],"game":[],"activity":[]}""");
    private static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK)
        { Content=new StringContent(text,Encoding.UTF8,"application/json") };

    private sealed class Transport : HttpMessageHandler
    {
        internal Func<HttpResponseMessage> Reply = EmptyResponse;
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if (request.RequestUri!.AbsoluteUri == EventFeed.NoticeUrl)
            {
                Calls++;
                return Task.FromResult(Reply());
            }
            if (request.RequestUri.AbsoluteUri == EventFeed.Cdn+"ArticleMenu.json") return Task.FromResult(JsonResponse("[]"));
            if (request.RequestUri.AbsoluteUri == EventFeed.News)
                return Task.FromResult(JsonResponse("""self.__next_f.push([1,"7:{\"bulletins\":[],\"total\":0}"])"""));
            throw new InvalidOperationException("意外的网络请求");
        }
    }

    internal static async Task RunAsync(Action<bool,string> check)
    {
        using var stream = typeof(EventChecks).Assembly.GetManifestResourceStream("wuwa-notices.json")!;
        using var reader = new StreamReader(stream);
        var fixture = reader.ReadToEnd();
        using var doc = JsonDocument.Parse(fixture);
        var notices = EventFeed.ReadNotices(doc.RootElement);
        var endpoint = EventFeed.NoticeAddress(null);
        var versionStart = new DateTimeOffset(2026,11,12,11,0,0,EventParser.China);
        var now = versionStart.AddDays(2);
        var maintenance = new List<Maintenance>{new("3.8",versionStart.AddDays(-1),versionStart.AddHours(-7),versionStart)};
        GamePool Parse(WuwaNotice n) => WuwaNoticeParser.Parse(n,maintenance,now,endpoint);
        var pools = notices.Select(Parse).ToList();
        var dec3 = new DateTimeOffset(2026,12,3,10,0,0,EventParser.China);
        check(pools.Count == 8 && pools.All(p => p.Start == versionStart && !p.NoticeTime) &&
            pools.Count(p => p.End == dec3) == 6 && pools.Count(p => p.Kind == "忆旅" && p.End == dec3.AddDays(18).AddHours(2)) == 2,
            "真实八池按正文及维护结束定时，忆旅截止早于公告下架");
        var hero = pools.Single(p => p.Name == "盛放于昨日梦土的花朵");
        var weapon = pools.Single(p => p.Name == "安魂");
        check(hero.Items.Select(i=>i.Name).SequenceEqual(["莉莉","釉瑚","秋水","秧秧"]) &&
            weapon.Items.Select(i=>i.Name).SequenceEqual(["安魂","华彩乐段","尘云旋臂","异度"]) &&
            new[]{hero,weapon}.All(p=>p.Items.Select(i=>i.Stars).SequenceEqual([5,4,4,4])) &&
            pools.Where(p=>p.Kind=="忆旅").All(p=>p.Items.Count(i=>i.Stars==5)==4),"五星与顿号连接的四星名单、忆旅自选名单");
        var n = notices[0];
        GamePool Body(string content) => Parse(n with {Content=content});
        check(Body("✦开放时间✦：2026-11-13 12:00 至 2026-12-03 09:59") is {Start:{} s,End:{} e} &&
            s == versionStart.AddDays(1).AddHours(1) && e == dec3,"同一行符号标签与明确起止日期");
        check(Body("唤取结束时间：2026年12月3日09:59") is {Start:null,End:{} stop,NoticeTime:false} && stop==dec3 &&
            Body("活动时间：截至2026年12月3日09:59").End==dec3,"仅结束时间不把展示时间用作开放时间");
        check(Body("未公布时段") is {NoticeTime:true,Start:{} start,End:{} end} && start==versionStart && end==dec3 &&
            Parse(n with { Content="",DisplayStart=now }).Start==now,"元数据回退取较晚开始并保留时间来源");
        var nodes = JsonNode.Parse(fixture)!;
        var recommended = nodes["recommend"]!.AsArray();
        var first=recommended[0]!.DeepClone();
        recommended.RemoveAt(0);
        nodes["game"]!.AsArray().Add(first.DeepClone());
        first["permanent"]=1;
        nodes["activity"]!.AsArray().Add(first);
        using var moved=JsonDocument.Parse(nodes.ToJsonString());
        check(EventFeed.ReadNotices(moved.RootElement).Count==8,"跨栏目读取并排除永久公告与非卡池");

        var custom=EventFeed.NoticeUrl.Replace("76402e5b20be2c39f095a152090afddc","new-code");
        check(EventFeed.NoticeAddress(JsonSerializer.Deserialize<UnifiedSettings>(JsonSerializer.Serialize(
            new UnifiedSettings {WuwaNoticeUrl=custom}))!.WuwaNoticeUrl).AbsoluteUri==custom,"settings.json 公告标识覆盖");
        foreach(var invalid in new[]{custom.Replace("https:","http:"),custom.Replace(".com/",".com.invalid/"),
            custom.Replace("https://","https://user@"),custom+"?uid=x",custom.Replace("zh-Hans.json","banner.jpg")})
        {
            bool rejected=false;
            try {EventFeed.NoticeAddress(invalid);} catch(InvalidDataException){rejected=true;}
            check(rejected,"拒绝非官方公告地址 "+invalid);
        }

        var transport=new Transport();
        using var feed=new EventFeed(transport);
        foreach (var response in new Func<HttpResponseMessage>[] {
            () => new(HttpStatusCode.NotFound), () => JsonResponse("""{"game":{}}"""),
            () => new(HttpStatusCode.OK){Content=new StringContent("{}",Encoding.UTF8,"text/html")},
            () => JsonResponse(new string(' ',EventFeed.NoticeLimit+1)) })
        {
            transport.Reply=response;
            var rejected=false;
            try {await feed.NoticesAsync(null,CancellationToken.None);}
            catch(Exception ex) when(ex is HttpRequestException or InvalidDataException or InvalidOperationException or KeyNotFoundException){rejected=true;}
            check(rejected,"读取器拒绝无效响应 "+transport.Calls);
        }

        var data=new EventStore();
        var fallback=hero with {Start=null,End=null,TimePending=true,NewUp=true,Items=[hero.Items[0]]};
        data.Data.Articles["wuwa:1"]=new(now,now,EventParser.Revision,ParsedPools:[fallback],NewWuwaItems:["莉莉","安魂"],Title:"3.8版本内容说明");
        data.Data.Maintenance["wuwa"]=maintenance;
        var sync=new EventSyncCoordinator(data,feed,CancellationToken.None,()=>now);
        transport.Reply=()=>new(HttpStatusCode.NotFound);
        await sync.SyncAsync(now);
        check(data.Data.WuwaNoticePools==null && data.Data.Pools.Single().End==null && data.Data.Sync["wuwa-notices"].Error!=null,
            "首次失败回退官网时间待公告");
        var calls=transport.Calls;
        transport.Reply=()=>JsonResponse(fixture);
        now=now.AddMinutes(9);sync.Tick(now);
        check(transport.Calls==calls,"公告失败后十分钟之前不重试");
        now=now.AddMinutes(1);sync.Tick(now);
        check(data.Data.Pools.Count==8 && data.Data.Pools.Count(p=>p.NewUp)==2 &&
            data.Data.Pools.Single(p=>p.Name==hero.Name) is {End:{} merged,Items.Count:4,NewUp:true} && merged==dec3,
            "十分钟恢复并去重，时间和名单来自游戏内公告，新 UP 来自版本说明");
        calls=transport.Calls;
        now=now.AddHours(6).AddSeconds(-1);sync.Tick(now);
        check(transport.Calls==calls,"成功后六小时之前不重试");
        transport.Reply=()=>JsonResponse("{}");
        now=now.AddSeconds(1);sync.Tick(now);
        check(data.Data.Sync["wuwa-notices"].Error!=null && PoolCatalog.Current(data.Data,"wuwa",now).Count()==8 &&
            !PoolCatalog.Current(data.Data,"wuwa",dec3.AddDays(30)).Any(),"结构损坏保留上次成功结果，截止后不再呈现");
        transport.Reply=EmptyResponse;
        now=dec3.AddDays(30);
        await sync.SyncAsync(now);
        check(!PoolCatalog.Current(data.Data,"wuwa",now).Any(),"公告下架后官网的待定条目不会重新显示已结束卡池");
    }
}
