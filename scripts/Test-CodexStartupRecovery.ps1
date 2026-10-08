param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$watcher = Join-Path $PackageRoot 'scripts/Watch-Codex.ps1'
$exe = Join-Path $PackageRoot 'CodexRelayUsage.exe'
$taskName = 'CodexRelayUsage-Watcher-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) { throw 'FAIL: no recovery task; login watcher termination permanently disables Store-launch auto-start until next login.' }
if ($task.Description -ne 'CodexRelayUsage startup watcher recovery') { throw 'Unexpected task owner' }
if (-not ($task.Triggers | Where-Object { $_.Repetition.Interval -eq 'PT1M' })) { throw 'FAIL: no periodic recovery trigger' }
if ($task.Settings.ExecutionTimeLimit -ne 'PT0S') { throw 'FAIL: watcher has a runtime limit' }
if ($task.Actions.Execute -ne $exe -or $task.Actions.Arguments -ne '--watch-codex') { throw 'FAIL: wrong installed package' }
$app = Get-Process ChatGPT -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -like '*\OpenAI.Codex_*\app\ChatGPT.exe' } | Select-Object -First 1
if (-not $app) { throw 'Open Codex first; this check never restarts Codex.' }
$codexPid = $app.Id
$codexStart = $app.StartTime
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
    $_.CommandLine -like ('*"' + $watcher + '"*')
} | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
Get-Process CodexRelayUsage -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process
# Do not manually launch: the registered minute trigger must recover on its own.
$deadline = [DateTime]::UtcNow.AddSeconds(85)
do {
    Start-Sleep -Milliseconds 500
    $overlay = Get-Process CodexRelayUsage -ErrorAction SilentlyContinue | Where-Object Path -eq $exe
} while (-not $overlay -and [DateTime]::UtcNow -lt $deadline)
if (-not $overlay) { throw 'FAIL: periodic trigger did not restore overlay after watcher termination' }
$current = Get-Process -Id $codexPid -ErrorAction Stop
if ($current.StartTime -ne $codexStart) { throw 'FAIL: Codex restarted' }
Write-Output 'PASS: automatic scheduled recovery relaunched overlay; current Codex process unchanged.'
