using System.Runtime.InteropServices;
using LlamaPeek.Core;

namespace LlamaPeek.Tray;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--version" or "-v")
        {
            // WinExe has no console attached; allocate one so the text is visible.
            AllocConsole();
            Console.WriteLine($"llamaPeek {Version}");
            return;
        }
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\llamaPeek", out bool isNew);
        if (!isNew) return;                       // single instance
        Application.Run(new TrayHost());
        GC.KeepAlive(mutex);
    }

    /// <summary>Single source of truth for the version: set in the csproj.</summary>
    public static string Version =>
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
