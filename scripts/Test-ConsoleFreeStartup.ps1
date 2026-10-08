param([Parameter(Mandatory=$true)][string]$PackageRoot, [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$fixture = Join-Path $env:TEMP ('CodexRelayUsage-console-' + [Guid]::NewGuid().ToString('N'))
$checks = 0
New-Item -ItemType Directory (Join-Path $fixture 'scripts') -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $PackageRoot 'CodexRelayUsage.exe') -Destination $fixture
    # Literal source: keep the child script's PSScriptRoot.
    $probe = 'Add-Type -TypeDefinition ''using System; using System.Runtime.InteropServices; public class ConsoleProbe { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); }''' + [Environment]::NewLine +
        '[IO.File]::WriteAllText((Join-Path $PSScriptRoot ''../console-handle.txt''),[ConsoleProbe]::GetConsoleWindow().ToInt64().ToString())'
    foreach ($scriptName in @('Start-CodexAuto.ps1', 'Install-CodexStartup.ps1')) {
        [IO.File]::WriteAllText((Join-Path $fixture ('scripts/' + $scriptName)), $probe)
    }
    $handleFile = Join-Path $fixture 'console-handle.txt'
    foreach ($entry in @('Launch-Package.vbs', 'Install-Startup-Package.vbs')) {
        $source = Get-Content -LiteralPath (Join-Path $PackageRoot ('scripts/' + $entry)) -Raw
        $source = $source.Replace(' --install-startup', ' --install-startup --quiet')
        $entryPath = Join-Path $fixture $entry
        [IO.File]::WriteAllText($entryPath, $source, [Text.Encoding]::ASCII)
        $process = Start-Process -FilePath "$env:SystemRoot/System32/wscript.exe" -ArgumentList @('//B', '//NoLogo', ('"' + $entryPath + '"')) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'VBS entry timed out' }
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $handleFile) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $handleFile) -or (Get-Content -LiteralPath $handleFile) -ne '0') { throw ($entry + ' created a console or did not execute the native host') }
        $process.Dispose(); $checks++
        Remove-Item -LiteralPath $handleFile
    }
    $report = Join-Path $fixture 'legacy-self-test.json'
    $process = Start-Process -FilePath (Join-Path $fixture 'CodexRelayUsage.exe') -ArgumentList @('--watch-codex', '--self-test', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Legacy startup flag still enters a watcher host' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Legacy startup flag did not enter the real program' }
    $result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($result.Failed -ne 0) { throw 'Legacy entry diagnostics failed' }
    $process.Dispose(); $checks++
    if ($OutputPath) { @{ Passed = $checks; Failed = 0 } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
    Write-Output "$checks console-free entry checks passed."
} finally {
    Get-Process CodexRelayUsage -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $fixture 'CodexRelayUsage.exe') | ForEach-Object {
        if (-not $_.WaitForExit(2000)) { $_.Kill(); $_.WaitForExit(2000) | Out-Null }
    }
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd([char]92, [char]47) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
