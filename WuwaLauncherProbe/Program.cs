using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wuwa.Core;

namespace WuwaLauncherProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var noPause = args.Contains("--no-pause") || Console.IsInputRedirected || Console.IsOutputRedirected;
        var report = new SafeReport { StartedAtUtc = DateTimeOffset.UtcNow };
        string? reportPath = null;
        var result = 1;
        try
        {
            int? chosen = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help": case "-h":
                        noPause = true;
                        Console.WriteLine("鸣潮国服读数工具（桌面版请运行 WuwaWidget.exe）");
                        Console.WriteLine("用法：WuwaLauncherProbe.exe [--account 序号] [--report 文件路径] [--no-pause]");
                        Console.WriteLine("账号列表显示角色昵称与特征码，报告仅保存匿名指标。");
                        return 0;
                    case "--no-pause": break;
                    case "--account":
                        if (chosen.HasValue || ++i >= args.Length || !int.TryParse(args[i], out var number) || number < 1)
                            throw new LauncherException("invalid_arguments", "--account 后请输入从 1 开始的序号，且只指定一次。");
                        chosen = number;
                        break;
                    case "--report":
                        if (reportPath is not null || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--"))
                            throw new LauncherException("invalid_arguments", "--report 后请输入报告路径，且只指定一次。");
                        reportPath = args[i];
                        ValidateReportPath(reportPath);
                        break;
                    default: throw new LauncherException("invalid_arguments", "存在不支持的参数，请使用 --help 查看用法。");
                }
            }

            Console.WriteLine("鸣潮国服官方启动器读数");
            Console.WriteLine("正在识别角色昵称与特征码……");
            using var client = new LauncherClient();
            var options = await client.DiscoverAccountsAsync();
            if (options.Count == 0)
                throw new LauncherException("authorization_missing", "没有可识别的本地授权，请先登录一次国服官方启动器。");
            for (var i = 0; i < options.Count; i++) Console.WriteLine($"  {i + 1}. {options[i].DisplayName}");
            int index;
            if (chosen.HasValue) index = chosen.Value;
            else if (options.Count == 1) index = 1;
            else if (Console.IsInputRedirected)
                throw new LauncherException("account_selection_required", "有多个授权项，请使用 --account 序号选择角色。");
            else
            {
                Console.Write("选择要查看的角色：");
                index = int.TryParse(Console.ReadLine(), out var selected) ? selected : 0;
            }
            if (index < 1 || index > options.Count)
                throw new LauncherException("account_selection_required", "选择的角色序号不存在。");
            var account = options[index - 1];
            if (account.Identity is null)
                throw new LauncherException("authorization_unavailable", account.Problem ?? "该授权项暂时无法读取，请选择其他角色或更新启动器授权。");
            var snapshot = await client.QueryAsync(account.Handle);
            if (snapshot.Identity.Uid != account.Identity.Uid)
                throw new LauncherException("account_changed", "账号授权已变化，请重新运行并选择角色。");
            report.Status = snapshot.MissingFields.Count > 0 ? "fields_missing" : "success";
            // Nickname and UID are deliberately excluded from diagnostic reports.
            report.Metrics = new
            {
                snapshot.FetchedAtUtc, snapshot.Energy, snapshot.MaxEnergy, snapshot.EnergyFullAtUtc,
                snapshot.StoreEnergy, snapshot.MaxStoreEnergy, snapshot.StoreEnergyFullAtUtc,
                snapshot.Liveness, snapshot.LivenessMaxCount, snapshot.LivenessUnlock, snapshot.DailyState,
                snapshot.BattlePassLevel, snapshot.WeekExp, snapshot.WeekMaxExp, snapshot.MissingFields
            };
            Console.WriteLine();
            Console.WriteLine($"游戏 ID：{snapshot.Identity.Name}");
            Console.WriteLine($"特征码：{snapshot.Identity.Uid}    联觉等级：{Value(snapshot.Identity.Level)}");
            Console.WriteLine($"日常：{Daily(snapshot.DailyState)}    活跃度：{Value(snapshot.Liveness)}/{Value(snapshot.LivenessMaxCount)}");
            Console.WriteLine($"结晶波片：{Value(snapshot.Energy)}/{Value(snapshot.MaxEnergy)}    {Recovery(snapshot.Energy, snapshot.MaxEnergy, snapshot.EnergyFullAtUtc)}");
            Console.WriteLine($"结晶单质：{Value(snapshot.StoreEnergy)}/{Value(snapshot.MaxStoreEnergy)}    {Recovery(snapshot.StoreEnergy, snapshot.MaxStoreEnergy, snapshot.StoreEnergyFullAtUtc)}");
            Console.WriteLine($"电台等级：{Value(snapshot.BattlePassLevel)}    本周经验：{Value(snapshot.WeekExp)}/{Value(snapshot.WeekMaxExp)}");
            Console.WriteLine($"更新时间：{snapshot.FetchedAtUtc.ToLocalTime():HH:mm:ss}");
            if (snapshot.MissingFields.Count > 0) Console.WriteLine("部分数据未返回，已显示为未知。");
            result = report.Status == "success" ? 0 : 2;
        }
        catch (LauncherException ex)
        {
            report.Status = ex.Status;
            Console.WriteLine($"读取未完成：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            report.Status = "cancelled";
            Console.WriteLine("读取已取消。");
        }
        catch (Exception)
        {
            report.Status = "unexpected_error";
            Console.WriteLine("读取遇到错误，请稍后重试。");
        }
        finally
        {
            report.CompletedAtUtc = DateTimeOffset.UtcNow;
            if (reportPath is not null)
            {
                try
                {
                    ValidateReportPath(reportPath);
                    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }), new UTF8Encoding(false));
                    Console.WriteLine("已保存匿名读数报告。");
                }
                catch (Exception) { Console.WriteLine("报告未能写入，请选择普通文件夹中的新文件。"); result = 3; }
            }
            if (!noPause) { Console.WriteLine("按 Enter 关闭……"); Console.ReadLine(); }
        }
        return result;
    }

    private static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未知";
    private static string Daily(string state) => state switch { "complete" => "已完成", "incomplete" => "未完成", "locked" => "未解锁", _ => "未知" };
    private static string Recovery(int? current, int? maximum, DateTimeOffset? fullAt)
    {
        if (current.HasValue && maximum is > 0 && current >= maximum) return "已满";
        if (fullAt.HasValue && fullAt > DateTimeOffset.UtcNow) return $"回满时间 {fullAt.Value.ToLocalTime():MM-dd HH:mm}";
        return "回满时间未知";
    }
    private static void ValidateReportPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var cacheRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KR_G152"));
        if (fullPath.Equals(cacheRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(fullPath).Equals("KRSDKUserLauncherCache.json", StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("report_path_not_allowed", "读数报告不能覆盖启动器缓存，请选择其他文件夹。");
    }
    private sealed class SafeReport
    {
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public string Status { get; set; } = "not_started";
        public object? Metrics { get; set; }
    }
}
