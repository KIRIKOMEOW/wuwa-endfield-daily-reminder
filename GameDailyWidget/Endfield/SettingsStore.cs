using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EndfieldDesktopWidget;

public static class SettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndfieldDesktopWidget");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private static string TokenPath => Path.Combine(DataDirectory, "account.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EndfieldDesktopWidget.Account.v1");

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsPath)) ?? new WidgetSettings();
                settings.Scale = double.IsFinite(settings.Scale) ? Math.Clamp(settings.Scale, .65, 1.75) : 1;
                if (!double.IsFinite(settings.Left)) settings.Left = 80;
                if (!double.IsFinite(settings.Top)) settings.Top = 100;
                return settings;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
        return new WidgetSettings();
    }

    public static void Save(WidgetSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        AtomicWrite(SettingsPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })));
    }

    public static string? LoadAccountToken()
    {
        if (!File.Exists(TokenPath)) return null;
        try
        {
            byte[] decrypted = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(decrypted); }
            finally { CryptographicOperations.ZeroMemory(decrypted); }
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void SaveAccountToken(string? accountToken)
    {
        Directory.CreateDirectory(DataDirectory);
        if (string.IsNullOrWhiteSpace(accountToken))
        {
            if (File.Exists(TokenPath)) File.Delete(TokenPath);
            return;
        }
        byte[] plaintext = Encoding.UTF8.GetBytes(accountToken);
        try { AtomicWrite(TokenPath, ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static void OpenDataFolder()
    {
        Directory.CreateDirectory(DataDirectory);
        Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, true);
    }
}
