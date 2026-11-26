# 发版流程

这份文档记录的是**实际踩过坑之后**的流程，不是理想流程。每一步下面都写明「为什么」。

## 概览

```
改版本 → 重新 publish → 打包 → 扫凭据 → 提交打 tag → 建 Release → 上传 → 回验
                                  ↑                                    ↓
                                  └────────── 回验不通过就回到第 2 步 ───┘
```

---

## 1. 改版本

`src/FfmpegStudio.vbproj` 三处一起改：

```xml
<Version>0.2.0</Version>
<AssemblyVersion>0.2.0.0</AssemblyVersion>
<FileVersion>0.2.0.0</FileVersion>
```

改完核对：

```powershell
(Get-Item "src\bin\Release\net10.0-windows\FfmpegStudio.exe").VersionInfo.FileVersion
```

---

## 2. 重新 publish —— **最容易漏的一步**

```powershell
pwsh -NoProfile -File .\build.ps1 -Publish
```

**`dotnet build` 和 `build.ps1 -Publish` 是两件事：**

- `dotnet build` 产出在 `src\bin\Release\...`
- `build.ps1 -Publish` 产出在 `dist\`，并复制 ffmpeg

改完代码只 `dotnet build`，`dist\` 还是**上一次 publish 的旧内容**。

> 这个坑真的踩过：V0.1.0 第一次上传的包里只有 9 个参数模块（当时已经做到 38 个），版本号还是默认的 `1.0.0.0`。因为改完代码只 build 没 publish。
>
> **「下载回来实跑」这一步抓到了它**——否则发给用户的就是过期构建。

publish 后必须核对两件事，**缺一不可**：

```powershell
# ① 版本号对不对
(Get-Item "dist\FfmpegStudio.exe").VersionInfo.FileVersion

# ② 新功能真的在里面（只看版本号不够——版本号改了但代码没进去的情况真发生过）
dist\FfmpegStudio.exe --no-hwprobe --add test.mp4 \
    --set video.scale mode factor --set video.scale factor 0.5 --command-out c.txt
# 检查 c.txt 里有没有新功能的痕迹（如 trunc(iw*0.5)）
```

> ② 是后补的：V0.3.1 发布前发现 dist 停留在 11:46 的构建，而按比例缩放的代码是 11:57 才写的。
> 光看版本号是 0.3.0（没变），但功能确实缺了。用真实命令跑一次才确定。

---

## 3. 打包

```powershell
$zip = Join-Path (Get-Location).Path "FFmpegStudio-v0.2.0-win-x64.zip"
Remove-Item $zip -Force -EA SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path "dist").Path, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)     # ← includeBaseDirectory 必须是 false
```

两个必须注意的点：

**① `includeBaseDirectory` 用 `$false`**
用 `$true` 的话包内会多一层 `dist\`，用户解压出来是 `dist\FfmpegStudio.exe` 而不是直接看到 exe。

**② zip 输出到 `dist\` 外面**
放到里面会「自己压自己」，报 `The process cannot access the file ... because it is being used by another process`。

打包后核对结构：

```powershell
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
$z.Entries | Where-Object { $_.FullName -notmatch '/' } | ForEach-Object { $_.FullName }
$z.Dispose()
# 期望输出：FfmpegStudio.exe（根目录直接是 exe）
```

---

## 4. 扫凭据

三处都查，缺一不可：

```powershell
# 源码（排除二进制、dist、.git、测试素材）
Get-ChildItem -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\dist\\|\\bin\\|\\obj\\|\\.git\\|_testmedia|\.png$|\.mp4$|\.dll$|\.exe$|\.zip$' } |
    Select-String -Pattern 'github_pat_|ghp_[A-Za-z0-9]{20}|password\s*=\s*["'']|secret\s*=\s*["'']' -List

# .git/config
Get-Content ".git\config" -Raw | Select-String 'github_pat_|x-access-token'

# git 历史（所有提交）
git grep -n 'github_pat_' $(git rev-list --all)
```

推送时用临时 token URL，推送后确认远程地址是干净的：

```powershell
git push -q "https://x-access-token:$token@github.com/NaughtDZ/FFmpegStudio.git" $branch
git remote -v   # 应显示不带 token 的 URL
```

---

## 5. 提交 + 打 tag

```powershell
git add -A
git commit -q -m "v0.2.0 ..."
git tag -a V0.2.0 -m "FFmpeg Studio v0.2.0 — ..."
git push -q "https://x-access-token:$token@..." master
git push -q "https://x-access-token:$token@..." V0.2.0
```

tag 用**大写 V + 语义化版本**（`V0.1.0`、`V0.2.0`）。

### 大文件别进仓库

`.gitignore` 必须有 `*.zip`。发布包 136 MB，超过 GitHub 单文件 100 MB 限制，推上去会被拒：

```
remote: error: File FFmpegStudio-v0.1.0-win-x64.zip is 136.19 MB;
        this exceeds GitHub's file size limit of 100.00 MB
