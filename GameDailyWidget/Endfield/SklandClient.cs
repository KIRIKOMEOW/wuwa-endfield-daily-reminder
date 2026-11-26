using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EndfieldDesktopWidget;

// Protocol reference: SciNancy/astrbot_plugin_sklands (MIT), api/login.py,
// api/request.py, schemas/binding.py and schemas/endfield/card.py.
// Credentials stay in memory here; response bodies and tokens are never logged.
public sealed class SklandClient : IDisposable
{
    private const string AppCode = "4ca99fa6b56cc2ba";
    private const string AccountBase = "https://as.hypergryph.com";
    private const string SklandBase = "https://zonai.skland.com";
    private readonly HttpClient _http;

    public SklandClient() : this(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    }) { }

    internal SklandClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Skland/1.32.1 (com.hypergryph.skland; build:103201004; Android 33; ) Okhttp/4.11.0");
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public async Task<QrLoginSession> StartQrLoginAsync(CancellationToken cancellationToken = default)
    {
        using var request = JsonPost(AccountBase + "/general/v1/gen_scan/login", new { appCode = AppCode });
        var response = await SendAsync(request, "生成登录二维码", cancellationToken);
        EnsureSuccess(response, "生成登录二维码");
        var scanId = RequiredText(RequiredObject(response, "data"), "scanId");
        return new QrLoginSession(scanId, "hypergryph://scan_login?scanId=" + Uri.EscapeDataString(scanId));
    }

    public async Task<string?> PollQrLoginAsync(QrLoginSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.ScanId))
            throw new InvalidOperationException("二维码无效，请重新生成。");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            AccountBase + "/general/v1/scan_status?scanId=" + Uri.EscapeDataString(session.ScanId));
        var response = await SendAsync(request, "检查扫码状态", cancellationToken);
        var status = Status(response);
        if (status is 100 or 101) return null; // Awaiting scan / confirmation.
        if (status == 102) throw new InvalidOperationException("二维码已过期，请重新生成。");
        EnsureSuccess(response, "检查扫码状态");

        var scanCode = OptionalText(Property(response, "data"), "scanCode");
        if (string.IsNullOrEmpty(scanCode)) return null;
        using var tokenRequest = JsonPost(AccountBase + "/user/auth/v1/token_by_scan_code", new { scanCode });
        var tokenResponse = await SendAsync(tokenRequest, "完成扫码登录", cancellationToken);
        EnsureSuccess(tokenResponse, "完成扫码登录");
        return RequiredText(RequiredObject(tokenResponse, "data"), "token");
    }

    public async Task<SklandCredential> LoginWithAccountTokenAsync(string accountToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountToken))
            throw new InvalidOperationException("请填写完整的账号 Token，或使用扫码登录。");
        using var grantRequest = JsonPost(AccountBase + "/user/oauth2/v2/grant",
            new { appCode = AppCode, token = accountToken.Trim(), type = 0 });
        var grantResponse = await SendAsync(grantRequest, "账号授权", cancellationToken);
        EnsureSuccess(grantResponse, "账号授权");
        var grantCode = RequiredText(RequiredObject(grantResponse, "data"), "code");

        using var credRequest = JsonPost(SklandBase + "/api/v1/user/auth/generate_cred_by_code",
            new { code = grantCode, kind = 1 });
        var credResponse = await SendAsync(credRequest, "获取森空岛凭证", cancellationToken);
        EnsureSuccess(credResponse, "获取森空岛凭证");
        var data = RequiredObject(credResponse, "data");
        return new SklandCredential(RequiredText(data, "cred"), RequiredText(data, "token"));
    }

    public async Task<IReadOnlyList<EndfieldRole>> GetRolesAsync(SklandCredential credential, CancellationToken cancellationToken = default)
    {
        var activeCredential = await RefreshCredentialAsync(credential, cancellationToken);
        using var userRequest = SignedGet(SklandBase + "/api/v1/user/teenager", activeCredential);
        var userResponse = await SendAsync(userRequest, "读取森空岛账号", cancellationToken);
        EnsureSuccess(userResponse, "读取森空岛账号");
        var userId = RequiredText(RequiredObject(RequiredObject(userResponse, "data"), "teenager"), "userId");

        using var bindingRequest = SignedGet(SklandBase + "/api/v1/game/player/binding", activeCredential);
        var bindingResponse = await SendAsync(bindingRequest, "读取绑定角色", cancellationToken);
        EnsureSuccess(bindingResponse, "读取绑定角色");
        var apps = Property(RequiredObject(bindingResponse, "data"), "list");
        if (apps.ValueKind != JsonValueKind.Array) throw InvalidData();

        var result = new List<EndfieldRole>();
        foreach (var app in apps.EnumerateArray())
        {
            if (!string.Equals(OptionalText(app, "appCode"), "endfield", StringComparison.OrdinalIgnoreCase)) continue;
            var bindings = Property(app, "bindingList");
            if (bindings.ValueKind != JsonValueKind.Array) throw InvalidData();
            foreach (var binding in bindings.EnumerateArray())
            {
                if (Property(binding, "isDelete").ValueKind == JsonValueKind.True) continue;
                var roles = Property(binding, "roles");
                if (roles.ValueKind != JsonValueKind.Array) continue;
                foreach (var role in roles.EnumerateArray())
                {
                    result.Add(new EndfieldRole(RequiredText(role, "roleId"), RequiredText(role, "serverId"), userId,
                        OptionalText(role, "nickname") ?? OptionalText(binding, "nickName") ?? "管理员",
                        OptionalText(role, "serverName") ?? OptionalText(binding, "channelName") ?? "终末地"));
                }
            }
        }
        return result;
    }

    public Task<GameSnapshot> GetSnapshotAsync(SklandCredential credential, EndfieldRole role, CancellationToken cancellationToken = default) =>
        GetSnapshotWithDetailAsync(credential, role, null, cancellationToken);

    internal async Task<GameSnapshot> GetSnapshotWithDetailAsync(SklandCredential credential, EndfieldRole role,
        Action<JsonElement>? observeDetail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(role);
        var activeCredential = await RefreshCredentialAsync(credential, cancellationToken);
        var url = SklandBase + "/web/v1/game/endfield/card/detail?roleId=" + Uri.EscapeDataString(role.RoleId)
            + "&serverId=" + Uri.EscapeDataString(role.ServerId) + "&userId=" + Uri.EscapeDataString(role.UserId);
        using var request = SignedGet(url, activeCredential);
        var response = await SendAsync(request, "读取终末地状态", cancellationToken);
        EnsureSuccess(response, "读取终末地状态");
        var detail = RequiredObject(RequiredObject(response, "data"), "detail");
        var snapshot = ParseSnapshot(detail, role, DateTimeOffset.UtcNow);
        observeDetail?.Invoke(detail);
        return snapshot;
    }

    internal static GameSnapshot ParseSnapshot(JsonElement detail, EndfieldRole role, DateTimeOffset fetchedAt)
    {
        var dungeon = RequiredObject(detail, "dungeon");
        var daily = RequiredObject(detail, "dailyMission");
        var weekly = RequiredObject(detail, "weeklyMission");
        var battlePass = RequiredObject(detail, "bpSystem");
        var maxLevel = OptionalInt(battlePass, "maxLevel");
        DateTimeOffset? fullAt = null;
        if (long.TryParse(OptionalText(dungeon, "maxTs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fullSeconds)
            && fullSeconds > 0 && fullSeconds <= 253402300799)
            fullAt = DateTimeOffset.FromUnixTimeSeconds(fullSeconds);

        // Missing progress fields produce an error, never a fabricated zero/completed state.
        return new GameSnapshot(RequiredInt(dungeon, "curStamina"), RequiredInt(dungeon, "maxStamina"), fullAt,
            RequiredInt(daily, "dailyActivation"), RequiredInt(daily, "maxDailyActivation"),
            RequiredInt(weekly, "score"), RequiredInt(weekly, "total"), RequiredInt(battlePass, "curLevel"),
            maxLevel is > 0 ? maxLevel : null,
            OptionalText(Property(detail, "base"), "name") ?? role.Nickname, fetchedAt);
    }

    private async Task<SklandCredential> RefreshCredentialAsync(SklandCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.Cred) || credential.Cred.Contains('\r') || credential.Cred.Contains('\n'))
            throw new InvalidOperationException("登录凭证无效，请重新扫码登录。");
        using var request = new HttpRequestMessage(HttpMethod.Get, SklandBase + "/api/v1/auth/refresh");
        request.Headers.Add("cred", credential.Cred);
        var response = await SendAsync(request, "刷新登录凭证", cancellationToken);
        EnsureSuccess(response, "刷新登录凭证");
        return credential with { Token = RequiredText(RequiredObject(response, "data"), "token") };
    }

    private static HttpRequestMessage SignedGet(string url, SklandCredential credential)
    {
        var uri = new Uri(url);
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        var timestamp = seconds.ToString(CultureInfo.InvariantCulture);
        var signature = ComputeSignature(url, credential.Token, seconds);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("cred", credential.Cred);
        request.Headers.Add("sign", signature);
        request.Headers.TryAddWithoutValidation("platform", "");
        request.Headers.Add("timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("dId", "");
        request.Headers.TryAddWithoutValidation("vName", "");
        return request;
    }

    internal static string ComputeSignature(string url, string token, long seconds)
    {
        var uri = new Uri(url);
        var timestamp = seconds.ToString(CultureInfo.InvariantCulture);
        // Field order and compact serialization match the protocol reference.
        var headerJson = "{\"platform\":\"\",\"timestamp\":\"" + timestamp + "\",\"dId\":\"\",\"vName\":\"\"}";
        var message = uri.AbsolutePath + uri.Query.TrimStart('?') + timestamp + headerJson;
        var hmac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(message));
        var hexHmac = Convert.ToHexString(hmac).ToLowerInvariant();
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(hexHmac))).ToLowerInvariant();
    }

    private static HttpRequestMessage JsonPost(string url, object payload) => new(HttpMethod.Post, url)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, string action, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("登录凭证无效或访问被拒绝，请重新扫码登录。");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("请求过于频繁，请稍后再试。");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{action}失败（HTTP {(int)response.StatusCode}），请稍后重试或重新登录。");
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(content);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw InvalidData();
            return json.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("连接森空岛超时，请检查网络后重试。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法连接森空岛，请检查网络后重试。");
        }
        catch (JsonException)
        {
            throw InvalidData();
        }
    }

    private static void EnsureSuccess(JsonElement response, string action)
    {
        var status = Status(response);
        if (status == 0) return;
        if (status is 10000 or 10002)
            throw new InvalidOperationException("登录凭证已失效，请重新扫码登录。");
        throw new InvalidOperationException($"{action}失败（接口代码 {status}），请稍后重试或重新登录。");
    }

    private static int Status(JsonElement response) => OptionalInt(response, "status")
        ?? OptionalInt(response, "code") ?? throw InvalidData();

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static JsonElement RequiredObject(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind == JsonValueKind.Object ? property : throw InvalidData();
    }

    private static string? OptionalText(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static string RequiredText(JsonElement value, string name) =>
        OptionalText(value, name) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : throw InvalidData();

    private static int? OptionalInt(JsonElement value, string name) =>
        int.TryParse(OptionalText(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static int RequiredInt(JsonElement value, string name)
    {
        var number = OptionalInt(value, name);
        return number.HasValue && number.Value >= 0 ? number.Value : throw InvalidData();
    }

    private static InvalidOperationException InvalidData() => new("森空岛返回的数据不完整，请稍后重试；接口格式可能已改变。");

    public void Dispose() => _http.Dispose();
}
