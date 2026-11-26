using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace GameDailyWidget;

public sealed record GuideAuthor(string Name, string Mid, string Game)
{
    internal static List<GuideAuthor> Defaults() => [new("叫我棉被","6014992","wuwa"), new("YZ-月咒","44286479","endfield")];

    internal static bool TryCreate(string name,string input,string game,out GuideAuthor? author)
    {
        author = null;
        name = name.Trim();
        var mid = input.Trim();
        if (Uri.TryCreate(mid,UriKind.Absolute,out var uri))
        {
            if (!GuideLinks.Space(uri) || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
            var path = uri.AbsolutePath;
            if (path.StartsWith("//",StringComparison.Ordinal) || path.EndsWith("//",StringComparison.Ordinal)) return false;
            mid = path[1..].TrimEnd('/');
        }
        if (name.Length == 0 || name.Any(char.IsControl) || game is not ("wuwa" or "endfield" or "both") ||
            mid.Length == 0 || !mid.All(char.IsAsciiDigit)) return false;
        author = new(name,mid,game);
        return true;
    }
}

internal sealed record GuideLink(string Label, Uri Target);

internal static class GuideLinks
{
    private static bool Host(Uri u,string name) => u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps &&
        u.IsDefaultPort && u.UserInfo.Length == 0 && u.IdnHost == name;
    internal static bool Space(Uri u) => Host(u,"space.bilibili.com");
    internal static IEnumerable<GuideLink> For(IEnumerable<GuideAuthor> authors,string game,string name)
    {
        foreach (var author in authors.Where(a => a.Game == game || a.Game == "both"))
        {
            if (!GuideAuthor.TryCreate(author.Name,author.Mid,author.Game,out var valid)) continue;
            var home = "https://space.bilibili.com/" + valid!.Mid;
            yield return new(author.Name + "：搜「" + name + "」",new(home + "/search?keyword=" + Uri.EscapeDataString(name)));
            yield return new(author.Name + "：空间主页",new(home));
        }
        foreach (var suffix in new[]{"一图流","配队"})
        {
            var term = name + " " + suffix;
            yield return new("B 站搜「" + term + "」",new("https://search.bilibili.com/all?keyword=" + Uri.EscapeDataString(term)));
        }
    }

    internal static void Open(Uri link, Action<Uri> launch)
    {
        if (!Space(link) && !Host(link,"search.bilibili.com")) throw new ArgumentException("攻略地址不受支持");
        launch(link);
    }

    internal static IEnumerable<string> Check()
    {
        var legacy = JsonSerializer.Deserialize<UnifiedSettings>("{}")!;
        if (legacy.GuideAuthors.Count != 2 || !legacy.CompletionArtEnabled ||
            JsonSerializer.Deserialize<UnifiedSettings>("""{"GuideAuthors":[]}""")!.GuideAuthors.Count != 0)
            throw new InvalidOperationException("攻略设置迁移异常");
        if (!GuideAuthor.TryCreate(" 编者 ","https://space.bilibili.com/123/","both",out var author) || author != new GuideAuthor("编者","123","both"))
            throw new InvalidOperationException("空间链接或适用游戏识别异常");
        foreach (var invalid in new[]{"","１２３","-1","https://evil.invalid/123","http://space.bilibili.com/123","https://space.bilibili.com/12/video","https://name@space.bilibili.com/12"})
            if (GuideAuthor.TryCreate("编者",invalid,"both",out _)) throw new InvalidOperationException("接受了无效 mid");
        const string text = "莉 莉·演示&?#";
        var links = For(legacy.GuideAuthors,"wuwa",text).ToArray();
        if (links.Length != 4 || links[0].Target.AbsoluteUri != "https://space.bilibili.com/6014992/search?keyword="+Uri.EscapeDataString(text)
            || links[3].Target.Query != "?keyword="+Uri.EscapeDataString(text+" 配队"))
            throw new InvalidOperationException("攻略链接编码或游戏筛选错误");
        var opened = new List<Uri>();
        foreach (var link in links) Open(link.Target,opened.Add);
        foreach(var bad in new[]{"https://space.bilibili.com.invalid/1","http://search.bilibili.com/all","https://space.bilibili.com:444/1"})
        {
            try { Open(new(bad),opened.Add); } catch(ArgumentException) { continue; }
            throw new InvalidOperationException("攻略跳转接受了非允许地址");
        }
        if (!opened.SequenceEqual(links.Select(x => x.Target))) throw new InvalidOperationException("攻略跳转未送达浏览器");
        return ["PASS 博主默认、旧设置迁移与删空保留","PASS 数字 mid、空间主页与非法输入",
            "PASS 名称编码、适用游戏、两个 HTTPS 主机与仅调用浏览器启动器"];
    }
}