```

万一误提交了：

```powershell
git reset --soft HEAD~1
git rm --cached "FFmpegStudio-v0.2.0-win-x64.zip"
# .gitignore 加 *.zip 后重新提交
git gc --prune=now --aggressive     # 清理残留对象（曾从 146 MB 降到 10 MB）
```

---

## 6. 建 Release + 上传附件

### 建 Release

```powershell
$payload = @{
    tag_name = "V0.2.0"
    name = "V0.2.0 · 支持音频格式输出"
    draft = $false
    prerelease = $false
    body = $body
} | ConvertTo-Json

$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/NaughtDZ/FFmpegStudio/releases" `
    -Headers $headers -Method Post -Body $payload -ContentType "application/json"
```

### 上传附件：**必须用 curl.exe，不能用 Invoke-RestMethod**

```powershell
$url = "https://uploads.github.com/repos/NaughtDZ/FFmpegStudio/releases/$($rel.id)/assets?name=FFmpegStudio-v0.2.0-win-x64.zip"
curl.exe -s -X POST -H "Authorization: Bearer $token" -H "Content-Type: application/zip" `
    --data-binary "@$zip" -w "`nHTTP=%{http_code} TIME=%{time_total}s" $url
```

> `Invoke-RestMethod -InFile` 传 136 MB 会撞 `HttpClient.Timeout of 600 seconds`，两次都失败。curl 稳定。
>
> 实测上传耗时波动很大：37 秒 ~ 1044 秒（17 分钟），取决于当时的网络。用后台任务跑，别阻塞。

### Release body 里的图片要用绝对 URL

Release 的 markdown 不解析仓库相对路径，必须用 raw 直链：

```markdown
![主界面](https://raw.githubusercontent.com/NaughtDZ/FFmpegStudio/master/docs/screenshot-main.png)
```

---

## 7. 回验 —— **不能省**

四步，任何一步不过都不算发完。

### ① 比对 digest

GitHub 会自己算 SHA256 存在 `asset.digest` 里，可以直接和本地比：

```powershell
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/NaughtDZ/FFmpegStudio/releases/$id" -Headers $headers
$rel.assets[0].digest    # sha256:437166e2...
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
```

### ② 下载回来再算一次

```powershell
curl.exe -sL -o $tmp\r.zip -w "HTTP=%{http_code} SIZE=%{size_download}" $rel.assets[0].browser_download_url
(Get-FileHash "$tmp\r.zip" -Algorithm SHA256).Hash
```

### ③ 解压，核对版本号

```powershell
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $app)
(Get-Item "$app\FfmpegStudio.exe").VersionInfo.FileVersion     # 必须是本次版本
```

### ④ 干净目录实跑

**这一步是整个流程里最有价值的一步。**

```powershell
$p = Start-Process -FilePath "$app\FfmpegStudio.exe" -ArgumentList @(
    '--no-hwprobe',
    '--add', "H:\doc_research\ffmpeg-gui\_testmedia\audio.mp3",
    '--container', 'flac',
    '--outdir', $outDir,
    '--autorun',
    '--shot', $png
) -PassThru -WorkingDirectory $tmp
$p.WaitForExit(120000)

Get-ChildItem $outDir | ForEach-Object {
    & "$app\ffmpeg\ffprobe.exe" -v error -show_entries stream=codec_name -of csv=p=0 $_.FullName
}
```

期望：输出文件存在、编码正确、版本正确。

> V0.1.0 那次就是靠这一步发现包里是旧构建（只有 9 个模块）。

---

## 命令行参数速查

发版回验用得到的开关：

| 参数 | 作用 |
|---|---|
| `--add "a;b"` | 导入文件（分号分隔） |
| `--container <name>` | 设置输出容器 |
| `--outdir <dir>` | 设置输出目录 |
| `--enc <name>` | 设置视频编码器 |
| `--mode av\|video\|audio` | 设置媒体模式 |
| `--add-module <id>` / `--remove <id>` | 增删链上模块（可重复） |
| `--set <id> <key> <value>` | 改参数（可重复） |
| `--command-out <file>` | 导出当前命令 |
| `--save-preset` / `--load-preset <json>` | 方案存取 |
| `--autorun` | 启动后自动跑完队列 |
| `--shot <png>` | 截图 |
| `--no-hwprobe` | 跳过硬件编码器探测（加快启动） |
| `--hwcheck <txt>` | 跑硬件探测并输出报告 |

---

## 踩坑清单

| 现象 | 根因 | 修法 |
|---|---|---|
| 包里是旧版本 | 只 `dotnet build` 没 `-Publish` | 发版前必须 publish + 核对 FileVersion |
| 解压后是 `dist\FfmpegStudio.exe` | `includeBaseDirectory=$true` | 改 `$false` |
| zip 打包报文件被占用 | zip 输出在 `dist\` 内 | 输出到 `dist\` 外面 |
| 推送被拒 100 MB 超限 | zip 被 `git add -A` 带入 | `.gitignore` 加 `*.zip` |
| 上传 600 秒超时 | `Invoke-RestMethod -InFile` | 改用 `curl.exe --data-binary` |
| Release 图片不显示 | markdown 不解析相对路径 | 用 `raw.githubusercontent.com` 绝对 URL |
| 仓库体积异常大 | 被拒提交的 blob 残留 | `git gc --prune=now --aggressive` |
