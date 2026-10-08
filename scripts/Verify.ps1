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
$packageRoot = Split-Path -Parent $executable
$startupPath = Join-Path $outputRoot "startup.json"
$consolePath = Join-Path $outputRoot "console-free.json"
$windowsPowerShell = Join-Path $env:SystemRoot "System32/WindowsPowerShell/v1.0/powershell.exe"
& $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Test-CodexStartup.ps1") -FixtureRoot $packageRoot -OutputPath $startupPath
if ($LASTEXITCODE -ne 0) { throw "开机启动迁移检查失败。" }
& $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Test-ConsoleFreeStartup.ps1") -PackageRoot $packageRoot -OutputPath $consolePath
if ($LASTEXITCODE -ne 0) { throw "无控制台入口检查失败。" }
$startup = Get-Content -LiteralPath $startupPath -Raw -Encoding UTF8 | ConvertFrom-Json
$console = Get-Content -LiteralPath $consolePath -Raw -Encoding UTF8 | ConvertFrom-Json
$selfTestPath = Join-Path $outputRoot "self-test.json"
Invoke-Probe -Arguments @("--self-test", $selfTestPath)
$selfTest = Get-Content -LiteralPath $selfTestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($selfTest.Failed -ne 0) { throw "合成日志测试未全部通过，请查看 $selfTestPath" }
$costUiPath = Join-Path $outputRoot "cost-ui.json"
Invoke-Probe -Arguments @("--cost-ui-probe", $costUiPath)
$costUi = Get-Content -LiteralPath $costUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($costUi.Failed -ne 0) { throw "费用窗口检查失败，请查看 $costUiPath" }
$feedbackUiPath = Join-Path $outputRoot "feedback-ui.json"
Invoke-Probe -Arguments @("--feedback-ui-probe", $feedbackUiPath)
$feedbackUi = Get-Content -LiteralPath $feedbackUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($feedbackUi.Failed -ne 0) { throw "工具栏交互检查失败，请查看 $feedbackUiPath" }
$liveUiPath = Join-Path $outputRoot "live-ui.json"
Invoke-Probe -Arguments @("--live-ui-probe", $liveUiPath)
$liveUi = Get-Content -LiteralPath $liveUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($liveUi.Failed -ne 0) { throw "实时更新及窗口跟随检查失败，请查看 $liveUiPath" }

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
Check "toolbar has no heavy native shadow" (-not $c.CsDropShadowPresent)
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
    StartupPassed = $startup.Passed
    ConsoleFreePassed = $console.Passed
    SyntheticFailed = $selfTest.Failed
    NativePassed = $checks.Count - $failed
    NativeFailed = $failed
    CostUiPassed = $costUi.Passed
    CostUiFailed = $costUi.Failed
    FeedbackUiPassed = $feedbackUi.Passed
    FeedbackUiFailed = $feedbackUi.Failed
    LiveUiPassed = $liveUi.Passed
    LiveUiFailed = $liveUi.Failed
    LogAppendToPublication = $liveUi.LogAppendToPublication
    HostMoveToOverlay = $liveUi.HostMoveToOverlay
    PeArchitecture = "x64"
    Checks = $checks.ToArray()
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot "verification.json") -Encoding UTF8
if ($failed -gt 0) { throw "原生窗口测试失败，请查看 $outputRoot" }
Write-Host "验证通过：$($selfTest.Passed) 项合成测试、$($checks.Count) 项原生窗口检查、$($costUi.Passed) 项费用窗口检查、$($feedbackUi.Passed) 项工具栏交互检查、$($liveUi.Passed) 项实时更新及跟随检查、$($startup.Passed) 项启动设置检查、$($console.Passed) 项无控制台检查、x64 GUI PE。"
