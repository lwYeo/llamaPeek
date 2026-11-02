using System.Security;
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
    // Test hook: unit tests point this at HKCU\Software\llamaPeek-test so a
    // test run can never clobber the user's live configuration. Production
    // code never changes it.
    public static string AppKeyPath { get; set; } = @"Software\llamaPeek";
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
        // One-time schema migration: pre-0.2 configs overloaded RequiresModel
        // with "required alert". Split: the flag users meant becomes Required;
        // router mode is opt-in again. The SchemaV2 marker is written right
        // here so an explicit Model tick is never re-migrated, even if the
        // user never presses OK.
        if (key.GetValue("SchemaV2") as int? != 1)
        {
            for (int i = 0; i < s.Servers.Count; i++)
                if (s.Servers[i].RequiresModel)
                    s.Servers[i] = s.Servers[i] with { RequiresModel = false, Required = true };
            try
            {
                using var w = Registry.CurrentUser.OpenSubKey(AppKeyPath, writable: true);
                w?.SetValue("SchemaV2", 1, RegistryValueKind.DWord);
            }
            catch (SecurityException) { /* read-only registry: migration stays in-memory */ }
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
        key.SetValue("SchemaV2", 1, RegistryValueKind.DWord);
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
