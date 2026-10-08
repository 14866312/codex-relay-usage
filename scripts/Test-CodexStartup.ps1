param([string]$FixtureRoot)
$ErrorActionPreference='Stop'
$checks=0
function Check($condition,$name) { if(-not $condition){throw $name}; $script:checks++ }
foreach($script in Get-ChildItem "$FixtureRoot/scripts" -Filter '*.ps1') {
 $tokens=$null; $errors=$null
 [System.Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$errors) | Out-Null
 Check ($errors.Count -eq 0) ('PS5 parse '+$script.Name)
}
$FixtureRoot=[IO.Path]::GetFullPath($FixtureRoot)
$links=Join-Path $FixtureRoot ('isolated-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory "$links/Desktop" -Force | Out-Null
$shell=New-Object -ComObject WScript.Shell
$old=$shell.CreateShortcut("$links/Desktop/Codex.lnk")
$old.TargetPath="$env:SystemRoot/notepad.exe"; $old.Save()
$originalHash=(Get-FileHash "$links/Desktop/Codex.lnk").Hash
& "$FixtureRoot/scripts/Install-CodexStartup.ps1" -ShortcutRoot $links -NoStart
Check (Test-Path "$links/State/desktop.lnk") 'original backup'
foreach($relative in @('Desktop/Codex.lnk','Programs/Codex.lnk','Startup/Codex 用量自动启动.lnk')) {
 $link=$shell.CreateShortcut((Join-Path $links $relative))
 Check ($link.TargetPath -eq (Join-Path $FixtureRoot 'CodexRelayUsage.exe')) 'console-free executable target'
 Check ($link.Arguments -in @('--launch-codex','--watch-codex')) 'GUI startup mode'
 Check ($link.WorkingDirectory -eq $FixtureRoot) 'correct package'
}
& "$FixtureRoot/scripts/Install-CodexStartup.ps1" -ShortcutRoot $links -NoStart
Check ((Get-FileHash "$links/State/desktop.lnk").Hash -eq $originalHash) 'reinstall retains original backup'
& "$FixtureRoot/scripts/Install-CodexStartup.ps1" -ShortcutRoot $links -Remove
Check ((Get-FileHash "$links/Desktop/Codex.lnk").Hash -eq $originalHash) 'restore original exactly'
Check (-not (Test-Path "$links/Programs/Codex.lnk")) 'remove generated menu'
Check (-not (Test-Path "$links/Startup/Codex 用量自动启动.lnk")) 'remove startup'
& "$FixtureRoot/scripts/Install-CodexStartup.ps1" -ShortcutRoot $links -Remove
Check ((Get-FileHash "$links/Desktop/Codex.lnk").Hash -eq $originalHash) 'idempotent removal preserves original'
Write-Output "$checks startup checks passed (Windows PowerShell 5)"
