using System;
using System.Collections.Generic;
using System.Linq;

namespace GameDailyWidget;

internal static class PoolChecks
{
    internal static void Run(Func<string,string[]> fixture, Action<bool,string> check)
    {
        var wuwa = EventChecks.Article("wuwa","5779","「旧梦凋零的百合」3.8版本内容说明");
        var endfield = EventChecks.Article("endfield","5979","「穿越甚大蘑境与你相逢」版本更新说明");
        var demo = new EventStore();
        void Read(EventArticle article, string[] lines)
        {
            demo.Data.Articles[article.Game + ":" + article.Source.Id] =
                new(article.Published,article.StartTime,EventParser.Revision,article.Source.Title,article.Published,
                    ParsedPools:PoolParser.Parse(article,lines));
        }
        var oldWuwa = EventChecks.Article("wuwa","5569","「镜锁妄世，心照红尘」3.7版本内容说明");
        Read(oldWuwa,fixture("pool-5569"));
        Read(wuwa,fixture("pool-5779"));
        var maintenance = EventParser.ReadMaintenance(wuwa, fixture("5723"))!;
        demo.Data.Maintenance["wuwa"] = [maintenance with { Version="3.7", End=maintenance.End.AddDays(-43) }, maintenance];
        PoolCatalog.Rebuild(demo.Data,"wuwa",demo.Data.Maintenance["wuwa"]);
        var current = PoolCatalog.Current(demo.Data,"wuwa",maintenance.End).ToArray();
        check(current.Length == 2 && current.All(p => p.Start == null && p.End == null && p.TimePending) &&
            current.Any(p => p.Name == "盛放于昨日梦土的花朵" && p.Items.Single() == new PoolItem("莉莉",5,"角色")) &&
            current.Any(p => p.Name == "安魂" && p.Items.Single() == new PoolItem("安魂",5,"佩枪")) &&
            demo.Data.Pools.Count(p => p.Version == "3.7") == 4, "5779 两个未定档卡池；5569 四个历史池不再占据当期面板");
        var schedule = EventChecks.Article("wuwa","991","「盛放于昨日梦土的花朵」角色活动唤取");
        Read(schedule,["唤取时间：3.8版本更新后 ~ 2026年12月2日11:59"]);
        PoolCatalog.Rebuild(demo.Data,"wuwa",demo.Data.Maintenance["wuwa"]);
        var filled = demo.Data.Pools.Single(p => p.Name == "盛放于昨日梦土的花朵");
        check(filled.Start == maintenance.End && filled.End == new DateTimeOffset(2026,12,2,12,0,0,EventParser.China) &&
            !filled.TimePending && filled.Items.Single().Name == "莉莉", "专门唤取公告补全时间；版本更新后取维护结束，保留 UP 与原键");
        var endOnly = PoolParser.Parse(schedule, ["唤取结束时间：2026年12月2日11:59"]).Single();
        check(endOnly.Start == null && endOnly.End == filled.End && endOnly.TimePending,
            "后续仅公布截止时间的公告不会把结束误作开始");
        var reprise = EventChecks.Article("wuwa","992","「重逢」角色复刻唤取");
        var recurring = PoolParser.Parse(reprise,["唤取时间：3.8版本更新后",
            "5星共鸣者「演示甲」、4星共鸣者「演示乙」获取概率提升。"]);
        check(recurring.Single().Items.Select(i => (i.Name,i.Stars)).SequenceEqual([("演示甲",5),("演示乙",4)]) &&
            recurring[0].StartsAfterMaintenance && recurring[0].TimePending, "鸣潮复刻、多个 UP、仅公布版本更新后");
        Read(endfield,fixture("pool-5979"));
        var special = EventChecks.Article("endfield","8535","「锻砧、行囊与蜜糖」特许寻访说明");
        var both = EventChecks.Article("endfield","7201","「致每一个飘游之愿」重构寻访1与「飘行申领」重构申领1开放");
        Read(special,fixture("pool-8535"));
        Read(both,fixture("pool-7201"));
        var window = EventParser.ReadMaintenance(endfield,fixture("9324"))!;
        PoolCatalog.Rebuild(demo.Data,"endfield",[window]);
        var pools = demo.Data.Pools.Where(p => p.Game == "endfield").ToList();
        check(pools.Count == 5 && pools.Single(p => p.Name == "锻砧、行囊与蜜糖") is { Start: {} start, End: {} end } hero &&
            start == window.End && end.Day == 23 && end.Hour == 12 && hero.Items.Single() == new PoolItem("小刻",6,"干员") &&
            hero.Source.Id == "8535", "5979 与 8535 合并限时特许寻访；不把可能出现的其他干员列为 UP");
        check(pools.Single(p => p.Name == "飘行申领") is { Start: {} begin, End: {} close } weapon &&
            begin == window.End && close.Day == 16 && weapon.Items.Single() == new PoolItem("使命必达",6,"施术单元") &&
            weapon.Source.Id == "7201" && pools.Single(p => p.Name == "绯红瑰宝") is { End:null, TimePending:true } &&
            pools.Single(p => p.Name == "赤獠申领").Items.Single() == new PoolItem("狼之绯",6,"单手剑"),
            "7201 同时解析重构寻访与重构武器申领，未公布的后半维护时间留空");
        var five = PoolParser.Parse(special,["▼//「锻砧、行囊与蜜糖」特许寻访说明",
            "· 开放时间：版本更新后","6星干员【演示甲】获取概率提升。","概率提升的5星干员为【演示乙】、【演示丙】。"]);
        check(five.Single().Items.Select(i => i.Stars).SequenceEqual([6,5,5]), "只收录明确概率提升的六星、五星，可有多个 UP");
        var sale = EventChecks.Article("endfield","6083","「恣游申领」限时特卖说明");
        check(PoolParser.Parse(sale,fixture("pool-6083")).Count == 0 && !pools.Any(p => p.Name == "恣游申领") &&
            new[] {"常驻", "新手", "商城", "礼包"}.All(word => PoolParser.Parse(reprise with {
                Source=reprise.Source with { Title=word + reprise.Source.Title } }, ["5星共鸣者「演示」获取概率提升。"]).Count == 0),
            "排除限时特卖、普通商店申领、常驻、新手、商城和礼包");
        var sent = 0;
        new EventDeadlineReminder(demo,() => true,_ => { sent++; return true; },() => TimeZoneInfo.Utc)
            .Tick(filled.End!.Value.AddMinutes(-1));
        check(sent == 0 && demo.Data.Sent.Count == 0, "卡池即将截止也不发送通知或写入投递记录");
    }
}
