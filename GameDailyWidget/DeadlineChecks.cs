using System;
using System.Linq;

namespace GameDailyWidget;

internal static class DeadlineChecks
{
    internal static void Run(EventData data, DateTimeOffset now)
    {
        var zone=TimeZoneInfo.Utc;
        void Check(bool pass,string what) { if(!pass) throw new InvalidOperationException("摘要规则 "+what); }
        foreach(var game in new[]{"wuwa","endfield"})
        {
            var a=data.Pools.Single() with { Game=game,Name="甲",Start=now,End=now.AddDays(7),Kind="干员",Items=[new("甲",6,"干员")] };
            var b=a with {Kind="武器",Items=[new("乙",6,"武器")]};
            var c=a with {End=now.AddDays(4),Items=[new("丙",5,"角色")]};
            var merged=DeadlineSummary.Pools([b,a,c],now,zone);
            Check(merged.Name=="UP 丙 · 甲 · 乙" && merged.Time=="剩 4天0时" && merged.Tip.Contains("北京时间"),game+"合并");
            // The overview uses the panels' wording and never rounds remaining time up.
            Check(DeadlineSummary.Pools([a with {End=now.AddDays(6).AddHours(11).AddMinutes(30)}],now,zone).Time=="剩 6天11时","卡池不向上取整");
            Check(DeadlineSummary.Pools([a with {Start=now.AddHours(5).AddMinutes(20)}],now,zone).Time=="5时20分后开始","卡池一天内开始");
            Check(DeadlineSummary.Pools([a with {End=null}],now,zone).Fraction==null,"待公告");
            Check(DeadlineSummary.Pools([a with {Start=now.AddDays(2)}],now,zone).Time=="2 天后开始","下一期");
            Check(DeadlineSummary.Pools([],now,zone).Name=="暂无已公布的卡池","无卡池");
        }
        var e=data.Events.First();
        var example=new EventData();
        EventPreviewData.WuwaPools(example,now,"groups");
        var newPools=example.Pools.Where(p=>p.NewUp).ToArray();
        var earliest=newPools.Min(p=>p.End)!.Value;
        var summary=DeadlineSummary.Pools(example.Pools,now,zone);
        Check(summary.Name=="UP 演示共鸣者 · 演示武器" && summary.Time=="剩 "+CountdownText.Format(earliest-now) &&
            example.Pools.All(p=>summary.Tip.Contains(p.Name)), "鸣潮合并当期新五星，采用最早截止，悬停含复刻及忆旅");
        example.Pools.RemoveAll(p=>p.NewUp);
        Check(DeadlineSummary.Pools(example.Pools,now,zone).Name=="UP 演示回归角色","新 UP 缺席优先非忆旅复刻");
        var rerun=example.Pools.Single(p=>p.Kind!="忆旅");
        example.Pools.Remove(rerun);
        example.Pools.Add(rerun with {Start=now.AddDays(2)});
        Check(DeadlineSummary.Pools(example.Pools,now,zone).Time=="2 天后开始","仅忆旅开放时显示下一期");
        example.Pools.RemoveAll(p=>p.Kind!="忆旅");
        Check(DeadlineSummary.Pools(example.Pools,now,zone).Name=="暂无已公布的卡池","仅剩忆旅时保留悬停明细");
        var d=new EventData();
        d.Events.AddRange(data.Events);foreach(var p in data.Marks)d.Marks[p.Key]=p.Value;
        d.Events[0]=e with {End=now.AddHours(1),ClaimDeadline=now.AddDays(3)};
        d.Events.Add(e with {Key="first",End=now.AddHours(24)});
        var row=DeadlineSummary.Activity(d,"wuwa",now,zone);
        Check(row.Key=="first" && !row.Urgent,"领奖截止与24小时");
        d.Events[^1]=d.Events[^1] with {End=now.AddMinutes(1439)};
        Check(DeadlineSummary.Activity(d,"wuwa",now,zone).Urgent,"23:59");
        string Only(DateTimeOffset end,DateTimeOffset? claim=null)
        {
            var single=new EventData();
            single.Events.Add(e with {Key="only",Game="wuwa",Start=now.AddDays(-1),End=end,ClaimDeadline=claim,EndPending=false});
            return DeadlineSummary.Activity(single,"wuwa",now,zone).Time;
        }
        Check(Only(now.AddMinutes(119))=="剩 1时59分","1时59分不写成2小时");
        Check(Only(now.AddDays(6).AddHours(11).AddMinutes(59))=="剩 6天11时","6天11时不写成7天");
        Check(Only(now.AddDays(5),now.AddHours(30))=="领奖剩 1天6时","领奖截止沿用面板写法");
        Check(CountdownText.Format(TimeSpan.FromMinutes(119))=="1时59分" && CountdownText.Format(TimeSpan.FromDays(6.5))=="6天12时" &&
            CountdownText.Until(TimeSpan.FromDays(5.8))=="6 天后开始","面板与总览共用同一写法");
        d.Events.RemoveAll(x=>x.Key=="first" || x.Key==e.Key);
        Check(DeadlineSummary.Activity(d,"wuwa",now,zone).Name=="暂无进行中的限时活动","排除未来、忽略、领完");
        d.Events.Add(e with {End=null,EndPending=true});
        Check(DeadlineSummary.Activity(d,"wuwa",now,zone).Time=="版本结束前","待定活动");
        foreach(int days in new[]{0,7,14,18}) Check(DeadlineSummary.Ratio(now.AddDays(days),now)==Math.Min(1,days/14d),"剩余比例");
    }
}
