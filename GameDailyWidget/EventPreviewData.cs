using System.Linq;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GameDailyWidget;

internal static class EventPreviewData
{
    internal static EventStore Create(DateTimeOffset now, bool claimed, bool failed, bool empty, int count = 3, bool progressDemo = false)
    {
        var store = new EventStore();
        foreach (var game in new[] { "wuwa", "endfield" })
        {
            var title = game == "wuwa" ? "「旧梦凋零的百合」3.8版本内容说明" : "「穿越甚大蘑境与你相逢」版本更新说明";
            store.Data.Articles[game + ":demo"] = new(now.AddDays(-3),now.AddDays(-3),Title:title);
            var next = now.AddDays(game == "wuwa" ? 19 : 27).AddMinutes(-1);
            store.Data.Maintenance[game] = [new("演示后续版本",now.AddDays(-1),next,next.AddHours(3))];
            store.Data.Sync[game] = new(now.AddHours(-2), now, failed ? "同步失败" : null);
            if (empty) continue;
            var wuwa = game == "wuwa";
            var kind = wuwa ? "角色" : "干员";
            var source = new EventSource("0", "演示卡池公告", "");
            store.Data.Pools.Add(new(game + "|demo-character", game, wuwa ? "浮光来信" : "前哨来客", kind,
                now.AddDays(-2), now.AddDays(wuwa ? 8 : 6), false,
                [new(wuwa ? "演示共鸣者" : "演示干员", wuwa ? 5 : 6, kind)], source, "演示", now));
            store.Data.Pools.Add(new(game + "|demo-weapon", game, wuwa ? "长夜回声" : "远星申领", "武器",
                now.AddDays(5), null, true, [new(wuwa ? "星垂" : "远星", wuwa ? 5 : 6, wuwa ? "迅刀" : "施术单元")],
                source, "演示", now));
            if (progressDemo && game == "endfield") continue;
            var sample = Probe(now) with { Game = game, Currency = game == "endfield" ? new("嵌晶玉",1200) : new("星声",null) };
            var names = game == "wuwa" ? new[] { "演示巡游","拾光签到","远岸探索" } : new[] { "林间采集","前哨签到","谷地勘察" };
            for (var i=0; i<count; i++)
            {
                var name = i < names.Length ? names[i] : $"演示活动 {i + 1}";
                var item = sample with { Key = game + "|" + name + "|demo", Name=name,
                    Kind = i == 1 ? "签到活动" : "探索活动", Start=i==2 && !claimed ? now.AddDays(3) : sample.Start,
                    End=i==0 ? sample.End : i==2 ? null : now.AddDays(4 + i), EndPending=i==2 };
                store.Data.Events.Add(item);
                if (claimed || i==1) store.Mark(item,true,false,now);
            }
        }
        return store;
    }

    internal static EventData Summaries(string scenario,DateTimeOffset now)
    {
        var data=Create(now,false,false,false).Data;
        foreach(var game in new[]{"wuwa","endfield"})
        {
            var p=data.Pools.First(p=>p.Game==game);
            data.Pools.Add(p with {Key=p.Key+"-second",Kind="武器",Items=[new("演示武器",5,"武器")]});
        }
        if(scenario=="no-pools")data.Pools.Clear();
        if(scenario=="no-events")data.Events.Clear();
        if(scenario=="pending") {
            for(int i=0;i<data.Pools.Count;i++)data.Pools[i]=data.Pools[i] with {End=null};
            for(int i=0;i<data.Events.Count;i++)data.Events[i]=data.Events[i] with {End=null,ClaimDeadline=null,EndPending=true};
        }
        if(scenario=="next")for(int i=0;i<data.Pools.Count;i++)data.Pools[i]=data.Pools[i] with {Start=now.AddDays(2)};
        if(scenario!="urgent")for(int i=0;i<data.Events.Count;i++)
            if(data.Events[i].End-now<TimeSpan.FromDays(1))data.Events[i]=data.Events[i] with {End=now.AddDays(5)};
        return data;
    }

    internal static Dictionary<string,string> AddProgressPreview(EventStore store, DateTimeOffset now, bool complete)
    {
        var start = now.AddDays(-1);
        var end = now.AddDays(2);
        foreach (var name in new[] { "演示合约", "战争回响·演示赛季" })
            store.Data.Events.Add(Probe(now) with { Game="endfield", Key="endfield|" + name + "|demo", Name=name,
                Kind="挑战", Start=start, End=end, Currency=new("嵌晶玉",null) });
        var mission = new {count=1,total=1};
        var detail = JsonSerializer.SerializeToElement(new
        {
            @base = new { roleId="demo" },
            crisisContract = new[] { new { name="演示合约",startAtTs=start.ToUnixTimeSeconds(),endAtTs=end.ToUnixTimeSeconds(),
                gameplayEndAtTs=end.ToUnixTimeSeconds(),weeklyMission=mission,indicatorMission=mission,
                stageMission=new {count=complete ? 2 : 1,total=2} } },
            warEchoes = new { seasons=new[] { new {name="演示赛季",stars=4,startTs=start.ToUnixTimeSeconds(),endTs=end.ToUnixTimeSeconds()} } }
        });
        var reading = new EndfieldProgressReading("demo",now,EndfieldEventProgress.Parse(detail,"demo"));
        var state = new GameViewState { GameKey="endfield",LoggedIn=true,SelectedAccountKey="demo",UpdatedAt=now };
        return EndfieldEventProgress.Apply(store,reading,state,now);
    }

    internal static GameEvent Probe(DateTimeOffset now) => new("wuwa|演示巡游|demo","wuwa","演示巡游","探索活动",
        now.AddDays(-2),now.AddHours(2),false,null,new("星声",null),new("0","演示公告",""),"演示",now.AddDays(-2));
}
