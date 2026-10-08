param([switch]$Restart, [switch]$CreateShortcut)
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$overlay = Join-Path $releaseRoot 'CodexRelayUsage.exe'
if (-not (Test-Path -LiteralPath $overlay)) { throw '请从 Windows 独立版目录运行此入口。' }
if ($CreateShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $desktop 'Codex 自动用量.lnk'))
    $shortcut.TargetPath = $overlay
    $shortcut.Arguments = '--launch-codex'
    $shortcut.WorkingDirectory = $releaseRoot
    $shortcut.IconLocation = $overlay
    $shortcut.Save()
    exit
}
try {
    $package = Get-AppxPackage | Where-Object { $_.Name -eq 'OpenAI.Codex' } | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $package) { throw '未找到已安装的 Codex 应用。' }
    $codexExe = Join-Path $package.InstallLocation 'app/ChatGPT.exe'
    if (-not (Test-Path -LiteralPath $codexExe)) { throw '当前 Codex 安装结构不支持此启动入口。' }
    $running = @(Get-Process ChatGPT -ErrorAction SilentlyContinue | Where-Object {
        $_.MainWindowHandle -ne 0 -and $_.Path -like '*OpenAI.Codex*'
    })
    if ($Restart) {
        foreach ($app in $running) {
            if (-not $app.CloseMainWindow()) { throw 'Codex 未接受关闭请求，请先正常关闭后使用专用入口。' }
            if (-not $app.WaitForExit(20000)) { throw 'Codex 尚未退出，已取消重启，请完成当前任务后重新打开。' }
        }
        $running = @()
    }
    if ($running.Count -eq 0) {
        Start-Process -FilePath $codexExe -ArgumentList @('--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0') -WindowStyle Hidden
    }
    Start-Process -FilePath $overlay -WorkingDirectory $releaseRoot -WindowStyle Hidden
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Codex 自动用量') | Out-Null
    exit 1
}
