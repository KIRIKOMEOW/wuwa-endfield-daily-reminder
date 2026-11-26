using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wuwa.Core;

/// <summary>
/// Queries only the two official CN launcher read endpoints using authorization
/// already present in the current Windows user's cache. No login or renewal occurs.
/// </summary>
public sealed class LauncherClient : IDisposable
{
    private const string PlayerInfoUrl = "https://pc-launcher-sdk-api.kurogame.com/game/queryPlayerInfo";
    private const string RoleUrl = "https://pc-launcher-sdk-api.kurogame.com/game/queryRole";
    private readonly HttpClient _client;
    private readonly string? _cacheRootOverride;
    private readonly SemaphoreSlim _requestSlots = new(2, 2);
    private readonly ConcurrentDictionary<string, string> _expectedUids = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public LauncherClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // Offline tests supply their own local fixture directory and in-memory handler.
    // Production callers use the public constructor and cannot change endpoint URLs.
    internal LauncherClient(HttpClient client, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _client = client;
        _cacheRootOverride = Path.GetFullPath(cacheRoot);
    }

    public async Task<IReadOnlyList<AccountOption>> DiscoverAccountsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
        var options = await Task.WhenAll(entries.Select(entry => DiscoverEntryAsync(entry, cancellationToken)))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Duplicate cache entries may refer to the same player. Offer one valid
        // option per UID; keep failed entries so users can see what needs attention.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<AccountOption>();
        foreach (var option in options)
        {
            if (option.Identity is not null)
            {
                _expectedUids[option.Handle.Key] = option.Identity.Uid;
                if (!seen.Add(option.Identity.Uid)) continue;
            }
            unique.Add(option);
        }
        return unique.AsReadOnly();
    }

    public async Task<RoleSnapshot> QueryAsync(AccountHandle account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        // Reopen the cache each time. This observes an official launcher's updated
        // authorization without saving a separate credential copy in this client.
        var entries = await ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(candidate => candidate.Handle.Key == account.Key);
        if (entry is null)
            throw Error("account_missing", "原账号缓存项已不存在。请重新扫描并选择账号。");
        if (entry.Problem is not null)
            throw Error(entry.ProblemStatus ?? "authorization_missing", entry.Problem);

        await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var profile = await RequestChinaAsync(PlayerInfoUrl,
                new { oauthCode = entry.OauthCode }, cancellationToken).ConfigureAwait(false);
            var identity = ReadIdentity(profile.RootElement);
            if (_expectedUids.TryGetValue(account.Key, out var expectedUid) && identity.Uid != expectedUid)
                throw Error("account_changed", "此缓存项现在对应另一个角色。请重新扫描并选择账号，未继续查询角色数据。");
            _expectedUids[account.Key] = identity.Uid;
            using var role = await RequestChinaAsync(RoleUrl,
                new { oauthCode = entry.OauthCode, playerId = identity.Uid, region = "China" },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ReadSnapshot(role.RootElement, identity);
        }
        finally { _requestSlots.Release(); }
    }

    private async Task<AccountOption> DiscoverEntryAsync(CachedEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Problem is not null)
            return new AccountOption { Handle = entry.Handle, Problem = entry.Problem };

        await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var profile = await RequestChinaAsync(PlayerInfoUrl,
                new { oauthCode = entry.OauthCode }, cancellationToken).ConfigureAwait(false);
            return new AccountOption { Handle = entry.Handle, Identity = ReadIdentity(profile.RootElement) };
        }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException error)
        {
            return new AccountOption { Handle = entry.Handle, Problem = error.Message };
        }
        catch (Exception)
        {
            return new AccountOption { Handle = entry.Handle, Problem = "身份读取失败，未显示原始错误内容。" };
        }
        finally { _requestSlots.Release(); }
    }

    private async Task<List<CachedEntry>> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        string root;
        if (_cacheRootOverride is not null) root = _cacheRootOverride;
        else
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(roaming))
                throw Error("cache_missing", "无法定位当前 Windows 用户的应用数据目录。");
            root = Path.Combine(roaming, "KR_G152");
        }
        if (!Directory.Exists(root))
            throw Error("cache_missing", "未找到国服启动器缓存。请先用当前 Windows 用户登录一次鸣潮官方启动器。");

        string[] directories;
        try { directories = Directory.GetDirectories(root); }
        catch (Exception)
        {
            throw Error("cache_unreadable", "无法读取国服启动器缓存目录。请检查当前用户的读取权限。");
        }
        Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
        var entries = new List<CachedEntry>();
        var cacheNumber = 0;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "KRSDKUserLauncherCache.json");
            if (!File.Exists(path)) continue;
            cacheNumber++;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                if (stream.Length == 0 || stream.Length > 4 * 1024 * 1024)
                {
                    entries.Add(FailedEntry(path, entries.Count + 1, cacheNumber, 1,
                        "cache_corrupt", "缓存为空或大小不符合预期，请用官方启动器更新缓存。"));
                    continue;
                }
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    entries.Add(FailedEntry(path, entries.Count + 1, cacheNumber, 1,
                        "cache_corrupt", "缓存结构不符合预期，请用官方启动器更新缓存。"));
                    continue;
                }
                if (document.RootElement.GetArrayLength() == 0)
                {
                    entries.Add(FailedEntry(path, entries.Count + 1, cacheNumber, 1,
                        "authorization_missing", "缓存没有授权项，请确认官方启动器已登录。"));
                    continue;
                }
                var accountNumber = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    accountNumber++;
                    var encoded = StringValue(item, "oauthCode");
                    if (string.IsNullOrWhiteSpace(encoded))
                    {
                        entries.Add(FailedEntry(path, entries.Count + 1, cacheNumber, accountNumber,
                            "authorization_missing", "此缓存项没有可用授权，请确认官方启动器已登录。"));
                        continue;
                    }
                    var decoded = new string(encoded.Select(character => (char)(character ^ 5)).ToArray());
                    if (string.IsNullOrWhiteSpace(decoded))
                    {
                        entries.Add(FailedEntry(path, entries.Count + 1, cacheNumber, accountNumber,
                            "authorization_missing", "此缓存项没有可用授权，请确认官方启动器已登录。"));
                        continue;
                    }
                    entries.Add(new CachedEntry
                    {
                        Handle = MakeHandle(path, entries.Count + 1, cacheNumber, accountNumber),
                        OauthCode = decoded
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (JsonException)
            {
                entries.Add(FailedEntry(path, entries.Count + 1, cacheNumber, 1,
                    "cache_corrupt", "缓存无法解析，请用官方启动器更新缓存后重新扫描。"));
            }
            catch (Exception)
            {
                entries.Add(FailedEntry(path, entries.Count + 1, cacheNumber, 1,
                    "cache_unreadable", "缓存无法读取，请检查读取权限后重新扫描。"));
            }
        }
        if (cacheNumber == 0)
            throw Error("cache_missing", "未找到国服启动器用户缓存。请先登录一次鸣潮官方启动器。");
        return entries;
    }

    private static CachedEntry FailedEntry(string path, int index, int cacheNumber, int accountNumber,
        string status, string message) => new()
    {
        Handle = MakeHandle(path, index, cacheNumber, accountNumber),
        ProblemStatus = status,
        Problem = message
    };

    private static AccountHandle MakeHandle(string path, int index, int cacheNumber, int accountNumber)
    {
        var selector = $"{Path.GetFullPath(path).ToUpperInvariant()}\n{accountNumber - 1}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selector)));
        return new AccountHandle(key, index, cacheNumber, accountNumber);
    }

    private async Task<JsonDocument> RequestChinaAsync(string url, object payload, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var response = await _client.PostAsJsonAsync(url, payload, requestTimeout.Token)
                    .ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw Error("service_http_error", $"官方查询接口返回 HTTP {(int)response.StatusCode}，未显示响应原文。");
                await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token)
                    .ConfigureAwait(false);
                using var body = await JsonDocument.ParseAsync(stream, cancellationToken: requestTimeout.Token)
                    .ConfigureAwait(false);
                var code = IntValue(body.RootElement, "code");
                if (code is null)
                    throw Error("service_response_invalid", "官方响应缺少有效业务状态码，未显示响应原文。");
                if (code == 1001)
                    throw Error("authorization_expired", "官方启动器授权已失效。请打开官方启动器更新授权后重新扫描，无需登录库街区。");
                if (code == 1005)
                {
                    if (attempt < 5)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw Error("service_busy", "官方接口连续返回等待状态，已达到 5 次尝试上限。稍后可重试。");
                }
                if (code is not (0 or 200))
                    throw Error("service_business_error", $"官方接口返回业务状态码 {code.Value}，未显示响应原文。");
                if (!TryObjectProperty(body.RootElement, "data", out var data) ||
                    !data.TryGetProperty("China", out var china) || china.ValueKind == JsonValueKind.Null)
                    throw Error("china_role_missing", "此授权没有返回国服 China 角色数据，请确认对应国服角色。");
                var text = china.ValueKind switch
                {
                    JsonValueKind.String => china.GetString(),
                    JsonValueKind.Object => china.GetRawText(),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(text))
                    throw Error("china_role_missing", "官方返回的国服角色数据为空。");
                var inner = JsonDocument.Parse(text);
                if (inner.RootElement.ValueKind == JsonValueKind.Object) return inner;
                inner.Dispose();
                throw Error("service_response_invalid", "国服角色数据不是预期对象结构，未显示响应原文。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw Error("request_timeout", "官方查询接口在 15 秒内未完成响应。稍后可重试。");
            }
            catch (HttpRequestException)
            {
                throw Error("network_error", "无法连接官方查询接口，请检查网络后重试。");
            }
            catch (IOException)
            {
                throw Error("network_error", "读取官方响应时连接中断，稍后可重试。");
            }
            catch (JsonException)
            {
                throw Error("service_response_invalid", "官方响应无法按预期 JSON 解析，未显示响应原文。");
            }
            catch (ObjectDisposedException)
            {
                throw Error("client_disposed", "查询客户端已关闭，请重新打开组件。");
            }
        }
        throw Error("service_busy", "官方查询未能在尝试上限内完成。");
    }

    private static PlayerIdentity ReadIdentity(JsonElement profile)
    {
        var uid = IdentifierValue(profile, "roleId");
        if (!ValidChinaUid(uid))
            throw Error("china_role_missing", "国服玩家信息没有有效的数字 UID，未继续查询。");
        var name = StringValue(profile, "roleName");
        if (string.IsNullOrWhiteSpace(name))
            throw Error("fields_missing", "国服玩家信息缺少角色昵称，无法确认账号身份。");
        return new PlayerIdentity(uid!, name, NonNegative(IntValue(profile, "level")));
    }

    private static RoleSnapshot ReadSnapshot(JsonElement role, PlayerIdentity identity)
    {
        if (!TryObjectProperty(role, "Base", out var data))
            throw Error("fields_missing", "官方角色响应缺少 Base 日常数据，不能确认角色和日常状态。");
        var baseUid = IdentifierValue(data, "Id");
        var baseName = StringValue(data, "Name");
        if (baseUid is null || string.IsNullOrWhiteSpace(baseName))
            throw Error("fields_missing", "官方日常数据缺少昵称或 UID，无法进行角色身份校验。");
        if (baseUid != identity.Uid || baseName != identity.Name)
            throw Error("identity_mismatch", "玩家信息与日常数据的昵称或 UID 不一致，已停止显示以避免串号。请稍后重新扫描。");

        var missing = new List<string>();
        var fetchedAtUtc = DateTimeOffset.UtcNow;
        int? ReadCount(JsonElement source, string key, bool positive = false)
        {
            var count = IntValue(source, key);
            if (count is null || (positive ? count <= 0 : count < 0))
            {
                missing.Add(key);
                return null;
            }
            return count;
        }
        DateTimeOffset? ReadTimestamp(string key, int? current, int? maximum)
        {
            var timestamp = LongValue(data, key);
            if (timestamp is null or < 0)
            {
                missing.Add(key);
                return null;
            }
            if (timestamp == 0) return null; // Official sentinel: already recovered/no future recovery.
            try
            {
                var fullAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value);
                if (current.HasValue && maximum.HasValue && current >= maximum) return null;
                if (fullAtUtc <= fetchedAtUtc)
                {
                    // An expired recovery date paired with a non-full or unknown
                    // balance is stale/contradictory. Never guess its current balance.
                    missing.Add(key);
                    return null;
                }
                return fullAtUtc;
            }
            catch (ArgumentOutOfRangeException) { missing.Add(key); return null; }
        }

        var energy = ReadCount(data, "Energy");
        var maxEnergy = ReadCount(data, "MaxEnergy", positive: true);
        var storeEnergy = ReadCount(data, "StoreEnergy");
        var maxStoreEnergy = ReadCount(data, "MaxStoreEnergy", positive: true);
        var liveness = ReadCount(data, "Liveness");
        var unlocked = BoolValue(data, "LivenessUnlock");
        if (unlocked is null) missing.Add("LivenessUnlock");
        var maxLiveness = ReadCount(data, "LivenessMaxCount", positive: unlocked != false);
        var weeklyCount = ReadCount(data, "WeeklyInstCount");
        var energyTime = ReadTimestamp("EnergyRecoverTime", energy, maxEnergy);
        // Both absolute timestamps use Unix milliseconds. Stored-energy units
        // were additionally checked against the official launcher display.
        var storeTime = ReadTimestamp("StoreEnergyRecoverTime", storeEnergy, maxStoreEnergy);
        int? battlePassLevel = null, weekExp = null, weekMaxExp = null;
        if (TryObjectProperty(role, "BattlePass", out var pass))
        {
            battlePassLevel = ReadCount(pass, "Level");
            weekExp = ReadCount(pass, "WeekExp");
            weekMaxExp = ReadCount(pass, "WeekMaxExp");
        }
        else missing.Add("BattlePass");
        var dailyState = unlocked switch
        {
            false => "locked",
            true when liveness.HasValue && maxLiveness is > 0 => liveness >= maxLiveness ? "complete" : "incomplete",
            _ => "unknown"
        };
        return new RoleSnapshot
        {
            Identity = identity with { Level = NonNegative(IntValue(data, "Level")) ?? identity.Level },
            FetchedAtUtc = fetchedAtUtc,
            Energy = energy,
            MaxEnergy = maxEnergy,
            StoreEnergy = storeEnergy,
            MaxStoreEnergy = maxStoreEnergy,
            Liveness = liveness,
            LivenessMaxCount = maxLiveness,
            LivenessUnlock = unlocked,
            WeeklyInstCount = weeklyCount,
            EnergyFullAtUtc = energyTime,
            StoreEnergyFullAtUtc = storeTime,
            BattlePassLevel = battlePassLevel,
            WeekExp = weekExp,
            WeekMaxExp = weekMaxExp,
            DailyState = dailyState,
            MissingFields = missing.AsReadOnly()
        };
    }

    private static bool ValidChinaUid(string? value) => !string.IsNullOrEmpty(value) && value[0] == '1' &&
        value.All(character => character is >= '0' and <= '9');

    private static string? IdentifierValue(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static bool TryObjectProperty(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value) &&
            value.ValueKind == JsonValueKind.Object;
    }

    private static string? StringValue(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntValue(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long? LongValue(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static bool? BoolValue(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ?
            value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : null : null;

    private static int? NonNegative(int? number) => number is >= 0 ? number : null;
    private static LauncherException Error(string status, string safeMessage) => new(status, safeMessage);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw Error("client_disposed", "查询客户端已关闭，请重新打开组件。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        // Do not dispose the semaphore while an in-flight query may release it.
    }

    private sealed class CachedEntry
    {
        public required AccountHandle Handle { get; init; }
        public string? OauthCode { get; init; }
        public string? ProblemStatus { get; init; }
        public string? Problem { get; init; }
    }
}
