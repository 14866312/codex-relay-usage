param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $env:TEMP ('CodexRelayUsage-console-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory (Join-Path $fixture 'scripts') -Force | Out-Null
try {
    Copy-Item (Join-Path $PackageRoot 'CodexRelayUsage.exe') $fixture
    $probe = @'
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class ConsoleProbe { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); }'
[IO.File]::WriteAllText((Join-Path $PSScriptRoot '../console-handle.txt'),[ConsoleProbe]::GetConsoleWindow().ToInt64().ToString())
'@
    foreach ($mode in @('watch', 'launch')) {
        $name = if ($mode -eq 'watch') { 'Watch-Codex.ps1' } else { 'Start-CodexAuto.ps1' }
        [IO.File]::WriteAllText((Join-Path $fixture ('scripts/' + $name)), $probe)
        $process = Start-Process (Join-Path $fixture 'CodexRelayUsage.exe') -ArgumentList ('--' + $mode + '-codex') -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Startup host timed out' }
        if ($process.ExitCode -ne 0 -or (Get-Content (Join-Path $fixture 'console-handle.txt')) -ne '0') { throw 'Startup child created a console' }
        Remove-Item -LiteralPath (Join-Path $fixture 'console-handle.txt')
    }
    Write-Output 'PASS: launcher and watcher children have no console window.'
} finally {
    if ([IO.Path]::GetFullPath($fixture).StartsWith([IO.Path]::GetFullPath($env:TEMP) + [IO.Path]::DirectorySeparatorChar)) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
