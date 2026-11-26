using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FakeController = GameDailyWidget.DailyResetCoordinatorChecks.FakeController;

namespace GameDailyWidget;

internal static class DailyReminderScheduleChecks
{
    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition,string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add("PASS " + label);
        }
        static DateTimeOffset U(int m,int d,int h,int min=0) => new(2027,m,d,h,min,0,TimeSpan.Zero);
        var jan = U(1,10,2);
        var east = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var china = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        foreach (var (id,at) in new[] { ("China Standard Time",U(1,10,2)),("Eastern Standard Time",U(1,10,15)),
            ("India Standard Time",U(1,10,4,30)),("New Zealand Standard Time",U(1,9,21)),("UTC",U(1,10,10)) })
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            Check(DailyReminderSchedule.Slots(jan,zone,["10:00"]).Single().At == at &&
                DailyReminderSchedule.Slots(jan,zone,null).Select(s => s.At).SequenceEqual(new[]{U(1,10,19,30),U(1,10,19,45),U(1,10,19,55)}),
                id + " 的自定义钟点和默认换日前三档");
        }
        Check(DailyReminderSchedule.Slots(U(3,14,7),east,["02:30"]).Single().At == U(3,14,7) &&
            DailyReminderSchedule.Slots(U(11,7,7),east,["01:30"]).Single().At == U(11,7,5,30),
            "夏令时缺口落到跳时结束，回拨时间只取首次");
        Check(DailyReminderSchedule.Slots(U(3,14,8),east,["15:30"]).Single().At == U(3,13,20,30) &&
            DailyReminderSchedule.Slots(U(11,7,8),east,["15:30"]).Count == 0,
            "一个游戏日内跨两个本地日期取首次，没有候选则跳过");
        Check(new[]{U(3,13,12),U(3,14,12),U(11,6,12),U(11,7,12)}.Select(at =>
            DailyReminderSchedule.Local(DailyReminderSchedule.NextReset(at),east)).SequenceEqual(["15:00","16:00","16:00","15:00"]),
            "春秋两次切换前后分别换算本地刷新时间");
        static UnifiedSettings Load(string json) => JsonSerializer.Deserialize<UnifiedSettings>(json)!;
        Check(Load("{}").DailyReminderTimes == null && Load("""{"DailyReminderTimes":[]}""").DailyReminderTimes?.Count == 0 &&
            Load("""{"Scale":2,"DailyReminderTimes":123}""") is {Scale:2,DailyReminderTimes:null} &&
            Load("""{"DailyReminderTimes":[null,"bad"]}""").DailyReminderTimes == null &&
            Load("""{"DailyReminderTimes":["9:00","09:00",{},"bad","02:00","03:00","04:00","05:00","06:00"]}""")
                .DailyReminderTimes!.SequenceEqual(["09:00","02:00","03:00","04:00","05:00"]),
            "旧设置、关闭、错误类型、无有效项、去重和最多五项");
        Check(ReminderTimesWindow.Validate("9:00",[],jan,china,out var time) == null && time == "09:00" &&
            new[]{"99:99","24:00","9:0","text","04:00","12:00"}.All(x => ReminderTimesWindow.Validate(x,["12:00"],jan,china,out _) != null) &&
            ReminderTimesWindow.Validate("21:30",["01:00","02:00","03:00","05:00","06:00"],jan,china,out _) != null,
            "编辑器接受 24 小时制，拒绝非法、重复、换日时刻或第六项");

        var now = jan;
        var currentZone = TimeZoneInfo.Utc;
        var settings = UnifiedSettings.Load(true);
        using var first = new FakeController("wuwa");
        using var second = new FakeController("endfield");
        GameViewState State(string game) => new() {GameKey=game,UpdatedAt=now,LoggedIn=true,Daily=20,MaxDaily=100,DailyState="incomplete"};
        bool network = true;
        first.Work = () => { first.State=State("wuwa") with {Error=network ? null : "offline"}; return Task.CompletedTask; };
        second.Work = () => { second.State=State("endfield") with {Error=network ? null : "offline"}; return Task.CompletedTask; };
        var sent = new List<(DailyReminderMessage Message,DateTimeOffset Slot)>();
        PreResetDailyReminder Create() => new(first,second,settings,(message,_,slot) => {sent.Add((message,slot)); return true;},() => currentZone);
        PreResetDailyReminder Start(bool launch,params string[] times)
        {
            settings = UnifiedSettings.Load(true);
            settings.StartupReminderEnabled = launch;
            settings.DailyReminderTimes = times.ToList();
            first.State = State("wuwa"); second.State = State("endfield");
            network=true;
            sent.Clear();
            return Create();
        }
        void Tick(PreResetDailyReminder r, DateTimeOffset at) {now=at;r.Tick(now);}
        foreach (var (id,at) in new[]{("China Standard Time",U(1,10,2)),("Eastern Standard Time",U(1,10,15)),
            ("India Standard Time",U(1,10,4,30)),("New Zealand Standard Time",U(1,9,21)),("UTC",U(1,10,10))})
        {
            currentZone=TimeZoneInfo.FindSystemTimeZoneById(id);
            var run=Start(false,"10:00");
            Tick(run,at.AddSeconds(-1)); Tick(run,at); Tick(Create(),at.AddSeconds(15));
            Check(sent.Count==1 && sent[0].Slot==at,id+" 到点检查一次，重启去重");
        }
        currentZone = TimeZoneInfo.Utc;
        var r = Start(false,"09:00","10:00");
        Tick(r,U(1,10,8)); var calls=first.Calls; Tick(r,U(1,10,11));
        Check(sent.Count==1 && sent[0].Slot==U(1,10,10) && sent[0].Message.Body.Contains("补发 10:00") && first.Calls==calls+1,
            "睡眠错过多点合并，按最近时点刷新一次");
        r=Start(true,"09:00"); calls=first.Calls; Tick(r,U(1,10,10)); Tick(Create(),U(1,10,11));
        Check(sent.Count==0 && first.Calls==calls && settings.LastPreResetReminderSlot==U(1,10,9),"开启启动概览，过去时点直接记账");
        r=Start(false,"09:00"); Tick(r,U(1,10,10));
        Check(sent.Count==1 && sent[0].Message.Body.Contains("补发 09:00"),"关闭启动概览后补发错过的一点");
        r=Start(true);calls=first.Calls;Tick(r,U(1,10,11));
        Check(sent.Count==0 && calls==first.Calls && StartupDailyReminder.ShouldSend(false,settings.StartupReminderEnabled),
            "删空列表独立关闭换日前提醒");
        r=Start(false,"09:00","12:00");Tick(r,U(1,10,9));currentZone=east;
        Tick(r,U(1,10,14));Tick(r,U(1,10,17));
        Check(sent.Select(n=>n.Slot).SequenceEqual([U(1,10,9),U(1,10,17)]),"系统切换时区后已处理钟点去重，下一点按新时区");
        currentZone=TimeZoneInfo.Utc;
        r=Start(false,"09:00");Tick(r,U(1,10,8));network=false;Tick(r,U(1,10,10));
        calls=first.Calls;now=U(1,10,10,4);first.State=State("wuwa");Tick(r,now);
        Check(sent.Count==1 && calls==first.Calls && sent[0].Message.Body.Contains("补发 09:00"),"睡醒离线保持待定，四分钟后新读数触发，不重复查询");
        r=Start(false,"09:00");network=false;Tick(r,U(1,10,10));now=U(1,10,10,5);first.State=State("wuwa");Tick(r,now);
        Check(sent.Count==0,"补发满五分钟放弃");
        var wake=U(1,10,10);
        r=Start(false,"09:00");network=false;Tick(r,wake);calls=first.Calls;var other=second.Calls;
        Tick(r,wake.AddSeconds(10));
        Check(sent.Count==0 && first.Calls==calls && second.Calls==other,"唤醒离线后 15 秒内不重复查询");
        network=true;Tick(r,wake.AddSeconds(15));Tick(r,wake.AddSeconds(16));
        Check(sent.Count==1 && first.Calls==calls+1 && second.Calls==other+1 && sent[0].Message.Body.Contains("补发 09:00"),
            "唤醒时网络未就绪，15 秒后重试刷新并补发");
        r=Start(false,"09:00");network=false;calls=first.Calls;
        for (var s=0;s<=310;s+=5) Tick(r,wake.AddSeconds(s));
        Check(sent.Count==0 && first.Calls==calls+5,"持续离线最多重试 4 次，满五分钟放弃");
        r=Start(false,"09:00");network=false;Tick(r,wake);
        second.State=State("endfield") with {Daily=100,DailyState="complete"};
        calls=first.Calls;other=second.Calls;network=true;Tick(r,wake.AddSeconds(15));Tick(r,wake.AddSeconds(16));
        Check(sent.Count==1 && first.Calls==calls+1 && second.Calls==other && !sent[0].Message.Body.Contains("终末地"),
            "重试只刷新还没拿到读数的游戏");
        r=Start(false,"09:00","10:03");network=false;Tick(r,U(1,10,10));network=true;Tick(r,U(1,10,10,3));
        Check(sent.Count==1 && !sent[0].Message.Body.Contains("补发") && sent[0].Slot==U(1,10,10,3),"下一提醒点取代旧的待定补发");
        r=Start(false,"19:55");network=false;Tick(r,U(1,10,19,58));now=U(1,10,20);first.State=State("wuwa");Tick(r,now);
        Check(sent.Count==0,"跨过 04:00 后不补发旧通知");
        r=Start(false);Tick(r,U(1,10,8));settings.DailyReminderTimes=["09:00","12:00"];r.SettingsChanged(U(1,10,11));
        Tick(Create(),U(1,10,11));Tick(r,U(1,10,12));
        Check(sent.Count==1 && sent[0].Slot==U(1,10,12),"保存新时间后不补过去时点，重启仍有效");

        now=U(1,10,17,30);
        var msg=StartupDailyReminder.ComposeBeforeReset(State("wuwa"),State("endfield"),now,now,east,"09:00")!;
        var xml=NativeDailyNotifications.Content(msg).GetXml();
        Check(xml.SelectSingleNode("//binding/text[1]")?.InnerText=="日常还没清完 · 距刷新还有 2 小时 30 分" &&
            xml.SelectSingleNode("//binding/text[2]")!.InnerText.EndsWith("补发 09:00 的提醒\n刷新：本地 15:00（北京时间 04:00）"),
            "从 Windows 通知 XML 回读实际倒计时、本地刷新和补发钟点");
        Check(DailyReminderSchedule.ResetText(now,china)=="北京时间 04:00" && DailyReminderSchedule.Remaining(U(1,10,19,45))=="15 分钟",
            "东八区不重复标时，不足一小时只显示分钟");
        static int Count(string text,string part) => (text.Length - text.Replace(part,"").Length) / part.Length;
        var winter = ReminderTimesWindow.ZoneText(east,U(1,10,12));
        var summer = ReminderTimesWindow.ZoneText(east,U(7,10,12));
        var custom = ReminderTimesWindow.ZoneText(TimeZoneInfo.CreateCustomTimeZone("example",TimeSpan.FromHours(5.5),"示例","示例"),U(1,10,12));
        Check(Count(winter,"UTC-05:00")==1 && !winter.Contains("夏令时") && Count(summer,"UTC-05:00")==1 &&
            summer.EndsWith("现为夏令时 UTC-04:00") && custom=="时区：跟随系统 · UTC+05:30 示例",
            "时区说明不重复偏移，夏令时期间注明当前偏移");
        return checks;
    }
}
