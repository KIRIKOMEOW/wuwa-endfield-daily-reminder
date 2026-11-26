using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EndfieldDesktopWidget;

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAILED: " + description);
    passed++;
    Console.WriteLine("PASS " + description);
}
string fixture = """
{"base":{"name":"测试管理员"},"dungeon":{"curStamina":"206","maxStamina":"360","maxTs":"1700066480"},"dailyMission":{"dailyActivation":0,"maxDailyActivation":100},"weeklyMission":{"score":0,"total":10},"bpSystem":{"curLevel":60,"maxLevel":60}}
""";
using var parsed = JsonDocument.Parse(fixture);
var role = new EndfieldRole("role-123", "1", "user-456", "备用昵称", "官服");
var snapshot = SklandClient.ParseSnapshot(parsed.RootElement, role, DateTimeOffset.FromUnixTimeSeconds(1700000000));
Check(snapshot.Stamina == 206 && snapshot.MaxStamina == 360, "string stamina values map correctly");
Check(snapshot.DailyActivation == 0 && snapshot.MaxDailyActivation == 100 && snapshot.WeeklyScore == 0 && snapshot.WeeklyTotal == 10, "daily and weekly fields remain separate");
Check(snapshot.StaminaFullAt!.Value - snapshot.FetchedAt == TimeSpan.FromMinutes(18 * 60 + 28), "Unix timestamps yield 18h28m without timezone shifts");
Check(snapshot.BattlePassLevel == 60 && snapshot.MaxBattlePassLevel == 60, "battle pass maximum comes from server");
using var unknownBp = JsonDocument.Parse(fixture.Replace(",\"maxLevel\":60", ""));
Check(SklandClient.ParseSnapshot(unknownBp.RootElement, role, snapshot.FetchedAt).MaxBattlePassLevel == null, "missing battle pass limit stays unknown");
try
{
    using var missing = JsonDocument.Parse(fixture.Replace("\"score\":0,", ""));
    SklandClient.ParseSnapshot(missing.RootElement, role, snapshot.FetchedAt);
    throw new Exception("Missing weekly data was accepted.");
}
catch (InvalidOperationException) { Check(true, "missing weekly data is rejected instead of fabricated"); }
Check(SklandClient.ComputeSignature("https://zonai.skland.com/web/v1/game/endfield/card/detail?roleId=role-123&serverId=1&userId=user-456", "test-signing-token", 1700000000) == "83fa8905f4ad8dfaac40b3a6da2ae676", "signature matches independently generated Python HMAC/MD5 vector");
Check(AccountTokenInput.Parse("  placeholder-token  ") == "placeholder-token" && AccountTokenInput.Parse("{\"data\":{\"content\":\"placeholder-token\"}}") == "placeholder-token", "manual login accepts a token or official account-info JSON");
Check(AccountTokenInput.Parse("{\"data\":0}") == "" && AccountTokenInput.Parse("{\"data\":{\"content\":123}}") == "" && AccountTokenInput.Parse("{invalid") == "", "malformed account-info input cannot crash the login window");
Check(AccountTokenInput.Parse("placeholder\nheader") == "", "control characters in credentials are rejected");

using (var handler = new FakeHandler())
using (var client = new SklandClient(handler))
{
    handler.Add("/general/v1/gen_scan/login", """{"status":0,"data":{"scanId":"placeholder-scan"}}""");
    var qr = await client.StartQrLoginAsync();
    Check(qr.QrContent == "hypergryph://scan_login?scanId=placeholder-scan", "QR content uses official app scheme");
    handler.Add("/general/v1/scan_status", """{"status":100}""");
    Check(await client.PollQrLoginAsync(qr) == null, "not scanned is a pending state");
    handler.Add("/general/v1/scan_status", """{"status":101}""");
    Check(await client.PollQrLoginAsync(qr) == null, "awaiting phone confirmation is a pending state");
    handler.Add("/general/v1/scan_status", """{"status":0,"data":{"scanCode":"placeholder-code"}}""");
    handler.Add("/user/auth/v1/token_by_scan_code", """{"status":0,"data":{"token":"placeholder-account-token"}}""");
    Check(await client.PollQrLoginAsync(qr) == "placeholder-account-token", "confirmed scan exchanges code for account token");
    handler.Add("/general/v1/scan_status", """{"status":102}""");
    try { await client.PollQrLoginAsync(qr); throw new Exception("Expired QR accepted."); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("过期"), "expired QR has a readable error"); }

    handler.Add("/user/oauth2/v2/grant", """{"status":0,"data":{"code":"placeholder-grant"}}""");
    handler.Add("/api/v1/user/auth/generate_cred_by_code", """{"code":0,"data":{"cred":"placeholder-cred","token":"placeholder-signing"}}""");
    var credential = await client.LoginWithAccountTokenAsync("placeholder-account-token");
    Check(credential.Cred == "placeholder-cred" && credential.Token == "placeholder-signing", "login distinguishes account token, cred, and signing token");
    handler.Add("/api/v1/auth/refresh", """{"code":0,"data":{"token":"refreshed-signing"}}""");
    handler.Add("/api/v1/user/teenager", """{"code":0,"data":{"teenager":{"userId":"skland-user-id"}}}""");
    handler.Add("/api/v1/game/player/binding", """{"code":0,"data":{"list":[{"appCode":"arknights","bindingList":[]},{"appCode":"endfield","bindingList":[{"nickName":"绑定昵称","channelName":"官服","roles":[{"roleId":"game-role-id","serverId":"server-2","nickname":"角色昵称"}]}]}]}}""");
    var roles = await client.GetRolesAsync(credential);
    Check(roles.Count == 1 && roles[0].UserId == "skland-user-id" && roles[0].RoleId == "game-role-id" && roles[0].ServerId == "server-2", "Skland account user ID and game role/server IDs are not confused");
    handler.Add("/api/v1/auth/refresh", """{"code":0,"data":{"token":"refreshed-signing"}}""");
    handler.Add("/web/v1/game/endfield/card/detail", "{\"code\":0,\"data\":{\"detail\":" + fixture + "}}");
    var fetched = await client.GetSnapshotAsync(credential, roles[0]);
    Check(fetched.Stamina == 206 && handler.LastUri!.Query == "?roleId=game-role-id&serverId=server-2&userId=skland-user-id", "card request uses the selected role and data.detail");
    handler.Add("/api/v1/auth/refresh", """{"message":"PRIVATE_BODY_MUST_NOT_SURFACE"}""", HttpStatusCode.Unauthorized);
    try { await client.GetSnapshotAsync(credential, role); throw new Exception("Unauthorized request accepted."); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("重新") && !ex.Message.Contains("PRIVATE_BODY"), "authentication errors do not reveal response bodies"); }
}
Console.WriteLine($"All {passed} checks passed.");

sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(string Path, string Json, HttpStatusCode Status)> _responses = new();
    public Uri? LastUri { get; private set; }
    public void Add(string path, string json, HttpStatusCode status = HttpStatusCode.OK) => _responses.Enqueue((path, json, status));
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri;
        var response = _responses.Dequeue();
        if (LastUri!.AbsolutePath != response.Path) throw new Exception("Unexpected request path: " + LastUri.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(response.Status) { Content = new StringContent(response.Json, Encoding.UTF8, "application/json") });
    }
}
