using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameDailyWidget;

internal static partial class EventChecks
{
    internal static readonly DateTimeOffset SampleTime = new(2026, 11, 12, 10, 0, 0, EventParser.China);
    internal static EventArticle Article(string game, string id, string title) =>
        new(game, new(id, title, game == "wuwa" ? "https://mc.kurogames.com/main/news/detail/" + id :
            "https://endfield.hypergryph.com/news/" + id), SampleTime, SampleTime);

    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add("PASS " + name);
        }
        string[] Fixture(string id)
        {
            using var stream = typeof(EventChecks).Assembly.GetManifestResourceStream("events." + id + ".txt")!;
            using var reader = new StreamReader(stream);
            return EventParser.Lines(reader.ReadToEnd());
        }
        var wuwa = Article("wuwa", "5779", "「旧梦凋零的百合」3.8版本内容说明");
        var endfield = Article("endfield", "5979", "「穿越甚大蘑境与你相逢」版本更新说明");
        var first = EventParser.Parse(wuwa, Fixture("5779"));
        var second = EventParser.Parse(endfield, Fixture("5979"));
        GameEvent Find(string name) => first.Concat(second).Single(e => e.Name == name);
        Check(first.Count == 7 && Find("梦野寻奇").Deadline == new DateTimeOffset(2026,12,21,4,0,0,EventParser.China), "5779 七个活动、北京时间与 xx:59 归整");
        Check(first.Any(e => e.Name == "终焉矩阵·险境强袭" && e.End?.Day == 22 && e.Key == "wuwa|终焉矩阵第二周期险境强袭|2026-11-12"), "仅给结束时间的周期");
        Check(Find("昔梦赠礼").Start.Hour == 11, "版本更新后优先维护结束");
        Check(EventParser.Parse(wuwa, Fixture("5569")).Count == 7, "旧版样本排除常驻及平台网页活动");
        Check(EventParser.ReadMaintenance(wuwa, Fixture("5723"))?.End.Hour == 11 &&
            EventParser.ReadMaintenance(endfield, Fixture("9324"))?.Start.Hour == 6, "两种维护预告");
        Check(second.Count == 14 && second.Count(e => e.Name == "理智补给") == 2 &&
            second.Single(e => e.Name == "理智补给" && e.Start.Day == 31).End?.Year == 2027, "终末地全部十三个活动，跨年两段独立");
        Check(Find("蜜饼是我的祈愿").EndPending && Find("方兴！竞技大会！").EndPending &&
            Find("蘑菇！蕈类！真菌！").Currency.Amount == 1200 && Find("漫野探奇").Currency.Amount == null,
            "版本期间、维护前、奖励限定本活动");
        var custom = EventParser.Parse(wuwa, EventParser.Lines("""
            [ 样本 ]签到活动
            ✦活动时间：3.8版本期间
            奖励：星声*20、星声×30、星声x4
            [样本]签到活动
            ✦活动时间：3.8版本更新后 ~ 2026年12月3日11:59
            兑换截止：2026年12月5日11:59
            [未知]签到活动
            ✦活动时间：开放时间另见站内信
            [商城上新]活动
            ✦活动时间：版本期间
            [角色唤取]活动
            ✦活动时间：版本期间
            [领取维护补偿]活动
            ✦活动时间：版本期间
            """));
        Check(custom.Count == 3 && custom[0].Currency.Amount == 54 && custom[2].Unknown, "奖励累计、未知保留、分类排除");
        var merged = new List<GameEvent>();
        foreach (var item in custom) EventParser.Merge(merged,item);
        Check(merged.Count == 2 && merged[0].ClaimDeadline?.Day == 5 && merged[0].End?.Day == 3 &&
            !merged[0].EndPending && merged[0].Start == SampleTime, "按名称和开始日去重、公告补时间、独立领奖截止");
        Check(EventParser.Parse(wuwa, ["[卡池]角色唤取", "✦活动时间：版本期间"]).Count == 0 &&
            EventParser.Parse(endfield, ["■ 全新寻访及申领", "1.「样本」特许寻访", "· 活动时间：版本期间"]).Count == 0,
            "排除栏目中的活动时间不会回退成整篇公告活动");
        foreach (var (article,id) in new[] { (wuwa,"5779"),(wuwa,"5569"),(endfield,"5979") })
        {
            var lines = Fixture(id);
            CheckCoverage(id,lines,EventParser.Parse(article,lines));
            checks.Add("PASS " + id + " 逐段独立核对时间、领奖内容与排除项");
        }
        var alternate = EventParser.Parse(wuwa, ["[寻宝周]限时探索玩法","✦开放时间：3.8版本更新后 ~ 2026年12月2日03:59","可领取星声×50"]);
        Check(alternate.Single().Name == "寻宝周" && alternate[0].Currency.Amount == 50,"鸣潮开放时间与奖励识别不限活动标题后缀");
        PoolChecks.Run(Fixture, Check);
        return checks;
    }
}
