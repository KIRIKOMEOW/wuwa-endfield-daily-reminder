using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameDailyWidget;

/// <summary>Pure in-memory regression checks; no credentials, network or personal history.</summary>
public static class DailyStreakChecks
{
    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var results = new List<string>();
        Check("连续记录首日达标与同日多次刷新幂等", () =>
        {
            var f = new Fixture();
            var state = f.State();
            f.Tracker.Observe(state);
            f.Tracker.Observe(state);
            f.Now = f.Now.AddMinutes(5);
            f.Tracker.Observe(f.State());
            Assert(f.Store.Reads == 1 && f.Store.Writes == 1 && f.Description().Contains("连续达标 1 天", StringComparison.Ordinal), "同日被重复计算或写入");
            Assert(DayCount(f.Store.Json!) == 1, "同一天出现多个记录");
        });
        Check("当日未达标后可升级为达标且不可降级", () =>
        {
            var f = new Fixture();
            f.Tracker.Observe(f.State(complete: false));
            Assert(f.Description().Contains("0 天", StringComparison.Ordinal) && f.Description().Contains("今日尚未达标", StringComparison.Ordinal), "首次未达标描述不准确");
            f.Now = f.Now.AddMinutes(5);
            f.Tracker.Observe(f.State());
            f.Now = f.Now.AddMinutes(5);
            f.Tracker.Observe(f.State(complete: false));
            Assert(f.Store.Writes == 2 && f.Description().Contains("1 天", StringComparison.Ordinal) && !f.Description().Contains("今日尚未达标", StringComparison.Ordinal), "达标被后续低值覆盖");
        });
        Check("连续跨日累积且今日未达标保留截至昨日连续数", () =>
        {
            var f = new Fixture();
            f.Tracker.Observe(f.State());
            f.Now = f.Now.AddDays(1);
            f.Tracker.Observe(f.State());
            Assert(f.Description().Contains("连续达标 2 天", StringComparison.Ordinal), "相邻游戏日没有连续计算");
            f.Now = f.Now.AddDays(1);
            f.Tracker.Observe(f.State(complete: false));
            Assert(f.Description().Contains("2 天（截至昨日）", StringComparison.Ordinal) && f.Description().Contains("今日尚未达标", StringComparison.Ordinal), "当日未达标过早清零连续数");
            f.Now = f.Now.AddMinutes(5);
            f.Tracker.Observe(f.State());
            Assert(f.Description().Contains("连续达标 3 天", StringComparison.Ordinal), "今日后来完成不能续接");
        });
        Check("未观察日期保持空白并使记录中断", () =>
        {
            var f = new Fixture();
            f.Tracker.Observe(f.State());
            f.Now = f.Now.AddDays(2);
            Assert(f.Description().Contains("记录中断", StringComparison.Ordinal) && f.Description().Contains("今日待确认", StringComparison.Ordinal), "空白日期被当成完成或无缺口");
            f.Tracker.Observe(f.State());
            Assert(f.Description().Contains("连续达标 1 天", StringComparison.Ordinal) && f.Description().Contains("中断", StringComparison.Ordinal), "缺口之后假连续");
            Assert(DayCount(f.Store.Json!) == 2, "系统补造了未观察日期");
        });
        Check("仅昨日达标而今日尚未观察时保留昨天连续数", () =>
        {
            var f = new Fixture();
            f.Tracker.Observe(f.State());
            f.Now = f.Now.AddDays(1);
            Assert(f.Description().Contains("1 天（截至昨日）", StringComparison.Ordinal) && f.Description().Contains("今日待确认", StringComparison.Ordinal), "未观察今日被标成未完成");
            Assert(f.Store.Writes == 1, "描述待确认时补写了新的一天");
        });
        Check("鸣潮按 UID 持续记录且授权缓存变化不丢失历史", () =>
        {
            var f = new Fixture();
            f.Tracker.Observe(f.State(accountKey: "first-authorization-cache"));
            f.Now = f.Now.AddDays(1);
            var rotated = f.State(accountKey: "rotated-authorization-cache");
            f.Tracker.Observe(rotated);
            Assert(f.Tracker.Describe(rotated).Contains("2 天", StringComparison.Ordinal), "授权缓存轮换导致分裂历史");
            var other = f.State(uid: "100000202", accountKey: "rotated-authorization-cache");
            Assert(f.Tracker.Describe(other).Contains("待记录", StringComparison.Ordinal), "不同 UID 串号");
            f.Tracker.Observe(other);
            Assert(f.Tracker.Describe(other).Contains("1 天", StringComparison.Ordinal) && f.Tracker.Describe(rotated).Contains("2 天", StringComparison.Ordinal), "切账号覆盖各自记录");
        });
        Check("终末地按完整角色键隔离服务器与游戏", () =>
        {
            var f = new Fixture();
            var a = f.State(game: "endfield", uid: "ROLE-1", accountKey: "server-a-role-1");
            var b = f.State(game: "endfield", uid: "ROLE-1", accountKey: "server-b-role-1");
            f.Tracker.Observe(a);
            f.Now = f.Now.AddDays(1);
            a = a with { UpdatedAt = f.Now };
            b = b with { UpdatedAt = f.Now };
            f.Tracker.Observe(a);
            f.Tracker.Observe(b);
            Assert(f.Tracker.Describe(a).Contains("2 天", StringComparison.Ordinal) && f.Tracker.Describe(b).Contains("1 天", StringComparison.Ordinal), "同角色ID不同服务器合并");
            Assert(f.Description().Contains("待记录", StringComparison.Ordinal), "终末地污染鸣潮连续记录");
        });
        Check("国服04点日界线独立于系统时区", () =>
        {
            var f = new Fixture { Now = DateTimeOffset.Parse("2026-10-05T03:59:59+08:00") };
            f.Tracker.Observe(f.State());
            f.Now = DateTimeOffset.Parse("2026-10-04T13:00:00-07:00"); // Same instant as CN 04:00.
            f.Tracker.Observe(f.State());
            Assert(f.Description().Contains("2 天", StringComparison.Ordinal) && DayCount(f.Store.Json!) == 2, "错误使用本地午夜或夏令时");
            var endfield = new Fixture { Now = DateTimeOffset.Parse("2026-10-05T03:59:59+08:00") };
            endfield.Tracker.Observe(endfield.State(game: "endfield", uid: "ROLE-1", accountKey: "ef-role"));
            endfield.Now = endfield.Now.AddSeconds(1);
            var efToday = endfield.State(game: "endfield", uid: "ROLE-1", accountKey: "ef-role");
            endfield.Tracker.Observe(efToday);
            Assert(endfield.Tracker.Describe(efToday).Contains("2 天", StringComparison.Ordinal), "终末地未按04点换日");
        });
        Check("跨日旧快照不能误记新日", () =>
        {
            var f = new Fixture { Now = DateTimeOffset.Parse("2026-10-05T03:59:59+08:00") };
            var yesterday = f.State();
            f.Tracker.Observe(yesterday);
            f.Now = f.Now.AddSeconds(2);
            f.Tracker.Observe(yesterday); // Still fresh by age, but belongs to the previous game day.
            Assert(f.Store.Writes == 1 && DayCount(f.Store.Json!) == 1 && f.Description().Contains("今日待确认", StringComparison.Ordinal), "刚跨日的缓存被计入新日");
        });
        Check("错误/繁忙/过期/未登录/未知进度均不产生历史", () =>
        {
            var f = new Fixture();
            var valid = f.State();
            var rejected = new[]
            {
                valid with { Error = "读取失败" }, valid with { Busy = true }, valid with { Stale = true },
                valid with { LoggedIn = false }, valid with { UpdatedAt = null },
                valid with { UpdatedAt = f.Now.AddMinutes(-16) }, valid with { UpdatedAt = f.Now.AddSeconds(1) },
                valid with { DailyState = "unknown" }, valid with { DailyState = "locked" },
                valid with { MaxDaily = null }, valid with { MaxDaily = 0 }, valid with { Daily = null },
                valid with { Daily = 50, DailyState = "complete" }, valid with { Daily = 100, DailyState = "incomplete" },
                valid with { CopyIdentity = null }, valid with { CopyIdentity = "auth-cache-key" }, valid with { GameKey = "unsupported" }
            };
            foreach (var state in rejected) f.Tracker.Observe(state);
            Assert(f.Store.Reads == 0 && f.Store.Writes == 0, "无效快照触及历史");
        });
        Check("预览与演示不读取或写入真实历史", () =>
        {
            foreach (var preview in new[] { false, true })
            {
                var f = new Fixture(preview);
                var demo = f.State() with { Demo = true };
                f.Tracker.Observe(demo);
                Assert(f.Tracker.Describe(demo).Contains("演示记录", StringComparison.Ordinal), "演示连续数缺少标识");
                if (preview)
                {
                    f.Tracker.Observe(f.State());
                    Assert(f.Description().Contains("预览", StringComparison.Ordinal), "空态预览制造真实连续数");
                }
                Assert(f.Store.Reads == 0 && f.Store.Writes == 0, "演示/预览读取或覆盖真实历史");
            }
        });
        Check("历史持久化可恢复且不含 UID/昵称/授权明文", () =>
        {
            var f = new Fixture();
            const string uid = "123456789";
            const string authorization = "PRIVATE-AUTH-CACHE-MUST-NOT-BE-SAVED";
            var state = f.State(uid: uid, accountKey: authorization) with { Nickname = "PRIVATE-NAME-MUST-NOT-BE-SAVED", IdentityLabel = "UID " + uid };
            f.Tracker.Observe(state);
            f.Now = f.Now.AddDays(1);
            state = state with { UpdatedAt = f.Now };
            f.Tracker.Observe(state);
            var json = f.Store.Json!;
            Assert(!json.Contains(uid, StringComparison.Ordinal) && !json.Contains(authorization, StringComparison.Ordinal) && !json.Contains(state.Nickname, StringComparison.Ordinal)
                && !json.Contains("Token", StringComparison.Ordinal) && !json.Contains("Nickname", StringComparison.Ordinal), "持久文件包含身份或授权明文");
            Assert(json.Contains("local-observation", StringComparison.Ordinal), "本地记录冒充官方历史");
            var restored = new DailyStreakTracker(false, f.Store, () => f.Now);
            Assert(restored.Describe(state).Contains("2 天", StringComparison.Ordinal), "重启丢失连续记录");
            restored.Observe(state);
            Assert(f.Store.Writes == 2, "重启重复快照重新写入");
        });
        Check("损坏历史保留原文件且本次会话可继续记录", () =>
        {
            var f = new Fixture();
            f.Store.Json = "{not-valid-history";
            f.Tracker.Observe(f.State());
            Assert(f.Description().Contains("1 天", StringComparison.Ordinal) && f.Description().Contains("历史不可读", StringComparison.Ordinal), "损坏历史被无声清零");
            Assert(f.Store.Writes == 0 && f.Store.Json == "{not-valid-history", "损坏文件被覆盖");
        });
        Check("写入失败明确提示且后续刷新可恢复保存", () =>
        {
            var f = new Fixture();
            f.Store.FailWrite = true;
            f.Tracker.Observe(f.State());
            Assert(f.Description().Contains("未能保存", StringComparison.Ordinal) && f.Description().Contains("1 天", StringComparison.Ordinal), "写入失败未明确说明");
            f.Store.FailWrite = false;
            f.Now = f.Now.AddMinutes(5);
            f.Tracker.Observe(f.State());
            Assert(!f.Description().Contains("未能保存", StringComparison.Ordinal) && DayCount(f.Store.Json!) == 1, "恢复写入失败或重复记录");
        });
        Check("过期旧结果与时钟回退不能新增达标记录", () =>
        {
            var f = new Fixture();
            f.Tracker.Observe(f.State(complete: false));
            var old = f.State();
            f.Now = f.Now.AddMinutes(1);
            f.Tracker.Observe(f.State(complete: false));
            f.Tracker.Observe(old);
            Assert(f.Description().Contains("0 天", StringComparison.Ordinal) && f.Store.Writes == 1, "较旧结果覆盖较新观测");
            f.Now = f.Now.AddMinutes(-2);
            f.Tracker.Observe(old);
            Assert(f.Store.Writes == 1, "时钟回退接受未来快照");
        });
        Check("终末地无完整角色身份时不串号且显式server+role可降级", () =>
        {
            var f = new Fixture();
            var noServer = f.State(game: "endfield", uid: "ROLE-1", accountKey: null);
            f.Tracker.Observe(noServer);
            Assert(f.Store.Writes == 0, "仅角色ID被当成完整身份");
            var a = noServer with { Details = new[] { new DetailLine("服务器 ID", "server-a") } };
            var b = noServer with { Details = new[] { new DetailLine("服务器", "服务器乙", "服务器 ID：server-b") } };
            f.Tracker.Observe(a);
            f.Now = f.Now.AddDays(1);
            a = a with { UpdatedAt = f.Now };
            b = b with { UpdatedAt = f.Now };
            f.Tracker.Observe(a);
            f.Tracker.Observe(b);
            Assert(f.Tracker.Describe(a).Contains("2 天", StringComparison.Ordinal) && f.Tracker.Describe(b).Contains("1 天", StringComparison.Ordinal), "server+role 降级身份串号");
        });
        return Task.FromResult<IReadOnlyList<string>>(results);

