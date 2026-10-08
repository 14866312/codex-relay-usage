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
    New-Item -ItemType Directory (Join-Path $links 'Desktop') -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $original = $shell.CreateShortcut((Join-Path $links 'Desktop/Codex.lnk'))
    $original.TargetPath = "$env:SystemRoot/notepad.exe"; $original.Save()
    $originalHash = (Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash
    $installer = Join-Path $FixtureRoot 'scripts/Install-CodexStartup.ps1'
    & $installer -ShortcutRoot $links -NoStart
    Check (Test-Path (Join-Path $links 'State/desktop.lnk')) 'original backup'
    function Check-InstalledLinks {
        foreach ($relative in @('Desktop/Codex.lnk', 'Programs/Codex.lnk', 'Startup/Codex 用量自动启动.lnk')) {
            $link = $shell.CreateShortcut((Join-Path $links $relative))
            Check ($link.TargetPath -eq (Join-Path $FixtureRoot 'CodexRelayUsage.exe')) 'console-free executable target'
            $expected = if ($relative -like 'Startup/*') { '' } else { '--launch-codex' }
            Check ($link.Arguments -eq $expected) 'login starts actual overlay directly without watcher'
            Check ($link.WorkingDirectory -eq $FixtureRoot) 'correct package directory'
        }
    }
    Check-InstalledLinks
    & $installer -ShortcutRoot $links -NoStart
    Check ((Get-FileHash (Join-Path $links 'State/desktop.lnk')).Hash -eq $originalHash) 'reinstall retains original backup'
    foreach ($arguments in @('-NoProfile -File "C:\old\scripts\Watch-Codex.ps1"', '-NoProfile -File C:\old\scripts\Watch-Codex.ps1', '--watch-codex')) {
        $legacy = $shell.CreateShortcut((Join-Path $links 'Startup/Codex 用量自动启动.lnk'))
        $legacy.TargetPath = if ($arguments -eq '--watch-codex') { Join-Path $FixtureRoot 'CodexRelayUsage.exe' } else { "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" }
        $legacy.Arguments = $arguments; $legacy.Description = 'Codex 与会话用量自动启动'; $legacy.Save()
        & $installer -ShortcutRoot $links -NoStart
        Check-InstalledLinks
        Check (-not (Test-Path (Join-Path $links 'State/startup.lnk'))) 'owned legacy watcher is migrated without backing it up'
    }
    & $installer -ShortcutRoot $links -Remove
    Check ((Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash -eq $originalHash) 'restore original exactly'
    Check (-not (Test-Path (Join-Path $links 'Programs/Codex.lnk'))) 'remove generated menu'
    Check (-not (Test-Path (Join-Path $links 'Startup/Codex 用量自动启动.lnk'))) 'remove direct login shortcut'
    & $installer -ShortcutRoot $links -Remove
    Check ((Get-FileHash (Join-Path $links 'Desktop/Codex.lnk')).Hash -eq $originalHash) 'idempotent removal preserves original'
    $unrelated = $shell.CreateShortcut((Join-Path $links 'Programs/Codex.lnk'))
    $unrelated.TargetPath = "$env:SystemRoot/notepad.exe"; $unrelated.Description = 'Personal shortcut'; $unrelated.Save()
    $unrelatedHash = (Get-FileHash (Join-Path $links 'Programs/Codex.lnk')).Hash
    & $installer -ShortcutRoot $links -Remove
    Check ((Get-FileHash (Join-Path $links 'Programs/Codex.lnk')).Hash -eq $unrelatedHash) 'unrelated shortcut is not removed'
    if ($OutputPath) { @{ Passed = $checks; Failed = 0 } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    Write-Output "$checks startup checks passed (Windows PowerShell 5)"
} finally {
    $resolved = [IO.Path]::GetFullPath($links)
    if ($resolved.StartsWith($FixtureRoot.TrimEnd([char]92, [char]47) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
