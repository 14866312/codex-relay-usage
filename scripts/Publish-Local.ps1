param(
    [string]$DotnetPath = "dotnet",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src/CodexTokenOverlay/CodexTokenOverlay.csproj"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryRoot "artifacts" }
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$project.Project.PropertyGroup.Version
$binaryName = "CodexRelayUsage-$version-win-x64"
$binaryDirectory = Join-Path $outputRoot $binaryName
New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null

Push-Location -LiteralPath $repositoryRoot
try {
    & $DotnetPath publish $projectPath -c Release -r win-x64 --self-contained true -o $binaryDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw "发布构建失败。" }
} finally { Pop-Location }

foreach ($name in @("README.zh-CN.md", "LICENSE", "UPSTREAM.md", "ACCEPTANCE.zh-CN.md", "CHANGELOG.md")) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination $binaryDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot "third_party") -Destination $binaryDirectory -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $binaryDirectory "scripts") -Force | Out-Null
foreach ($launcher in @("Start-CodexAuto.ps1", "Install-CodexStartup.ps1", "Launch-Package.vbs", "Install-Startup-Package.vbs")) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $launcher) -Destination (Join-Path $binaryDirectory "scripts") -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Launch-Package.vbs") -Destination (Join-Path $binaryDirectory "启动 Codex 自动用量.vbs") -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Install-Startup-Package.vbs") -Destination (Join-Path $binaryDirectory "设置开机自动启动.vbs") -Force
& (Join-Path $PSScriptRoot "Verify.ps1") -ExecutablePath (Join-Path $binaryDirectory "CodexRelayUsage.exe") -OutputDirectory (Join-Path $binaryDirectory "verification")
$previewDirectory = Join-Path $repositoryRoot "previews"
$previewProcess = Start-Process -FilePath (Join-Path $binaryDirectory "CodexRelayUsage.exe") -ArgumentList @("--render-preview", ('"' + $previewDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
if ($previewProcess.ExitCode -ne 0) { throw "生成预览失败。" }
$costUiProcess = Start-Process -FilePath (Join-Path $binaryDirectory "CodexRelayUsage.exe") -ArgumentList @("--cost-ui-probe", ('"' + (Join-Path $binaryDirectory "verification/cost-ui-previews.json") + '"'), ('"' + $previewDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
if ($costUiProcess.ExitCode -ne 0) { throw "生成费用窗口预览失败。" }
$feedbackUiProcess = Start-Process -FilePath (Join-Path $binaryDirectory "CodexRelayUsage.exe") -ArgumentList @("--feedback-ui-probe", ('"' + (Join-Path $binaryDirectory "verification/feedback-ui-previews.json") + '"'), ('"' + $previewDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
if ($feedbackUiProcess.ExitCode -ne 0) { throw "生成工具栏交互预览失败。" }
Copy-Item -LiteralPath $previewDirectory -Destination $binaryDirectory -Recurse -Force
$exeHash = (Get-FileHash -LiteralPath (Join-Path $binaryDirectory "CodexRelayUsage.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $binaryDirectory "SHA256SUMS.txt"), "$exeHash  CodexRelayUsage.exe" + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
$binaryArchive = Join-Path $outputRoot ($binaryName + ".zip")
Compress-Archive -LiteralPath $binaryDirectory -DestinationPath $binaryArchive -CompressionLevel Optimal -Force

# Allow-list source entries: never package bin/obj, the SDK, settings, or real conversation logs.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceName = "CodexRelayUsage-$version-source"
$sourceArchive = Join-Path $outputRoot ($sourceName + ".zip")
$stream = [System.IO.File]::Open($sourceArchive, [System.IO.FileMode]::Create)
$zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $sourceFiles = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    foreach ($name in @(".gitignore", "global.json", "README.md", "README.zh-CN.md", "LICENSE", "UPSTREAM.md", "ACCEPTANCE.zh-CN.md", "CHANGELOG.md")) {
        $sourceFiles.Add((Get-Item -LiteralPath (Join-Path $repositoryRoot $name) -Force))
    }
    foreach ($folder in @("src", "scripts", "tests", "third_party", "previews")) {
        foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $repositoryRoot $folder) -Recurse -File)) {
            $relative = $file.FullName.Substring($repositoryRoot.Length + 1).Replace([char]92, [char]47)
            if ($relative -match '(^|/)(bin|obj)(/|$)') { continue }
            if ($file.Name -match '^(prices|settings)\.json($|\.)' -or $file.Extension -eq '.jsonl') { continue }
            $sourceFiles.Add($file)
        }
    }
    foreach ($file in $sourceFiles) {
        $relative = $file.FullName.Substring($repositoryRoot.Length + 1).Replace([char]92, [char]47)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "$sourceName/$relative", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose(); $stream.Dispose() }

$hashLines = foreach ($archive in @($binaryArchive, $sourceArchive)) {
    $line = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + [System.IO.Path]::GetFileName($archive)
    [System.IO.File]::WriteAllText(($archive + ".sha256"), $line + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    $line
}
[System.IO.File]::WriteAllText((Join-Path $outputRoot "SHA256SUMS.txt"), ($hashLines -join [Environment]::NewLine) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "发布完成：$binaryArchive"
Write-Host "源码：$sourceArchive"
