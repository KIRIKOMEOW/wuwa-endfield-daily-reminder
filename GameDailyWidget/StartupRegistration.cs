using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GameDailyWidget;

internal interface IStartupStore
{
    string? Read(string name);
    void Write(string name, string command);
    void Remove(string name);
}

internal sealed class WindowsStartupStore : IStartupStore
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(name) as string;
    }
    public void Write(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue(name, command, RegistryValueKind.String);
    }
    public void Remove(string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.DeleteValue(name, false);
    }
}

internal static class StartupRegistration
{
    internal const string EntryName = "GameDailyWidget";
    internal static string ResolveExecutable()
    {
        var executable = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executable) || string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "GameDailyWidget.exe") : executable;
    }
    internal static string BuildCommand(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('"') || executable.Contains('\r') || executable.Contains('\n'))
            throw new ArgumentException("Invalid executable path.");
        return "\"" + Path.GetFullPath(executable) + "\" --startup";
    }
    internal static bool IsEnabled(IStartupStore? store = null)
    {
        try { return !string.IsNullOrWhiteSpace((store ?? new WindowsStartupStore()).Read(EntryName)); }
        catch { return false; }
    }
    internal static bool SetEnabled(bool enabled, string executable, IStartupStore? store = null)
    {
        try
        {
            var target = store ?? new WindowsStartupStore();
            if (enabled) target.Write(EntryName, BuildCommand(executable));
            else target.Remove(EntryName);
            return true;
        }
        catch { return false; }
    }
}

public static class StartupChecks
{
    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var store = new MemoryStore();
        store.Values["WuwaDailyWidget"] = "original-wuwa-command";
        store.Values["EndfieldDesktopWidget"] = "original-endfield-command";
        var path = @"C:\组件 路径\双游戏日常.exe";
        if (!StartupRegistration.SetEnabled(true, path, store) || !StartupRegistration.IsEnabled(store)
            || store.Values[StartupRegistration.EntryName] != "\"" + path + "\" --startup")
            throw new InvalidOperationException("Startup command must quote paths and use the unified startup argument.");
        if (!StartupRegistration.SetEnabled(false, path, store) || StartupRegistration.IsEnabled(store)
            || store.Values["WuwaDailyWidget"] != "original-wuwa-command" || store.Values["EndfieldDesktopWidget"] != "original-endfield-command")
            throw new InvalidOperationException("Disabling unified startup must leave standalone registrations untouched.");
        if (StartupRegistration.SetEnabled(true, "bad\"path.exe", store))
            throw new InvalidOperationException("Invalid startup command path was accepted.");
        return Task.FromResult<IReadOnlyList<string>>(new[]
        {
            "PASS 开机自启正确引用含中文和空格的EXE路径",
            "PASS 关闭合并版自启不改变原独立程序注册项",
            "PASS 开机自启拒绝无效的命令路径"
        });
    }
    private sealed class MemoryStore : IStartupStore
    {
        internal Dictionary<string, string> Values { get; } = new();
        public string? Read(string name) => Values.TryGetValue(name, out var value) ? value : null;
        public void Write(string name, string command) => Values[name] = command;
        public void Remove(string name) => Values.Remove(name);
    }
}
