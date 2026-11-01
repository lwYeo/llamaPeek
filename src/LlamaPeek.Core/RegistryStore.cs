using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace LlamaPeek.Core;

/// <summary>
/// User-level registry persistence at HKCU\Software\llamaPeek. Config-only values
/// plus the API key as a DPAPI CurrentUser blob — plaintext key never touches disk.
/// </summary>
public static class RegistryStore
{
    public const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AppKeyPath = @"Software\llamaPeek";
    public const string RunValueName = "llamaPeek";

    public static AppSettings Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppKeyPath);
        var s = new AppSettings();
        if (key is null) return s;

        var serversJson = key.GetValue("Servers") as string;
        if (!string.IsNullOrEmpty(serversJson))
        {
            try
            {
                s.Servers = JsonSerializer.Deserialize<List<ServerConfig>>(serversJson) ?? [];
            }
            catch (JsonException) { /* corrupt value -> defaults */ }
        }
        s.PollActiveMs = Math.Clamp(GetInt(key, "PollActiveMs", s.PollActiveMs), 100, 60_000);
        s.PollIdleMs = Math.Clamp(GetInt(key, "PollIdleMs", s.PollIdleMs), 100, 600_000);
        s.GraceMs = Math.Clamp(GetInt(key, "GraceMs", s.GraceMs), 0, 600_000);
        s.RefreshMs = Math.Clamp(GetInt(key, "RefreshMs", s.RefreshMs), 100, 5000);
        s.LingerMs = Math.Clamp(GetInt(key, "LingerMs", s.LingerMs), 0, 60_000);
        // Grace must cover the linger window, or polling drops to idle cadence
        // while the icon still shows a lingering value.
        if (s.GraceMs < s.LingerMs) s.GraceMs = s.LingerMs;
        s.Smoothing = double.TryParse(key.GetValue("Smoothing") as string,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, 0.0, 1.0) : s.Smoothing;
        s.LaunchAtStartup = key.GetValue("LaunchAtStartup") as int? == 1;
        return s;
    }

    public static void Save(AppSettings s)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AppKeyPath);
        key.SetValue("Servers", JsonSerializer.Serialize(s.Servers));
        key.SetValue("PollActiveMs", s.PollActiveMs, RegistryValueKind.DWord);
        key.SetValue("PollIdleMs", s.PollIdleMs, RegistryValueKind.DWord);
        key.SetValue("GraceMs", s.GraceMs, RegistryValueKind.DWord);
        key.SetValue("RefreshMs", Math.Clamp(s.RefreshMs, 100, 5000), RegistryValueKind.DWord);
        key.SetValue("LingerMs", Math.Clamp(s.LingerMs, 0, 60_000), RegistryValueKind.DWord);
        key.SetValue("Smoothing",
            s.Smoothing.ToString(System.Globalization.CultureInfo.InvariantCulture));
        key.SetValue("LaunchAtStartup", s.LaunchAtStartup ? 1 : 0, RegistryValueKind.DWord);
    }

    public static string? LoadApiKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppKeyPath);
        if (key?.GetValue("ApiKey") is byte[] blob and not { Length: 0 })
        {
            try
            {
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { return null; }
        }
        return null;
    }

    public static void SaveApiKey(string? key)
    {
        using var rk = Registry.CurrentUser.CreateSubKey(AppKeyPath);
        if (string.IsNullOrEmpty(key))
            rk.DeleteValue("ApiKey", false);
        else
            rk.SetValue("ApiKey",
                ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null,
                    DataProtectionScope.CurrentUser),
                RegistryValueKind.Binary);
    }

    /// <summary>Launch at startup: HKCU Run key, exe path quoted.</summary>
    public static void SetLaunchAtStartup(bool enabled, string exePath)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            run.SetValue(RunValueName, $"\"{exePath}\"", RegistryValueKind.String);
        else
            run.DeleteValue(RunValueName, false);
    }

    /// <summary>Uninstall helper: remove all app state (settings, API key, Run key).</summary>
    public static void DeleteAll()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            run?.DeleteValue(RunValueName, false);
        Registry.CurrentUser.DeleteSubKeyTree(AppKeyPath, throwOnMissingSubKey: false);
    }

    private static int GetInt(RegistryKey key, string name, int fallback) =>
        key.GetValue(name) is int i ? i : fallback;
}
