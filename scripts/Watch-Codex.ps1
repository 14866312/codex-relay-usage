param([switch]$Once)
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path -Parent $PSScriptRoot
$overlay = Join-Path $releaseRoot 'CodexRelayUsage.exe'
if (-not (Test-Path -LiteralPath $overlay)) { throw 'Overlay executable missing.' }
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\CodexRelayUsage.StartupWatcher', [ref]$created)
if (-not $created) { $mutex.Dispose(); exit }
try {
    $launchedFor = $null
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
                    }
                    $launchedFor = $identity
                }
            } else { $launchedFor = $null }
        } catch {
            # A disappearing process or a transient launch failure is retried next tick.
            $launchedFor = $null
        }
        if (-not $Once) { Start-Sleep -Milliseconds 1000 }
    } while (-not $Once)
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
