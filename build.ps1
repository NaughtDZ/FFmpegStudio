<#
.SYNOPSIS
    构建 / 发布 FFmpeg Studio。

.DESCRIPTION
    发布产物是「自包含 + 内置 ffmpeg」的便携目录：
      dist\FFmpegStudio.exe        自包含单文件（无需安装 .NET）
      dist\ffmpeg\                 内置 ffmpeg / ffprobe 及其依赖 DLL
    整个 dist 目录拷走即可用，双击 exe 就能跑。

    内置的是 shared build，所以 ffmpeg.exe / ffprobe.exe 必须和它们的 DLL 放在同一目录里。

.PARAMETER Publish
    清理并发布到 dist（默认只编译到 src\bin\Release）。

.PARAMETER FfmpegDir
    指定 ffmpeg 所在目录；不指定时从 PATH 里的 ffmpeg 推断。

.EXAMPLE
    pwsh ./build.ps1 -Publish
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [string]$FfmpegDir
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$proj = Join-Path $root "src\FfmpegStudio.vbproj"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

if (-not $Publish) {
    Write-Host "== 编译（不发布）==" -ForegroundColor Cyan
    dotnet build $proj -c Release
    exit $LASTEXITCODE
}

# ---------- 1. 清理 ----------
Write-Host "[1/4] 清理 dist ..." -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# ---------- 2. 发布单文件 ----------
Write-Host "[2/4] 发布自包含单文件（这一步最慢）..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=zh-Hans `
    -o $dist

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# ---------- 3. 内置 ffmpeg ----------
Write-Host "[3/4] 打包内置 ffmpeg ..." -ForegroundColor Cyan

if ([string]::IsNullOrEmpty($FfmpegDir)) {
    $ffCmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (-not $ffCmd) { throw "找不到 ffmpeg，请用 -FfmpegDir 指定目录" }
    $FfmpegDir = Split-Path $ffCmd.Source
}

$ffDst = Join-Path $dist "ffmpeg"
New-Item -ItemType Directory -Path $ffDst -Force | Out-Null

# shared build 的 exe 依赖同目录的 av*.dll，必须一起带上。
# ffplay.exe 不需要（我们不做播放）。
$needed = @("ffmpeg.exe", "ffprobe.exe") +
          (Get-ChildItem $FfmpegDir -Filter "*.dll" | Select-Object -ExpandProperty Name)

foreach ($f in $needed) {
    $src = Join-Path $FfmpegDir $f
    if (Test-Path $src) {
        Copy-Item $src $ffDst -Force
    } else {
        Write-Warning "缺少 $f"
    }
}

# 记录内置的是哪个版本，便于以后核对能力表
$ver = (& (Join-Path $ffDst "ffmpeg.exe") -hide_banner -version 2>&1 | Select-Object -First 1)
Set-Content -Path (Join-Path $ffDst "VERSION.txt") -Value $ver -Encoding UTF8

# ---------- 4. 报告 ----------
Write-Host "[4/4] 完成" -ForegroundColor Cyan
$sw.Stop()

$exe = Get-Item (Join-Path $dist "FFmpegStudio.exe")
$ffSize = (Get-ChildItem $ffDst -File | Measure-Object Length -Sum).Sum
$total = (Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host ""
Write-Host ("  FFmpegStudio.exe  {0,8:N1} MB" -f ($exe.Length / 1MB))
Write-Host ("  ffmpeg\           {0,8:N1} MB（{1} 个文件）" -f ($ffSize / 1MB), (Get-ChildItem $ffDst -File).Count)
Write-Host ("  合计              {0,8:N1} MB" -f ($total / 1MB))
Write-Host ("  耗时              {0,8:N1} 秒" -f $sw.Elapsed.TotalSeconds)
Write-Host ""
Write-Host "  产物目录：$dist" -ForegroundColor Green
Write-Host "  $ver" -ForegroundColor DarkGray