        void Check(string name, Action action)
        {
            try { action(); results.Add("PASS " + name); }
            catch (Exception ex) { results.Add("FAIL " + name + "：" + ex.Message); }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static int DayCount(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("Accounts").EnumerateObject()
            .Sum(account => account.Value.GetProperty("Days").EnumerateObject().Count());
    }

    private sealed class Fixture
    {
        public MemoryStore Store { get; } = new();
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-05T12:00:00+08:00");
        public DailyStreakTracker Tracker { get; }
        public Fixture(bool preview = false) => Tracker = new DailyStreakTracker(preview, Store, () => Now);
        public GameViewState State(bool complete = true, string game = "wuwa", string uid = "100000101", string? accountKey = "auth-cache-1") => new()
        {
            GameKey = game, LoggedIn = true, CopyIdentity = uid, SelectedAccountKey = accountKey,
            Daily = complete ? 100 : 0, MaxDaily = 100, DailyState = complete ? "complete" : "incomplete", UpdatedAt = Now
        };
        public string Description() => Tracker.Describe(State());
    }

    private sealed class MemoryStore : IDailyHistoryStore
    {
        public string? Json { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool FailWrite { get; set; }
        public string? Load() { Reads++; return Json; }
        public void Save(string json)
        {
            Writes++;
            if (FailWrite) throw new InvalidOperationException("Injected in-memory write failure.");
            Json = json;
        }
    }
}
