param([switch]$Remove, [switch]$NoStart, [string]$ShortcutRoot = '')
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$overlay = Join-Path $releaseRoot 'CodexRelayUsage.exe'
$powerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$stateRoot = Join-Path $env:LOCALAPPDATA 'CodexRelayUsage/startup'
if (-not $Remove -and -not (Test-Path -LiteralPath $overlay)) { throw '请从 Windows 独立版目录安装。' }
if ($ShortcutRoot) {
    # Isolated installer fixture; never changes real tasks or processes.
    $desktop = Join-Path $ShortcutRoot 'Desktop'
    $programs = Join-Path $ShortcutRoot 'Programs'
    $startup = Join-Path $ShortcutRoot 'Startup'
    $stateRoot = Join-Path $ShortcutRoot 'State'
} else {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $programs = [Environment]::GetFolderPath('Programs')
    $startup = [Environment]::GetFolderPath('Startup')
    # Migrate the old login/minute watcher. No replacement task is needed.
    $taskName = 'CodexRelayUsage-Watcher-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task -and $task.Description -eq 'CodexRelayUsage startup watcher recovery') {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' or Name='CodexRelayUsage.exe'")
    foreach ($process in $processes) {
        if ($process.ProcessId -eq $PID) { continue }
        $oldWatcher = $false
        if ($process.Name -eq 'powershell.exe' -and $process.CommandLine -match '-File\s+(?:"(?<script>[^"\r\n]+[/\\]scripts[/\\]Watch-Codex\.ps1)"|(?<script>\S+[/\\]scripts[/\\]Watch-Codex\.ps1))(?=\s|$)') {
            $oldRoot = Split-Path -Parent (Split-Path -Parent $Matches.script)
            $oldWatcher = Test-Path -LiteralPath (Join-Path $oldRoot 'CodexRelayUsage.exe')
        } elseif ($process.Name -eq 'CodexRelayUsage.exe' -and $process.CommandLine -match '(?:^|\s)--watch-codex(?:\s|$)' -and $process.ExecutablePath) {
            # New versions accept this legacy flag as a real overlay; leave those alone.
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($process.ExecutablePath)
            $installedVersion = [Version]::new($version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart, $version.FilePrivatePart)
            $oldWatcher = $installedVersion -lt [Version]'1.1.8.0'
        }
        if ($oldWatcher) { Stop-Process -Id $process.ProcessId -ErrorAction SilentlyContinue }
    }
}
$shell = New-Object -ComObject WScript.Shell
$legacyDescription = 'Codex 与会话用量自动启动'
$loginDescription = 'Codex 会话用量：登录 Windows 自动启动'
$entries = @(
    @{ Path = (Join-Path $startup 'Codex 用量自动启动.lnk'); Arguments = ''; Backup = 'startup.lnk'; Description = $loginDescription }
)
foreach ($entry in $entries) {
    $backup = Join-Path $stateRoot $entry.Backup
    $link = if (Test-Path -LiteralPath $entry.Path) { $shell.CreateShortcut($entry.Path) } else { $null }
    $isOurs = $false
    if ($link -and $link.Description -in @($legacyDescription, $loginDescription)) {
        $isNative = [IO.Path]::GetFileName($link.TargetPath) -eq 'CodexRelayUsage.exe' -and
            ($link.Arguments -in @('--launch-codex', '--watch-codex') -or
             ($entry.Arguments -eq '' -and $link.Arguments -eq '' -and $link.Description -eq $loginDescription))
        # Accept both quoted and unquoted -File arguments from older releases.
        $isLegacyScript = $link.TargetPath -eq $powerShell -and $link.Description -eq $legacyDescription -and
            $link.Arguments -match '(?:Start-CodexAuto|Watch-Codex)\.ps1"?\s*$'
        $isOurs = $isNative -or $isLegacyScript
    }
    if ($Remove) {
        if ($isOurs) {
            Remove-Item -LiteralPath $entry.Path
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $entry.Path }
        }
        continue
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Path), $stateRoot -Force | Out-Null
    if ($link -and -not $isOurs) {
        if (Test-Path -LiteralPath $backup) { throw '存在旧快捷方式备份，请先还原或保留当前入口。' }
        Copy-Item -LiteralPath $entry.Path -Destination $backup
    }
    $link = $shell.CreateShortcut($entry.Path)
    $link.TargetPath = $overlay
    $link.Arguments = $entry.Arguments
    $link.WorkingDirectory = $releaseRoot
    $link.Description = $entry.Description
    $link.IconLocation = $overlay
    $link.Save()
}
# Older releases replaced the desktop and Start Menu Codex entries; only their cleanup remains.
foreach ($stale in @(
    @{ Path = (Join-Path $desktop 'Codex.lnk'); Backup = 'desktop.lnk' },
    @{ Path = (Join-Path $programs 'Codex.lnk'); Backup = 'programs.lnk' })) {
    if (-not (Test-Path -LiteralPath $stale.Path)) { continue }
    $staleLink = $shell.CreateShortcut($stale.Path)
    if ($staleLink.Description -notin @($legacyDescription, $loginDescription)) { continue }
    $isNative = [IO.Path]::GetFileName($staleLink.TargetPath) -eq 'CodexRelayUsage.exe' -and
        $staleLink.Arguments -in @('--launch-codex', '--watch-codex')
    $isLegacyScript = $staleLink.TargetPath -eq $powerShell -and $staleLink.Description -eq $legacyDescription -and
        $staleLink.Arguments -match '(?:Start-CodexAuto|Watch-Codex)\.ps1"?\s*$'
    if (-not ($isNative -or $isLegacyScript)) { continue }
    Remove-Item -LiteralPath $stale.Path
    $staleBackup = Join-Path $stateRoot $stale.Backup
    if (Test-Path -LiteralPath $staleBackup) { Move-Item -LiteralPath $staleBackup -Destination $stale.Path }
}
if (-not $ShortcutRoot -and -not $Remove -and -not $NoStart) {
    Start-Process -FilePath $overlay -WorkingDirectory $releaseRoot -WindowStyle Hidden
}
Write-Host $(if ($Remove) { '已取消开机自动启动，并清理旧版写入的桌面／开始菜单入口。' } else { '已设置登录 Windows 后直接启动工具；Codex 打开后悬浮条自动出现。' })