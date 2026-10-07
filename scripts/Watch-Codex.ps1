param([switch]$Once)
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$overlay = Join-Path $releaseRoot 'CodexRelayUsage.exe'
if (-not (Test-Path -LiteralPath $overlay)) { throw 'Overlay executable missing.' }
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\CodexRelayUsage.StartupWatcher', [ref]$created)
if (-not $created) { $mutex.Dispose(); exit }
$statusRoot = Join-Path $env:LOCALAPPDATA 'CodexRelayUsage/startup'
$statusPath = Join-Path $statusRoot 'watcher-status.json'
function Save-WatcherStatus([string]$state, [string]$message = '') {
    try {
        New-Item -ItemType Directory -Path $statusRoot -Force | Out-Null
        $status = [ordered]@{ version = 1; pid = $PID; updatedUtc = [DateTime]::UtcNow.ToString('o'); state = $state; message = $message }
        $temporary = $statusPath + '.' + $PID + '.tmp'
        [IO.File]::WriteAllText($temporary, ($status | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $statusPath -Force
    } catch { } # Diagnostic persistence must not prevent startup.
}
try {
    Save-WatcherStatus 'running'
    $launchedFor = $null
    $heartbeat = [DateTime]::UtcNow
    do {
        try {
            $app = Get-Process ChatGPT -ErrorAction SilentlyContinue | Where-Object {
                $_.MainWindowHandle -ne 0 -and $_.Path -like '*\OpenAI.Codex_*\app\ChatGPT.exe'
            } | Sort-Object Id | Select-Object -First 1
            if ($app) {
                $identity = '{0}:{1}' -f $app.Id, $app.StartTime.ToUniversalTime().Ticks
                if ($identity -ne $launchedFor) {
                    $existing = Get-Process CodexRelayUsage -ErrorAction SilentlyContinue
                    if (-not $existing) {
                        Start-Process -FilePath $overlay -WorkingDirectory $releaseRoot -WindowStyle Hidden
                        Save-WatcherStatus 'overlay-started'
                    }
                    $launchedFor = $identity
                }
            } else { $launchedFor = $null }
        } catch {
            # A disappearing process or a transient launch failure is retried next tick.
            $launchedFor = $null
            Save-WatcherStatus 'retrying' $_.Exception.Message
        }
        if (([DateTime]::UtcNow - $heartbeat).TotalSeconds -ge 15) {
            Save-WatcherStatus $(if ($app) { 'watching-codex' } else { 'waiting-for-codex' })
            $heartbeat = [DateTime]::UtcNow
        }
        if (-not $Once) { Start-Sleep -Milliseconds 1000 }
    } while (-not $Once)
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
