<#
.SYNOPSIS
    构建期能力探测器：从 ffmpeg 二进制提取编码器/滤镜/容器的完整参数元数据，
    烘焙成 capabilities.json 内嵌进程序。运行时不再需要探测。

.DESCRIPTION
    产物分两部分：
      - 索引部分（encoders 的名字/类型/codec/像素格式，formats 列表）：启动时加载
      - 选项部分（每个编码器的完整 AVOptions）：按需加载

    本脚本只在两种情况下运行：
      1. 首次构建
      2. 升级内置 ffmpeg 时

.EXAMPLE
    pwsh ./dump-capabilities.ps1
    pwsh ./dump-capabilities.ps1 -Ffmpeg "C:\ffmpeg\bin\ffmpeg.exe"
#>
[CmdletBinding()]
param(
    [string]$Ffmpeg = "ffmpeg",
    [string]$Out = (Join-Path $PSScriptRoot "..\src\Data\capabilities.json")
)

$ErrorActionPreference = "Stop"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Ffmpeg {
    param([string[]]$Arguments)
    # ffmpeg 把 help 输出写到 stderr，统一合并
    return (& $Ffmpeg @Arguments 2>&1 | ForEach-Object { $_.ToString() })
}

# ---------- 1. 版本 ----------
Write-Host "[1/4] 读取 ffmpeg 版本 ..." -ForegroundColor Cyan
$verLine = (Invoke-Ffmpeg -Arguments @('-hide_banner', '-version') | Select-Object -First 1)
if ($verLine -notmatch 'ffmpeg version (\S+)') { throw "无法解析 ffmpeg 版本：$verLine" }
$ffmpegVersion = $matches[1]
$buildDate = if ($verLine -match 'built with') { 'unknown' } else { 'unknown' }
Write-Host "      $ffmpegVersion"

# ---------- 2. 编码器索引 ----------
Write-Host "[2/4] 解析编码器列表 ..." -ForegroundColor Cyan
$encLines = Invoke-Ffmpeg -Arguments @('-hide_banner', '-encoders')
$encoders = New-Object System.Collections.Generic.List[object]

foreach ($line in $encLines) {
    # 形如： V....D libx264   libx264 H.264 / AVC ... (codec h264)
    if ($line -match '^\s([VAS])([F.])([S.])([X.])([B.])([D.])\s+(\S+)\s+(.*)$') {
        # 关键：$matches 是自动变量，后面任何一次 -match 都会覆盖它，
        # 所以必须在进入分支时立刻把每个捕获组取出来存到局部变量。
        $kind = $matches[1]
        $typeFlags = "$($matches[1])$($matches[2])$($matches[3])$($matches[4])$($matches[5])$($matches[6])"
        $name = $matches[7]
        $desc = $matches[8].Trim()

        # 图例行 " V..... = Video" 也会命中上面的正则，名字会变成 "="，必须剔除
        if ($name -eq '=') { continue }

        $codec = ''
        if ($desc -match '\(codec (\S+)\)') {
            $codec = $matches[1]
            $desc = ($desc -replace '\s*\(codec \S+\)\s*$', '').Trim()
        }
        $encoders.Add([pscustomobject]@{
            name  = $name
            kind  = $kind                # V=视频 A=音频 S=字幕
            flags = $typeFlags
            codec = $codec
            desc  = $desc
            pixelFormats = @()
            options = @()
        })
    }
}
Write-Host "      找到 $($encoders.Count) 个编码器"

