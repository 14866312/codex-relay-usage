using System.Diagnostics;

namespace CodexTokenOverlay;

internal static class StartupHost
{
    public static bool TryRun(string[] args)
    {
        var watch = args.Contains("--watch-codex");
        if (!watch && !args.Contains("--launch-codex")) return false;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(AppContext.BaseDirectory, "scripts", watch ? "Watch-Codex.ps1" : "Start-CodexAuto.ps1") })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("启动失败");
            // Keep the task running for the watcher's lifetime so IgnoreNew still applies.
            child.WaitForExit();
            Environment.ExitCode = child.ExitCode;
        }
        catch (Exception error)
        {
            if (!watch) MessageBox.Show(error.Message, "Codex 自动用量");
            Environment.ExitCode = 1;
        }
        return true;
    }
}
