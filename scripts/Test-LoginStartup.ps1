param([Parameter(Mandatory=$true)][string]$PackageRoot, [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$exe = Join-Path $PackageRoot 'CodexRelayUsage.exe'
$taskName = 'CodexRelayUsage-Watcher-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$checks = 0
function Check($condition, $name) { if (-not $condition) { throw $name }; $script:checks++ }
function Overlay-MutexHeld {
    $mutex = $null
    if (-not [Threading.Mutex]::TryOpenExisting('Local\CodexRelayUsage', [ref]$mutex)) { return $false }
    try {
        try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if ($acquired) { $mutex.ReleaseMutex(); return $false }
        return $true
    } finally { $mutex.Dispose() }
}
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Check (-not ($task -and $task.Description -eq 'CodexRelayUsage startup watcher recovery')) 'old watcher task removed'
$linkPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex 用量自动启动.lnk'
Check (Test-Path -LiteralPath $linkPath) 'login shortcut exists'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($linkPath)
Check ($link.TargetPath -eq $exe -and $link.Arguments -eq '') 'login invokes real GUI executable directly'
$app = Get-Process ChatGPT -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -like '*\OpenAI.Codex_*\app\ChatGPT.exe' } | Select-Object -First 1
if (-not $app) { throw 'Open Codex first; this check never restarts it.' }
$codexPid = $app.Id; $codexStart = $app.StartTime
try {
    Get-Process CodexRelayUsage -ErrorAction SilentlyContinue | Where-Object Path -eq $exe | Stop-Process
    Start-Process -FilePath $linkPath -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 100; $held = Overlay-MutexHeld } while (-not $held -and [DateTime]::UtcNow -lt $deadline)
    Check $held 'real overlay singleton acquired, not just a startup wrapper'
    $overlay = @(Get-Process CodexRelayUsage -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
    Check ($overlay.Count -eq 1) 'single resident overlay process'
    $firstOverlayPid = $overlay[0].Id
    Start-Process -FilePath $linkPath -WindowStyle Hidden
    Start-Sleep -Milliseconds 700
    $overlay = @(Get-Process CodexRelayUsage -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)
    Check ($overlay.Count -eq 1 -and $overlay[0].Id -eq $firstOverlayPid -and (Overlay-MutexHeld)) 'duplicate login entry preserves singleton'
    $processes = @(Get-CimInstance Win32_Process)
    $children = @($processes | Where-Object ParentProcessId -eq $firstOverlayPid)
    Check (-not ($children | Where-Object Name -match '^(powershell|cmd|conhost|WindowsTerminal)\.exe$')) 'direct login creates no shell or terminal child'
    Check (-not ($processes | Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -match '[/\\]scripts[/\\]Watch-Codex\.ps1(?:"|\s|$)' })) 'no recurring PowerShell watcher'
    $current = Get-Process -Id $codexPid
    Check ($current.StartTime -eq $codexStart) 'current Codex process unchanged'
    if ($OutputPath) { @{ Passed = $checks; Failed = 0; TestedActualLogon = $false } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    Write-Output "$checks installed login-entry checks passed; Codex was not restarted."
} finally {
    # Always leave the tool running, even when a startup assertion fails.
    if (-not (Overlay-MutexHeld)) { Start-Process -FilePath $exe -WorkingDirectory $PackageRoot -WindowStyle Hidden }
}
