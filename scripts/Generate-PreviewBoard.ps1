param([string]$PreviewDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $PreviewDirectory) { $PreviewDirectory = Join-Path $repositoryRoot 'previews' }
$PreviewDirectory = [IO.Path]::GetFullPath($PreviewDirectory)
$canvas = [Drawing.Bitmap]::new(2440, 3260)
$graphics = [Drawing.Graphics]::FromImage($canvas)
$titleFont = [Drawing.Font]::new('Microsoft YaHei UI', 34, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
$sectionFont = [Drawing.Font]::new('Microsoft YaHei UI', 24, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
$noteFont = [Drawing.Font]::new('Microsoft YaHei UI', 21, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$ink = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#1C2335'))
$muted = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#6C7790'))
$surface = [Drawing.SolidBrush]::new([Drawing.Color]::White)
$border = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#DEE4EF'))
function Draw-Card([string]$title, [int]$x, [int]$y, [int]$w, [int]$h) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $d = 24
        $path.AddArc($x, $y, $d, $d, 180, 90)
        $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $graphics.FillPath($surface, $path)
        $graphics.DrawPath($border, $path)
        $graphics.DrawString($title, $sectionFont, $ink, [single]($x + 24), [single]($y + 18))
    } finally { $path.Dispose() }
}
function Draw-Preview([string]$name, [int]$x, [int]$y, [int]$w, [int]$h) {
    $image = [Drawing.Image]::FromFile((Join-Path $PreviewDirectory $name))
    try {
        $scale = [Math]::Min($w / $image.Width, $h / $image.Height)
        $width = [int]($image.Width * $scale); $height = [int]($image.Height * $scale)
        $rectangle = [Drawing.Rectangle]::new($x + [int](($w - $width) / 2), $y, $width, $height)
        $graphics.DrawImage($image, $rectangle)
    } finally { $image.Dispose() }
}
try {
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#F2F5FB'))
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $icon = [Drawing.Image]::FromFile((Join-Path $repositoryRoot 'src/CodexTokenOverlay/Assets/app.png'))
    try { $graphics.DrawImage($icon, [Drawing.Rectangle]::new(40, 34, 96, 96)) } finally { $icon.Dispose() }
    $graphics.DrawString('Codex 用量助手', $titleFont, $ink, [single]158, [single]38)
    $graphics.DrawString('Codex Usage Assistant 1.3.0 · 实际界面预览', $noteFont, $muted, [single]160, [single]88)
    $graphics.DrawString('浅色 / 深色 · 用量与费用 · 登录启动 · 保留原配置', $noteFont, $muted, [single]1520, [single]88)
    Draw-Card '浅色 · 悬浮条与展开卡片' 40 180 720 870
    Draw-Preview 'light-expanded-cost.png' 64 252 672 770
    Draw-Card '深色 · 悬浮条与展开卡片' 800 180 720 870
    Draw-Preview 'dark-expanded-cost.png' 824 252 672 770
    Draw-Card '托盘菜单 · 浅色 / 深色' 1560 180 840 870
    Draw-Preview 'light-tray-menu.png' 1584 252 372 430
    Draw-Preview 'dark-tray-menu.png' 2004 252 372 430
    Draw-Preview 'light-appearance-menu.png' 1584 720 372 290
    Draw-Preview 'dark-appearance-menu.png' 2004 720 372 290
    Draw-Card '模型价格 · 搜索方案与精确价格录入' 40 1090 1160 870
    Draw-Preview 'model-prices.png' 64 1156 1112 780
    Draw-Card '模型价格 · 深色主题' 1240 1090 1160 870
    Draw-Preview 'model-prices-dark.png' 1264 1156 1112 780
    Draw-Card '费用明细 · 按模型与档位汇总' 40 2000 1160 665
    Draw-Preview 'cost-details.png' 64 2066 1112 570
    Draw-Card '费用明细 · 深色主题' 1240 2000 1160 665
    Draw-Preview 'cost-details-dark.png' 1264 2066 1112 570
    Draw-Card '默认 / 鼠标悬停 / 按住鼠标 / 同名对话自动识别' 40 2705 1160 470
    Draw-Preview 'light-toolbar-states.png' 64 2767 548 254
    Draw-Preview 'light-toolbar-identify.png' 628 2767 548 254
    Draw-Card '默认 / 鼠标悬停 / 按住鼠标 / 同名对话自动识别 · 深色' 1240 2705 1160 470
    Draw-Preview 'dark-toolbar-states.png' 1264 2767 548 254
    Draw-Preview 'dark-toolbar-identify.png' 1828 2767 548 254
    $graphics.DrawString('全部预览来自程序真实窗体，使用合成数据；费用金额显示三位小数。', $noteFont, $muted, [single]40, [single]3195)
    $canvas.Save((Join-Path $PreviewDirectory 'overview.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally {
    foreach ($resource in @($graphics, $canvas, $titleFont, $sectionFont, $noteFont, $ink, $muted, $surface, $border)) { $resource.Dispose() }
}