# ---------- 3. 逐个抓取 AVOptions ----------
Write-Host "[3/4] 抓取每个编码器的 AVOptions（这一步最慢）..." -ForegroundColor Cyan
$i = 0
foreach ($enc in $encoders) {
    $i++
    if ($i % 20 -eq 0) { Write-Host "      $i / $($encoders.Count)" }

    $help = Invoke-Ffmpeg -Arguments @('-hide_banner', '-h', "encoder=$($enc.name)")

    $opts = New-Object System.Collections.Generic.List[object]
    $current = $null

    foreach ($line in $help) {
        # 像素格式
        if ($line -match '^\s+Supported pixel formats:\s+(.+)$') {
            $enc.pixelFormats = @($matches[1].Trim() -split '\s+')
            continue
        }

        # 一个选项：   -crf   <float>   E..V....... 说明 (from -1 to FLT_MAX) (default -1)
        if ($line -match '^\s{2}-(\S+)\s+<(\w+)>\s+(\S+)\s+(.*)$') {
            $flag = $matches[1]
            $type = $matches[2]
            $flags = $matches[3]
            $descRaw = $matches[4]

            $range = $null
            $default = $null
            if ($descRaw -match '\(from (\S+) to (\S+)\)') { $range = "$($matches[1])..$($matches[2])" }
            if ($descRaw -match '\(default ([^)]*)\)') { $default = $matches[1] }
            $cleanDesc = ($descRaw -replace '\s*\(from [^)]*\)', '' -replace '\s*\(default [^)]*\)', '').Trim()

            $current = [pscustomobject]@{
                flag    = "-$flag"
                name    = $flag
                type    = $type
                flags   = $flags
                range   = $range
                default = $default
                desc    = $cleanDesc
                enum    = $null
            }
            $opts.Add($current)
            continue
        }

        # 枚举取值：      none   0   E..V....... 说明
        if ($line -match '^\s{4,}(\S+)\s+(-?\d+)\s+E\.\.V\S*\s*(.*)$' -and $null -ne $current) {
            if ($null -eq $current.enum) {
                $current.enum = New-Object System.Collections.Generic.List[object]
            }
            $current.enum.Add([pscustomobject]@{
                value = $matches[1]
                num   = [long]$matches[2]
                desc  = $matches[3].Trim()
            })
            continue
        }
    }

    $enc.options = $opts.ToArray()
}

# ---------- 4. 容器格式 ----------
Write-Host "[4/4] 解析容器格式 ..." -ForegroundColor Cyan
$fmtLines = Invoke-Ffmpeg -Arguments @('-hide_banner', '-formats')
$formats = New-Object System.Collections.Generic.List[object]
foreach ($line in $fmtLines) {
    # 形如：  DE mp4    MP4 (MPEG-4 Part 14)
    if ($line -match '^\s([D ])([E ])\s+(\S+)\s+(.*)$') {
        $formats.Add([pscustomobject]@{
            name = $matches[3]
            demux = ($matches[1] -eq 'D')
            mux   = ($matches[2] -eq 'E')
            desc  = $matches[4].Trim()
        })
    }
}
Write-Host "      找到 $($formats.Count) 个格式"

# ---------- 汇总输出 ----------
$doc = [pscustomobject]@{
    schemaVersion = 1
    generatedAt   = (Get-Date).ToString("o")
    generatedBy   = "tools/dump-capabilities.ps1"
    ffmpeg        = [pscustomobject]@{
        version = $ffmpegVersion
        raw     = $verLine
    }
    counts        = [pscustomobject]@{
        encoders = $encoders.Count
        formats  = $formats.Count
    }
    encoders      = $encoders.ToArray()
    formats       = $formats.ToArray()
}

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$json = $doc | ConvertTo-Json -Depth 8 -Compress:$false
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))

$sw.Stop()
$sizeMB = [math]::Round((Get-Item $Out).Length / 1MB, 2)
$optTotal = ($encoders | ForEach-Object { $_.options.Count } | Measure-Object -Sum).Sum
Write-Host ""
Write-Host "完成：$Out" -ForegroundColor Green
Write-Host "  $($encoders.Count) 个编码器 / $($formats.Count) 个格式 / $optTotal 个选项"
Write-Host "  $sizeMB MB，耗时 $([math]::Round($sw.Elapsed.TotalSeconds,1)) 秒"
