using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Wuwa.Core;

// Generated fake authorization strings and in-memory HTTP only. No real account access.
var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
var fixtureRoot = Path.Combine(temporaryParent, "WuwaWidgetOffline_" + Guid.NewGuid().ToString("N"));
var cacheDirectory = Path.Combine(fixtureRoot, "fixture-user");
Directory.CreateDirectory(cacheDirectory);
var cacheFile = Path.Combine(cacheDirectory, "KRSDKUserLauncherCache.json");
var checks = new List<string>();
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("Offline check failed: " + label);
    checks.Add(label);
}
async Task WriteCache(params string[] tokens) => await File.WriteAllTextAsync(cacheFile,
    JsonSerializer.Serialize(tokens.Select(token => new { oauthCode = new string(token.Select(c => (char)(c ^ 5)).ToArray()) })));
async Task<string> ErrorStatus(Func<Task> action)
{
    try { await action(); return "no_error"; }
    catch (LauncherException ex) { Check(!ex.Message.Contains("SYNTHETIC_PRIVATE"), "private error body never returned"); return ex.Status; }
}

try
{
    await WriteCache("FAKE_A", "FAKE_DUPLICATE_A", "FAKE_B", "FAKE_EXPIRED");
    var originalCache = await File.ReadAllTextAsync(cacheFile);
    var handler = new FixtureHandler();
    using var client = new LauncherClient(new HttpClient(handler), fixtureRoot);
    var accounts = await client.DiscoverAccountsAsync();
    Check(accounts.Count == 3 && accounts.Count(a => a.Identity is not null) == 2, "duplicate UID entries collapse into two identifiable roles");
    var a = accounts.First(o => o.Identity?.Uid == "100000001");
    Check(a.DisplayName.Contains("测试漂泊者甲") && a.DisplayName.Contains("100000001"), "account list includes nickname and full UID");
    Check(accounts.Any(o => o.Identity is null && o.Problem?.Contains("授权已失效") == true), "expired cache does not hide working roles");
    Check(handler.PeakConcurrent <= 2, "account discovery concurrency bounded to two");
    var snapshot = await client.QueryAsync(a.Handle);
    Check(snapshot.Identity.Name == "测试漂泊者甲" && snapshot.Identity.Uid == "100000001", "selected role identity verified in snapshot");
    Check(snapshot.DailyState == "incomplete" && snapshot.Energy == 240 && snapshot.MaxEnergy == 240, "observed daily and waveplate shape");
    Check(snapshot.BattlePassLevel == 25 && snapshot.WeekExp == 8800 && snapshot.WeekMaxExp == 12000, "radio level and weekly experience fields");
    Check(snapshot.StoreEnergyFullAtUtc > DateTimeOffset.UtcNow.AddHours(92) && snapshot.MissingFields.Count == 0, "verified stored-waveplate Unix millisecond timestamp");
    Check(originalCache == await File.ReadAllTextAsync(cacheFile), "all queries leave authorization cache unchanged");

    handler.MissingDaily = true;
    var missing = await client.QueryAsync(a.Handle);
    Check(missing.DailyState == "unknown" && missing.Liveness is null && missing.MissingFields.Contains("Liveness"), "missing daily data never looks complete");
    handler.MissingDaily = false;
    handler.CompletedDaily = true;
    Check((await client.QueryAsync(a.Handle)).DailyState == "complete", "100 of 100 is completed");
    handler.CompletedDaily = false;
    handler.MismatchIdentity = true;
    Check(await ErrorStatus(async () => { await client.QueryAsync(a.Handle); }) == "identity_mismatch", "mismatched role body rejected");
    handler.MismatchIdentity = false;

    var roleRequestsBeforeChange = handler.RoleRequests;
    await WriteCache("FAKE_B", "FAKE_DUPLICATE_A", "FAKE_B", "FAKE_EXPIRED");
    Check(await ErrorStatus(async () => { await client.QueryAsync(a.Handle); }) == "account_changed" &&
        handler.RoleRequests == roleRequestsBeforeChange, "cache reorder blocked before wrong role query");
    await WriteCache("FAKE_A");
    handler.DelayUntilCancelled = true;
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60)))
    {
        var cancelled = false;
        try { await client.QueryAsync(a.Handle, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "in-flight account request cancels without fabricated result");
    }
    handler.DelayUntilCancelled = false;

    using var alreadyCancelled = new CancellationTokenSource();
    alreadyCancelled.Cancel();
    var beforeCancel = handler.Requests;
    var immediateCancellation = false;
    try { await client.DiscoverAccountsAsync(alreadyCancelled.Token); }
    catch (OperationCanceledException) { immediateCancellation = true; }
    Check(immediateCancellation && handler.Requests == beforeCancel, "cancelled discovery makes zero requests");

    foreach (var scenario in new[] { "retry", "busy", "redirect", "malformed", "foreign" })
    {
        var transport = new FixtureHandler { Scenario = scenario };
        using var testClient = new LauncherClient(new HttpClient(transport), fixtureRoot);
        var options = await testClient.DiscoverAccountsAsync();
        Check(scenario switch
        {
            "retry" => transport.Requests == 2 && options[0].Identity is not null,
            "busy" => transport.Requests == 5 && options[0].Identity is null,
            _ => transport.Requests == 1 && options[0].Identity is null
        }, "safe service handling: " + scenario);
        Check(options.All(o => o.Problem is null || !o.Problem.Contains("SYNTHETIC_PRIVATE")), "private response excluded: " + scenario);
    }
    var consoleProgram = Assembly.Load("WuwaLauncherProbe").GetType("WuwaLauncherProbe.Program", true)!;
    var reportValidator = consoleProgram.GetMethod("ValidateReportPath", BindingFlags.Static | BindingFlags.NonPublic)!;
    var overwriteBlocked = false;
    try { reportValidator.Invoke(null, [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KR_G152", "fake-report.json")]); }
    catch (TargetInvocationException ex) when (ex.InnerException is LauncherException le) { overwriteBlocked = le.Status == "report_path_not_allowed"; }
    Check(overwriteBlocked, "report cannot overwrite launcher cache");
    Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", count = checks.Count, checks,
        realLauncherCacheRead = false, realNetworkRequests = 0 }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    // Only remove the uniquely generated fixture directory under the checked temp parent.
    var resolved = Path.GetFullPath(fixtureRoot);
    if (Path.GetDirectoryName(resolved) == temporaryParent &&
        Path.GetFileName(resolved).StartsWith("WuwaWidgetOffline_", StringComparison.Ordinal) && Directory.Exists(resolved))
        Directory.Delete(resolved, recursive: true);
}

sealed class FixtureHandler : HttpMessageHandler
{
    public string? Scenario { get; init; }
    public bool MissingDaily { get; set; }
    public bool CompletedDaily { get; set; }
    public bool MismatchIdentity { get; set; }
    public bool DelayUntilCancelled { get; set; }
    public int Requests => _requests;
    public int RoleRequests => _roleRequests;
    public int PeakConcurrent => _peakConcurrent;
    private int _requests, _roleRequests, _concurrent, _peakConcurrent;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.Host != "pc-launcher-sdk-api.kurogame.com" ||
            request.RequestUri.AbsolutePath is not ("/game/queryPlayerInfo" or "/game/queryRole"))
            throw new Exception("Unexpected synthetic endpoint.");
        var sequence = Interlocked.Increment(ref _requests);
        var concurrent = Interlocked.Increment(ref _concurrent);
        Interlocked.Exchange(ref _peakConcurrent, Math.Max(_peakConcurrent, concurrent));
        try
        {
            await Task.Delay(DelayUntilCancelled ? 20000 : 15, token);
            if (Scenario == "redirect") return Reply("SYNTHETIC_PRIVATE", HttpStatusCode.Redirect);
            if (Scenario == "malformed") return Reply("SYNTHETIC_PRIVATE");
            if (Scenario == "busy" || Scenario == "retry" && sequence == 1) return Reply("{\"code\":1005}");
            if (Scenario == "foreign") return Reply("{\"code\":0,\"data\":{\"America\":\"{}\"}}");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var auth = body.RootElement.GetProperty("oauthCode").GetString();
            if (auth is null || !auth.StartsWith("FAKE_")) throw new Exception("Non-synthetic credential rejected by fixture.");
            if (auth == "FAKE_EXPIRED") return Reply("{\"code\":1001,\"msg\":\"SYNTHETIC_PRIVATE\"}");
            var uid = auth == "FAKE_B" ? "100000002" : "100000001";
            var name = auth == "FAKE_B" ? "测试漂泊者乙" : "测试漂泊者甲";
            object inner;
            if (request.RequestUri.AbsolutePath == "/game/queryPlayerInfo") inner = new { roleId = uid, roleName = name, level = 80 };
            else
            {
                Interlocked.Increment(ref _roleRequests);
                if (body.RootElement.GetProperty("region").GetString() != "China" ||
                    body.RootElement.GetProperty("playerId").GetString() != uid) throw new Exception("Wrong synthetic role selection.");
                var data = new Dictionary<string, object?>
                {
                    ["Id"] = MismatchIdentity ? "100000999" : uid, ["Name"] = name, ["Level"] = 80,
                    ["Energy"] = 240, ["MaxEnergy"] = 240, ["EnergyRecoverTime"] = 0,
                    ["StoreEnergy"] = 15, ["MaxStoreEnergy"] = 480,
                    ["StoreEnergyRecoverTime"] = DateTimeOffset.UtcNow.AddHours(93).ToUnixTimeMilliseconds(),
                    ["LivenessMaxCount"] = 100, ["LivenessUnlock"] = true, ["WeeklyInstCount"] = 0
                };
                if (!MissingDaily) data["Liveness"] = CompletedDaily ? 100 : 0;
                inner = new { Base = data, BattlePass = new { Level = 25, WeekExp = 8800, WeekMaxExp = 12000 } };
            }
            return Reply(JsonSerializer.Serialize(new { code = 0, data = new { China = JsonSerializer.Serialize(inner) } }));
        }
        finally { Interlocked.Decrement(ref _concurrent); }
    }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
