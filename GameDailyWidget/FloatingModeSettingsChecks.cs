using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameDailyWidget;

/// <summary>Checks synthetic legacy preferences without loading any personal files.</summary>
public static class FloatingModeSettingsChecks
{
    private const string LegacyJson = """
        {
          "DesktopMode": true,
          "Left": -1543.25,
          "Top": 118.75,
          "AlwaysOnTop": true,
          "Scale": 1.37,
          "Opacity": 0.82,
          "CurrentGame": "endfield",
          "StartWithWindows": true,
          "StartupReminderEnabled": false,
          "LastStartupReminderDay": "2026-10-06",
          "EdgeAutoHide": false,
          "DockedEdge": "left",
          "Wuwa": {
            "AccountKey": "synthetic-wuwa-account",
            "RefreshMinutes": 10,
            "GamePath": "Z:\\合成测试\\鸣潮.exe"
          },
          "Endfield": {
            "SelectedRoleId": "synthetic-role-1001",
            "SelectedRoleKey": "synthetic-server_role",
            "RefreshMinutes": 1,
            "GamePath": "Z:\\合成测试\\终末地.exe"
          }
        }
        """;

    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var checks = new List<string>();
        var settings = Read(LegacyJson);
        AssertPreserved(settings);
        checks.Add("PASS 含旧DesktopMode和Opacity的合成配置仍保留账号选择、兼容路径和刷新间隔");
        checks.Add("PASS 移除桌面固定和透明度字段仍保留置顶、物理位置、自由缩放和其他本机偏好");

        var serialized = JsonSerializer.Serialize(settings);
        using (var output = JsonDocument.Parse(serialized))
        {
            Assert(!output.RootElement.TryGetProperty("DesktopMode", out _), "新配置仍保存废弃的桌面固定字段");
            Assert(!output.RootElement.TryGetProperty("Opacity", out _), "新配置仍保存废弃的透明度字段");
            Assert(!output.RootElement.TryGetProperty("IsPreview", out _), "预览运行标记被写入配置");
            Assert(!output.RootElement.TryGetProperty("LastSaveSucceeded", out _), "运行时保存状态被写入配置");
        }
        AssertPreserved(Read(serialized));
        checks.Add("PASS 新配置不再输出DesktopMode或Opacity且往返转换保留其他偏好");

        var changed = Read(LegacyJson.Replace("\"DesktopMode\": true", "\"DesktopMode\": false", StringComparison.Ordinal)
            .Replace("\"Opacity\": 0.82", "\"Opacity\": 0.2", StringComparison.Ordinal));
        var omitted = Read(LegacyJson.Replace("\"DesktopMode\": true,", "", StringComparison.Ordinal)
            .Replace("\"Opacity\": 0.82,", "", StringComparison.Ordinal));
        AssertPreserved(changed);
        AssertPreserved(omitted);
        Assert(JsonSerializer.Serialize(changed) == serialized && JsonSerializer.Serialize(omitted) == serialized,
            "废弃桌面固定或透明度字段的值变化及缺失仍改变了有效偏好");
        checks.Add("PASS 废弃桌面固定和透明度字段值变化或缺失均得到相同有效设置");

        // Load(true) returns before any normal-settings path or legacy import.
        // Save() returns immediately for this object and performs no file access.
        var preview = UnifiedSettings.Load(preview: true);
        Assert(preview.IsPreview, "预览未隔离");
        Assert(preview.Wuwa.AccountKey is null && preview.Endfield.SelectedRoleId is null &&
            preview.Endfield.SelectedRoleKey is null, "预览载入了个人账号选择");
        preview.Wuwa.AccountKey = "preview-only-synthetic-choice";
        preview.Left = -999;
        preview.AlwaysOnTop = true;
        preview.Save();
        var anotherPreview = UnifiedSettings.Load(preview: true);
        Assert(anotherPreview.IsPreview && anotherPreview.Wuwa.AccountKey is null && anotherPreview.Left is null &&
            !anotherPreview.AlwaysOnTop, "预览修改被持久化或污染后续预览实例");
        checks.Add("PASS 预览仅使用独立内存设置，保存操作直接返回且不读取正常个人配置");

        return Task.FromResult<IReadOnlyList<string>>(checks.AsReadOnly());
    }

    private static UnifiedSettings Read(string json) => JsonSerializer.Deserialize<UnifiedSettings>(json)
        ?? throw new InvalidOperationException("合成设置未能反序列化");

    private static void AssertPreserved(UnifiedSettings settings)
    {
        Assert(settings.Wuwa.AccountKey == "synthetic-wuwa-account" && settings.Wuwa.RefreshMinutes == 10 &&
            settings.Wuwa.GamePath == @"Z:\合成测试\鸣潮.exe", "鸣潮偏好丢失");
        Assert(settings.Endfield.SelectedRoleId == "synthetic-role-1001" &&
            settings.Endfield.SelectedRoleKey == "synthetic-server_role" && settings.Endfield.RefreshMinutes == 1 &&
            settings.Endfield.GamePath == @"Z:\合成测试\终末地.exe", "终末地偏好丢失");
        Assert(settings.Left == -1543.25 && settings.Top == 118.75 && settings.AlwaysOnTop &&
            settings.Scale == 1.37 && settings.CurrentGame == "endfield",
            "位置、置顶、缩放或游戏页面偏好丢失");
        Assert(settings.StartWithWindows && !settings.StartupReminderEnabled &&
            settings.LastStartupReminderDay == "2026-10-06" && !settings.EdgeAutoHide && settings.DockedEdge == "left",
            "其他本机偏好丢失");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
