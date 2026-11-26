using System;
using System.Linq;
using System.Collections.Generic;

namespace GameDailyWidget;

// One wording for remaining and upcoming times, shared by the overview rows and the 活动/卡池 panels.
internal static class CountdownText
{
    internal static string Format(TimeSpan time) => time.TotalDays >= 1 ? $"{time.Days}天{time.Hours}时" :
        $"{Math.Max(0,(int)time.TotalHours)}时{Math.Max(0,time.Minutes)}分";

    internal static string Until(TimeSpan time) => time.TotalDays >= 1
        ? $"{Math.Ceiling(time.TotalDays)} 天后开始" : $"{Format(time)}后开始";
}

internal sealed record DeadlineSummary(string Name,string Time,string Tip,double? Fraction,bool Urgent=false,string? Key=null)
{
    internal static double? Ratio(DateTimeOffset? end,DateTimeOffset now) =>
        end is {} value ? Math.Clamp((value-now).TotalDays/14,0,1) : null;

    private static string Stamp(DateTimeOffset? at,TimeZoneInfo zone) => at is {} t
        ? DailyReminderSchedule.Local(t,zone,"MM/dd HH:mm")+"（北京时间 "+t.ToOffset(EventParser.China).ToString("MM/dd HH:mm")+"）" : "时间待公告";

    // Same wording as the 活动 and 卡池 panels; remaining time is never rounded up.
    private static string Left(DateTimeOffset? end,DateTimeOffset now,bool claim=false) => end is not {} t ? "时间待公告" :
        (claim ? "领奖剩 " : "剩 ") + CountdownText.Format(t-now);

    internal static DeadlineSummary Pools(IEnumerable<GamePool> source,DateTimeOffset now,TimeZoneInfo zone)
    {
        var pools=source.Where(p=>!(p.End<=now)).OrderBy(p=>p.End??DateTimeOffset.MaxValue)
            .ThenBy(p=>p.Kind=="武器").ThenBy(p=>p.Name,StringComparer.Ordinal).ToList();
        var chosen=pools.Where(p=>p.Start<=now).ToList();
        if(chosen.Count==0)
        {
            var start=pools.Where(p=>p.Start>now).Min(p=>p.Start);
            chosen=pools.Where(p=>p.Start==start).ToList();
        }
        if(chosen.Count==0) return new("暂无已公布的卡池","","暂无已公布的卡池",0);
        var names=chosen.SelectMany(p=>p.Items.Select(i=>i.Name)).Distinct();
        var end=chosen.Min(p=>p.End);
        var time=chosen[0].Start is {} next && next>now ? CountdownText.Until(next-now) : Left(end,now);
        return new("UP "+string.Join(" · ",names),time,string.Join("\n",chosen.Select(p=>p.Name+" · "+string.Join("、",p.Items.Select(i=>i.Name))+"\n"+Stamp(p.Start,zone)+" 至 "+Stamp(p.End,zone))),Ratio(end,now));
    }

    internal static DeadlineSummary Activity(EventData? data,string game,DateTimeOffset now,TimeZoneInfo zone)
    {
        var e=data?.Events.Where(e=>e.Game==game && e.Start<=now && !(e.Deadline<=now) &&
            (e.Deadline.HasValue || e.EndPending) && data.Marks.GetValueOrDefault(e.Key) is not ({Claimed:true} or {Ignored:true}))
            .OrderBy(e=>e.Deadline??DateTimeOffset.MaxValue).ThenBy(e=>e.Key,StringComparer.Ordinal).FirstOrDefault();
        return e==null ? new("暂无进行中的限时活动","","暂无进行中的限时活动",0) :
            new(e.Name,e.Deadline.HasValue?Left(e.Deadline,now,e.ClaimDeadline!=null):"版本结束前",e.Name+"\n"+Stamp(e.Start,zone)+" 至 "+Stamp(e.Deadline,zone),
                Ratio(e.Deadline,now),e.Deadline-now<TimeSpan.FromDays(1),e.Key);
    }
}
