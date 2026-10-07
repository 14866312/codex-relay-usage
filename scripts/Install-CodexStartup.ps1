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
if (-not $ShortcutRoot) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $taskName = 'CodexRelayUsage-Watcher-' + $identity.User.Value
    if ($Remove) {
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($task -and $task.Description -eq 'CodexRelayUsage startup watcher recovery') {
            Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        }
    } else {
        if (-not (Test-Path -LiteralPath $overlay)) { throw '请从 Windows 独立版目录安装。' }
        $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($existingTask -and $existingTask.Description -ne 'CodexRelayUsage startup watcher recovery') {
            throw '同名计划任务不属于本工具，已取消设置。'
        }
        $action = New-ScheduledTaskAction -Execute $powerShell -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $watcher + '"') -WorkingDirectory $releaseRoot
        $triggers = @(
            (New-ScheduledTaskTrigger -AtLogOn -User $identity.Name),
            (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1))
        )
        $principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        try {
            Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Description 'CodexRelayUsage startup watcher recovery' -Force | Out-Null
        } catch {
            throw ('未能安装后台恢复任务，自动启动设置未完成：' + $_.Exception.Message)
        }
    }
}
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
        # Retire old package watchers before the new task acquires the singleton mutex.
        Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
            $_.CommandLine -match '-File "[^"\r\n]+[/\\]scripts[/\\]Watch-Codex\.ps1"' -and
            $_.ProcessId -ne $PID
        } | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        Start-ScheduledTask -TaskName $taskName
    }
}
Write-Host $(if ($Remove) { '已移除自动启动并恢复已备份的普通入口。' } else { '已设置随 Codex 启动。下次请使用桌面或开始菜单的 Codex 快捷方式。' })
