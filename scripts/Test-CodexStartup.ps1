param([Parameter(Mandatory=$true)][string]$FixtureRoot, [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$checks = 0
function Check($condition, $name) { if (-not $condition) { throw $name }; $script:checks++ }
$FixtureRoot = [IO.Path]::GetFullPath($FixtureRoot)
foreach ($script in Get-ChildItem (Join-Path $FixtureRoot 'scripts') -Filter '*.ps1') {
    $tokens = $null; $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    Check ($errors.Count -eq 0) ('PS5 parse ' + $script.Name)
}
$links = Join-Path $FixtureRoot ('isolated-' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($folder in @('Desktop', 'Programs', 'Startup')) { New-Item -ItemType Directory (Join-Path $links $folder) -Force | Out-Null }
    $shell = New-Object -ComObject WScript.Shell
    $original = $shell.CreateShortcut((Join-Path $links 'Desktop/Codex.lnk'))
    $original.TargetPath = "$env:SystemRoot/notepad.exe"; $original.Save()
    $originalHash = (Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash
    $installer = Join-Path $FixtureRoot 'scripts/Install-CodexStartup.ps1'
    & $installer -ShortcutRoot $links -NoStart
    Check ((Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash -eq $originalHash) 'installer keeps the user desktop entry'
    Check (-not (Test-Path (Join-Path $links 'State/desktop.lnk'))) 'installer writes no desktop shortcut backup'
    Check (-not (Test-Path (Join-Path $links 'Programs/Codex.lnk'))) 'installer writes no menu shortcut'
    function Check-InstalledLinks {
        $link = $shell.CreateShortcut((Join-Path $links 'Startup/Codex 用量自动启动.lnk'))
        Check ($link.TargetPath -eq (Join-Path $FixtureRoot 'CodexRelayUsage.exe')) 'console-free executable target'
        Check ($link.Arguments -eq '') 'login starts actual overlay directly without watcher'
        Check ($link.WorkingDirectory -eq $FixtureRoot) 'correct package directory'
    }
    Check-InstalledLinks
    & $installer -ShortcutRoot $links -NoStart
    Check ((Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash -eq $originalHash) 'reinstall keeps the user desktop entry'
    foreach ($arguments in @('-NoProfile -File "C:\old\scripts\Watch-Codex.ps1"', '-NoProfile -File C:\old\scripts\Watch-Codex.ps1', '--watch-codex')) {
        $legacy = $shell.CreateShortcut((Join-Path $links 'Startup/Codex 用量自动启动.lnk'))
        $legacy.TargetPath = if ($arguments -eq '--watch-codex') { Join-Path $FixtureRoot 'CodexRelayUsage.exe' } else { "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" }
        $legacy.Arguments = $arguments; $legacy.Description = 'Codex 与会话用量自动启动'; $legacy.Save()
        & $installer -ShortcutRoot $links -NoStart
        Check-InstalledLinks
        Check (-not (Test-Path (Join-Path $links 'State/startup.lnk'))) 'owned legacy watcher is migrated without backing it up'
    }
    & $installer -ShortcutRoot $links -Remove
    Check ((Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash -eq $originalHash) 'removal keeps the user desktop entry'
    Check (-not (Test-Path (Join-Path $links 'Programs/Codex.lnk'))) 'remove leaves no menu shortcut'
    Check (-not (Test-Path (Join-Path $links 'Startup/Codex 用量自动启动.lnk'))) 'remove direct login shortcut'
    & $installer -ShortcutRoot $links -Remove
    Check ((Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash -eq $originalHash) 'idempotent removal keeps the user desktop entry'
    $unrelated = $shell.CreateShortcut((Join-Path $links 'Programs/Codex.lnk'))
    $unrelated.TargetPath = "$env:SystemRoot/notepad.exe"; $unrelated.Description = 'Personal shortcut'; $unrelated.Save()
    $unrelatedHash = (Get-FileHash (Join-Path $links 'Programs/Codex.lnk')).Hash
    & $installer -ShortcutRoot $links -NoStart
    Check ((Get-FileHash (Join-Path $links 'Programs/Codex.lnk')).Hash -eq $unrelatedHash) 'unrelated shortcut is not removed'
    & $installer -ShortcutRoot $links -Remove
    Check ((Get-FileHash (Join-Path $links 'Programs/Codex.lnk')).Hash -eq $unrelatedHash) 'removal also keeps unrelated shortcuts'
    foreach ($arguments in @('--launch-codex', '--watch-codex')) {
        $stale = $shell.CreateShortcut((Join-Path $links 'Desktop/Codex.lnk'))
        $stale.TargetPath = (Join-Path $FixtureRoot 'CodexRelayUsage.exe'); $stale.Arguments = $arguments
        $stale.Description = 'Codex 与会话用量自动启动'; $stale.Save()
        & $installer -ShortcutRoot $links -NoStart
        Check (-not (Test-Path (Join-Path $links 'Desktop/Codex.lnk'))) 'settings clean up the old desktop shortcut'
    }
    $backup = $shell.CreateShortcut((Join-Path $links 'State/desktop.lnk'))
    $backup.TargetPath = "$env:SystemRoot/notepad.exe"; $backup.Save()
    $backupHash = (Get-FileHash (Join-Path $links 'State/desktop.lnk')).Hash
    $stale = $shell.CreateShortcut((Join-Path $links 'Desktop/Codex.lnk'))
    $stale.TargetPath = (Join-Path $FixtureRoot 'CodexRelayUsage.exe'); $stale.Arguments = '--launch-codex'
    $stale.Description = 'Codex 与会话用量自动启动'; $stale.Save()
    & $installer -ShortcutRoot $links -Remove
    Check ((Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash -eq $backupHash) 'old entry removal restores the backed-up shortcut'
    Check (-not (Test-Path (Join-Path $links 'State/desktop.lnk'))) 'restored backup is consumed'
    if ($OutputPath) { @{ Passed = $checks; Failed = 0 } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    Write-Output "$checks startup checks passed (Windows PowerShell 5)"
} finally {
    $resolved = [IO.Path]::GetFullPath($links)
    if ($resolved.StartsWith($FixtureRoot.TrimEnd([char]92, [char]47) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}