param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/verification"
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

function Invoke-Probe {
    param([string[]]$Arguments)
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    # These arguments are flags and Windows paths, which cannot contain a double quote.
    $start.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw "探针超时：$($Arguments[0])"
        }
        if ($process.ExitCode -ne 0) {
            throw "探针失败：$($Arguments[0])，退出码 $($process.ExitCode)"
        }
    } finally { $process.Dispose() }
}

& (Join-Path $PSScriptRoot "Test-PeArchitecture.ps1") -ExecutablePath $executable -Architecture x64
$selfTestPath = Join-Path $outputRoot "self-test.json"
Invoke-Probe -Arguments @("--self-test", $selfTestPath)
$selfTest = Get-Content -LiteralPath $selfTestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($selfTest.Failed -ne 0) { throw "合成日志测试未全部通过，请查看 $selfTestPath" }

$nativePath = Join-Path $outputRoot "native-result.json"
$fixture = Join-Path $repositoryRoot "tests/fixtures/FormProbe.json"
Invoke-Probe -Arguments @("--form-probe", $nativePath, $fixture)
$nativeResult = Get-Content -LiteralPath $nativePath -Raw -Encoding UTF8 | ConvertFrom-Json
$c = $nativeResult.Cases[0]
$checks = [System.Collections.Generic.List[object]]::new()
function Check {
    param([string]$Name, [bool]$Condition)
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Condition })
}
Check "tool window" $c.WsExToolWindowPresent
Check "normal window does not activate" $c.WsExNoActivatePresent
Check "normal mouse click preserves input focus" ($c.MouseActivateResult -eq 3)
Check "native shadow" $c.CsDropShadowPresent
Check "expanded region matches capsule plus panel" $c.ExpandedRegionMatchesUnion
Check "capsule click delivered once" ($c.NormalCapsuleClickCount -eq 1)
Check "normal mode does not intercept Enter" (-not $c.NormalCommandIntercepted)
Check "edit requires collapsed mode" $c.BeginEditRejectsExpanded
Check "edit mode permits intentional focus" (-not $c.EditWsExNoActivatePresent -and $c.EditMouseActivateResult -eq 1)
Check "edit drag does not open panel" ($c.EditCapsuleClickCount -eq 0)
Check "move preserves size" $c.MovePreservedSize
Check "minimum scale 60 percent" ($c.MinimumResizePreview.ScalePercent -eq 60)
Check "maximum scale 130 percent" ($c.MaximumResizePreview.ScalePercent -eq 130)
Check "capture loss finishes move once" ($c.LostMoveCapture.CompletionCount -eq 1 -and -not $c.LostMoveCapture.ActiveAfterRepeatedSignals)
Check "capture loss finishes resize once" ($c.LostResizeCapture.CompletionCount -eq 1 -and -not $c.LostResizeCapture.ActiveAfterRepeatedSignals)
Check "cancel releases mouse capture" ($c.CancelCapture.CancelRequestCount -eq 1 -and -not $c.CancelCapture.CaptureAfterInterruption)
Check "save and cancel are delivered once" ($c.SaveRequestCount -eq 1 -and $c.CancelRequestCount -eq 1)
Check "normal no-activate restored after editing" ($c.RestoredWsExNoActivatePresent -and $c.RestoredMouseActivateResult -eq 3)
Check "attachment highlight is click-through" ($c.HighlightWsExTransparentPresent -and $c.HighlightHitTest -eq -1)
Check "attachment highlight clears after editing" ($c.HighlightHiddenAfterClear -and $c.HighlightRegionCleared)

$failed = @($checks | Where-Object { -not $_.Passed }).Count
$summary = [pscustomobject]@{
    SyntheticPassed = $selfTest.Passed
    SyntheticFailed = $selfTest.Failed
    NativePassed = $checks.Count - $failed
    NativeFailed = $failed
    PeArchitecture = "x64"
    Checks = $checks.ToArray()
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "verification.json") -Encoding UTF8
if ($failed -gt 0) { throw "原生窗口测试失败，请查看 $outputRoot" }
Write-Host "验证通过：$($selfTest.Passed) 项合成测试、$($checks.Count) 项原生窗口检查、x64 PE。"
