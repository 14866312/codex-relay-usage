using System.Diagnostics;

namespace CodexTokenOverlay;

internal static class StartupHost
{
    public static bool TryRun(string[] args)
    {
        var install = args.Contains("--install-startup") || args.Contains("--remove-startup");
        if (!install && !args.Contains("--launch-codex")) return false;
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
                Path.Combine(AppContext.BaseDirectory, "scripts", install ? "Install-CodexStartup.ps1" : "Start-CodexAuto.ps1") })
                start.ArgumentList.Add(argument);
            if (args.Contains("--remove-startup")) start.ArgumentList.Add("-Remove");
            using var child = Process.Start(start) ?? throw new InvalidOperationException("启动失败");
            child.WaitForExit();
            Environment.ExitCode = child.ExitCode;
            if (install && !args.Contains("--quiet"))
                MessageBox.Show(child.ExitCode == 0
                    ? args.Contains("--remove-startup") ? "已取消开机自动启动。" : "已设置登录 Windows 后自动启动。Codex 打开后悬浮条会自动出现。"
                    : "自动启动设置失败，请运行 scripts/Install-CodexStartup.ps1 查看原因。", "Codex 会话用量");
        }
        catch (Exception error)
        {
            if (!args.Contains("--quiet")) MessageBox.Show(error.Message, "Codex 会话用量");
            Environment.ExitCode = 1;
        }
        return true;
    }
}
