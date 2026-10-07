param([switch]$Remove, [switch]$NoStart, [string]$ShortcutRoot = '')
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$overlay = Join-Path $releaseRoot 'CodexRelayUsage.exe'
$powerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$launcher = Join-Path $PSScriptRoot 'Start-CodexAuto.ps1'
$watcher = Join-Path $PSScriptRoot 'Watch-Codex.ps1'
$stateRoot = Join-Path $env:LOCALAPPDATA 'CodexRelayUsage/startup'
if ($ShortcutRoot) {
    # Isolated directory for installer verification; never used by normal installation.
    $desktop = Join-Path $ShortcutRoot 'Desktop'
    $programs = Join-Path $ShortcutRoot 'Programs'
    $startup = Join-Path $ShortcutRoot 'Startup'
    $stateRoot = Join-Path $ShortcutRoot 'State'
} else {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $programs = [Environment]::GetFolderPath('Programs')
    $startup = [Environment]::GetFolderPath('Startup')
}
$shell = New-Object -ComObject WScript.Shell
$entries = @(
    @{ Path = (Join-Path $desktop 'Codex.lnk'); Script = $launcher; Backup = 'desktop.lnk' },
    @{ Path = (Join-Path $programs 'Codex.lnk'); Script = $launcher; Backup = 'programs.lnk' },
    @{ Path = (Join-Path $startup 'Codex 用量自动启动.lnk'); Script = $watcher; Backup = 'startup.lnk' }
)
foreach ($entry in $entries) {
    $backup = Join-Path $stateRoot $entry.Backup
    $link = $null
    if (Test-Path -LiteralPath $entry.Path) { $link = $shell.CreateShortcut($entry.Path) }
    $isOurs = $link -and $link.TargetPath -eq $powerShell -and
        $link.Description -eq 'Codex 与会话用量自动启动' -and
        $link.Arguments -match '(Start-CodexAuto|Watch-Codex)\.ps1"$'
    if ($Remove) {
        if ($isOurs) {
            Remove-Item -LiteralPath $entry.Path
            if (Test-Path -LiteralPath $backup) {
                Move-Item -LiteralPath $backup -Destination $entry.Path
            }
        }
        continue
    }
    if (-not (Test-Path -LiteralPath $overlay)) { throw '请从 Windows 独立版目录安装。' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Path), $stateRoot -Force | Out-Null
    if ($link -and -not $isOurs) {
        if (Test-Path -LiteralPath $backup) { throw '存在旧快捷方式备份，请先还原或保留当前入口。' }
        Copy-Item -LiteralPath $entry.Path -Destination $backup
    }
    $link = $shell.CreateShortcut($entry.Path)
    $link.TargetPath = $powerShell
    $link.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $entry.Script + '"'
    $link.WorkingDirectory = $releaseRoot
    $link.Description = 'Codex 与会话用量自动启动'
    $package = Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1
    if ($package) { $link.IconLocation = (Join-Path $package.InstallLocation 'app/ChatGPT.exe') }
    else { $link.IconLocation = $overlay }
    $link.Save()
}
if (-not $ShortcutRoot) {
    if ($Remove) {
        Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
            $_.CommandLine -like ('*' + $watcher + '*')
        } | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
    } elseif (-not $NoStart) {
        Start-Process -FilePath $powerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', ('"' + $watcher + '"')) -WindowStyle Hidden
    }
}
Write-Host $(if ($Remove) { '已移除自动启动并恢复已备份的普通入口。' } else { '已设置随 Codex 启动。下次请使用桌面或开始菜单的 Codex 快捷方式。' })
