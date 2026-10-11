using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (StartupHost.TryRun(args)) return;
        // Old login shortcuts and recovery tasks now enter the real GUI singleton.
        // A watcher host must never be mistaken for a running overlay again.
        args = args.Where(arg => arg is not "--watch-codex" and not "--login-startup").ToArray();
        var sessionRoot = SessionPathResolver.Resolve(args);
        if (DiagnosticsRunner.TryRun(args, sessionRoot) || ProbeRunner.TryRun(args, sessionRoot))
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "Local\\CodexRelayUsage",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var settingsPath = OverlaySettings.ResolveSettingsOverride(args);
        Application.Run(new OverlayContext(sessionRoot, settingsPath, !args.Contains("--sessions")));
        GC.KeepAlive(singleInstanceMutex);
    }
}

internal static class SessionPathResolver
{
    public static string Resolve(IReadOnlyList<string>? arguments = null)
    {
        if (arguments is not null)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index].Equals("--sessions", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    return Normalize(arguments[index + 1]);
                }
            }
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.Combine(Normalize(codexHome), "sessions");
    }

    private static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.Equals("~", StringComparison.Ordinal)
            || expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded.Length == 1 ? string.Empty : expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }
}

internal sealed record ActiveThreadRouteStatus(
    string? ThreadId,
    int ActiveWindowCount,
    bool IsConnected,
    long Version,
    string? LastError, string? ViewKey = null, string? Identification = null, bool SameTitleAmbiguous = false);

