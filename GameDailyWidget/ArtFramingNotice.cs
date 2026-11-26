using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GameDailyWidget;

public sealed class ArtFramingNotice
{
    public string? Pending {get;set;}
    public HashSet<string> Handled {get;set;}=new();
    internal bool Observe(string? previous,string current,GameArtSettings art)
    {
        if(previous==current) return false;
        var next=previous!=null && art.Selected==null && !art.Crops.ContainsKey(current) && !Handled.Contains(current) ? current:null;
        if(Pending==next)return false;
        Pending=next; return true;
    }
    internal bool Validate(string? latest,GameArtSettings art,bool official)
    {
        if(Pending==null || Pending==latest && official && art.Selected==null && !art.Crops.ContainsKey(Pending) && !Handled.Contains(Pending))return false;
        Pending=null;return true;
    }
    internal void Handle(string key)
    {
        Handled.Add(key);
        if(Pending==key) Pending=null;
    }
    internal static string Check()
    {
        var settings=JsonSerializer.Deserialize<UnifiedSettings>("{}")!;
        foreach(var game in new[]{"wuwa","endfield"})
        {
            var n=settings.FramingFor(game); var art=settings.ArtFor(game);
            void Must(bool ok){if(!ok)throw new InvalidOperationException("构图提醒状态 "+game);}
            Must(n.Pending==null && !n.Observe(null,"a",art));
            Must(n.Observe("a","b",art) && n.Pending=="b");
            Must(n.Validate("b",art,false) && n.Pending==null);
            n.Observe("b","c",art); n.Handle("c");
            var restored=JsonSerializer.Deserialize<UnifiedSettings>(JsonSerializer.Serialize(settings))!.FramingFor(game);
            Must(!restored.Observe("b","c",art) && restored.Pending==null && restored.Handled.Contains("c"));
            art.Crops["d"]=new(); Must(!n.Observe("c","d",art));
            n.Observe("d","e",art);art.Selected="local"; Must(n.Validate("e",art,true) && !n.Observe("e","f",art));
        }
        return "PASS 构图提醒首次、替换、已处理、删除、手动/本地图、独立游戏与旧设置升级";
    }
}
