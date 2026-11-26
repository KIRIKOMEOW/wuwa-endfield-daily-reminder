using System;
using System.Net;
using System.Text.RegularExpressions;

namespace GameDailyWidget;

internal static class OfficialArtSources
{
    private static readonly Regex ImageTags = new("""<img\b(?:[^"'<>]|"[^"]*"|'[^']*')*>""", RegexOptions.IgnoreCase);
    private static readonly Regex Attributes = new("""(?<key>[^\s=/"'<>]+)(?:\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s"'=<>]+)))?""");

    internal static bool Allowed(string game, string? url)
    {
        var host = game switch { "wuwa" => "media-cdn-mingchao.kurogame.com", "endfield" => "web.hycdn.cn", _ => null };
        return host != null && url != null && !url.Contains('\\') && Uri.TryCreate(url,UriKind.Absolute,out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.IdnHost,host,StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort && uri.UserInfo.Length == 0;
    }

    internal static bool VersionNote(EventArticle article) => Regex.IsMatch(article.Source.Title,
        article.Game == "wuwa" ? @"「[^」]+」\s*\d+(?:\.\d+)+\s*版本内容说明" : @"「[^」]+」\s*版本更新说明");

    internal static string? FirstWuwaImage(string html)
    {
        foreach (Match tag in ImageTags.Matches(html))
            foreach (Match attr in Attributes.Matches(tag.Value))
                if (attr.Groups["key"].Value.Equals("src",StringComparison.OrdinalIgnoreCase))
                {
                    var url = WebUtility.HtmlDecode(attr.Groups["value"].Value).Trim();
                    return Allowed("wuwa",url) ? url : null;
                }
        return null;
    }
}