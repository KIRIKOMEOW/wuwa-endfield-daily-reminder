using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameDailyWidget;

internal static partial class EventChecks
{
    internal static IReadOnlyList<string> Progress()
    {
        var checks = new List<string>();
        void Check(bool ok,string label)
        {
            if (!ok) throw new InvalidOperationException(label);
            checks.Add("PASS " + label);
        }
        using var fixture = typeof(EventChecks).Assembly.GetManifestResourceStream("events.endfield-progress.json")!;
        var detail = JsonNode.Parse(fixture)!;
        var now = DateTimeOffset.FromUnixTimeSeconds(1893542400);
        var state = new GameViewState { GameKey="endfield",SelectedAccountKey="sample",LoggedIn=true,UpdatedAt=now };
        EndfieldProgressReading Read() => new("sample",now,
            EndfieldEventProgress.Parse(JsonSerializer.SerializeToElement(detail),"sample-role"));
        var reading = Read();
        var sample = EventPreviewData.Probe(now) with { Game="endfield", Start=now.AddDays(-1),End=now.AddDays(2) };
        GameEvent Event(string name) => sample with { Key="endfield|" + name, Name=name };
        var contract = Event("「演示合约」");
        var monument = Event("演示丰碑");
        var season = Event("战争回响·演示赛季");
        EventStore Store(params GameEvent[] events)
        {
            var store = new EventStore();
            store.Data.Events.AddRange(events);
            return store;
        }
        var store = Store(contract,monument,season);
        var labels = EndfieldEventProgress.Apply(store,reading,state,now);
        Check(labels[monument.Key] == "普通 2/2 · 苦难 1/2" && labels[contract.Key] == "进度 6/6",
            "规范化名称和重叠时间匹配，逐难度与三类任务进度");
        Check(labels[season.Key] == "进度 7 星" && !store.Data.Marks.ContainsKey(season.Key),
            "战争回响即使附加任务全满也仅显示星数");
        Check(store.Data.Marks.Count == 1 && store.Data.Marks[contract.Key] is { Claimed:true,Automatic:true } &&
            reading.Items.Single(r => r.Name=="演示合约").End == DateTimeOffset.FromUnixTimeSeconds(1895184000),
            "危机合约取较早玩法截止，三类均满后自动标记");
        var sends = 0;
        var marked = Store(contract);
        marked.Data.Marks[contract.Key] = store.Data.Marks[contract.Key];
        new EventDeadlineReminder(marked,() => true,_ => { sends++; return true; },() => TimeZoneInfo.Utc).Tick(now);
        Check(sends == 0, "自动已领完停止活动提醒");
        var temp = Path.Combine(Path.GetTempPath(),"GameDailyWidget-progress-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var saved = new EventStore(temp);
            saved.Data.Events.Add(contract);
            EndfieldEventProgress.Apply(saved,reading,state,now);
            saved = new(temp);
            Check(saved.Data.Marks[contract.Key].Automatic,"自动来源可从 events.json 读回");
            saved.Mark(contract,false,false,now);
            saved = new(temp);
            EndfieldEventProgress.Apply(saved,reading,state,now.AddSeconds(1));
            Check(saved.Data.Marks[contract.Key] is { Claimed:false,Automatic:false,ManualOverride:true },
                "手动撤销自动标记后重启不再自动领取");
            var legacy = JsonSerializer.Deserialize<EventMark>("""{"claimed":true,"ignored":true,"at":"2030-01-02T08:00:00+08:00"}""",EventStore.Json);
            Check(legacy is { Claimed:true,Ignored:true,Automatic:false,ManualOverride:false }, "旧版人工标记保持原值");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        void NoMatch(EndfieldProgressReading current, params GameEvent[] events)
        {
            var isolated = Store(events);
            var result = EndfieldEventProgress.Apply(isolated,current,state,now);
            if (result.Count != 0 || isolated.Data.Marks.Count != 0 || isolated.Data.Events.Count != events.Length)
                throw new InvalidOperationException("无效或不唯一的匹配产生了进度");
        }
        NoMatch(reading,Event("其他名称"), contract with { Game="wuwa" });
        NoMatch(reading,contract with { Start=now.AddDays(-5), End=now.AddDays(-1) });
        NoMatch(reading,contract with { Start=now.AddSeconds(1) });
        NoMatch(reading,contract,contract with {Key="other-period",Start=now.AddHours(-1)});
        NoMatch(reading with { Items=[..reading.Items,..reading.Items] },contract);
        NoMatch(reading);
        var dotted = Event("演示丰碑·挑战");
        var source = reading.Items.Single(row => row.Name == "演示丰碑");
        foreach (var dot in "·•・‧･∙⋅")
        {
            var variant = reading with { Items=[source with {Name="演示丰碑" + dot + "挑战"}] };
            Check(EndfieldEventProgress.Apply(Store(dotted),variant,state,now).ContainsKey(dotted.Key),
                $"进度匹配接受 U+{(int)dot:X4} 间隔点");
            NoMatch(variant,Event("演示丰碑·其他"));
            NoMatch(variant,dotted,Event("演示丰碑・挑战"));
        }
        Check(EventParser.Normalize("演示丰碑・挑战") != EventParser.Normalize(dotted.Name),
            "活动键保留原始间隔点，进度匹配仍拒绝歧义或不同名称");
        Check(true,"名称不符、时段不交、未来、双向歧义、鸣潮与无公告均不挂进度");

        foreach (var blocked in new[] {state with { Stale=true },state with { Error="同步失败" },
            state with { Busy=true },state with { LoggedIn=false },state with { SelectedAccountKey="other" },
            state with { Demo=true },state with { UpdatedAt=now.AddSeconds(-1) }})
        {
            var isolated = Store(contract);
            Check(EndfieldEventProgress.Apply(isolated,reading,blocked,now).Count == 0 && isolated.Data.Marks.Count == 0,
                "当前角色和成功读数可信条件 " + checks.Count);
        }
        var staleStore = Store(contract);
        Check(EndfieldEventProgress.Apply(staleStore,reading,state,now.AddMinutes(15)).Count == 0 &&
            EndfieldEventProgress.Apply(staleStore,reading,state,now.AddSeconds(-1)).Count == 0 &&
            staleStore.Data.Marks.Count == 0,"过期和未来读数均不生效");
        var beforeReset = new DateTimeOffset(2030,1,2,3,59,59,EventParser.China);
        Check(!(reading with { FetchedAt=beforeReset }).IsTrusted(state with { UpdatedAt=beforeReset },beforeReset.AddSeconds(1)),
            "04:00 换日前的读数立即失效");
        Check(EndfieldEventProgress.Parse(JsonSerializer.SerializeToElement(detail),"different-role").Count == 0,
            "接口 base.roleId 与请求角色不符时整组弃用");
        var missions = detail["crisisContract"]![0]!;
        missions["stageMission"]!["count"] = 2;
        var partial = Store(contract);
        Check(EndfieldEventProgress.Apply(partial,Read(),state,now)[contract.Key] == "进度 5/6" &&
            partial.Data.Marks.Count == 0, "未满只显示进度");
        missions["stageMission"]!["total"] = 0;
        NoMatch(Read(),contract);
        detail["crisisContract"] = new JsonArray();
        var empty = Read();
        NoMatch(empty,contract);
        Check(empty.Items.Count == 2,"危机合约空数组或无有效总数不产生假完成");
        var group = detail["indieHard"]!["indieHardGroups"]![0]!;
        group["dungeonGroups"]![1]!["hardDungeon"]!["isPass"] = true;
        var full = Store(monument);
        Check(EndfieldEventProgress.Apply(full,Read(),state,now)[monument.Key] == "普通 2/2 · 苦难 2/2" &&
            full.Data.Marks.Count == 0,"影拓丰碑即使全通也只显示进度");
        return checks;
    }
}
