# ============================================================================
#  Docs\图\render.ps1 —— 把 md 里的 mermaid 块渲染成 SVG（一条命令，可重复跑）
#  项目：3D联网战斗Demo
#
#  ---------------------------------------------------------------------------
#  为什么要有这个脚本（而不是"手动导一遍"）
#  ---------------------------------------------------------------------------
#  `Docs\图\*.md` 里的 mermaid 代码块是**唯一真源**：GitHub 会直接渲染它。
#  `out\*.svg` 只是**产物**（给不能渲染 mermaid 的地方用：离线看、塞进 PPT、发给面试官）。
#
#  ⚠️ 手工导一次、之后只改 md —— 图与 md 就会**静默漂移**（本项目最恨这个）。
#     所以：**改图只改 md，然后跑这个脚本**。脚本会：
#       ① 从每个 md 里把 ```mermaid 块抠出来
#       ② 逐块渲染成 `out\<md名>-<序号>.svg`
#       ③ **回读产物**：文件在不在、够不够大、里面有没有 mermaid 的语法错误图形
#       ④ 有任何一块失败 → 退出码 1（能被 CI / 手工一眼看出）
#
#  ---------------------------------------------------------------------------
#  依赖（装在**仓库外面**，别把 node_modules 塞进仓库）
#  ---------------------------------------------------------------------------
#      $env:PUPPETEER_SKIP_DOWNLOAD = "true"      # 跳过下载 Chromium，用本机浏览器
#      npm install @mermaid-js/mermaid-cli
#      # 再用一个 puppeteer 配置文件指向本机浏览器，例如 C:\TEMP\mmdc\puppeteer.json：
#      # { "executablePath": "C:/Program Files/Google/Chrome/Application/chrome.exe" }
#
#      用法：  pwsh -File Docs\图\render.ps1
#              pwsh -File Docs\图\render.ps1 -MmdcPath D:\tools\mmdc\node_modules\.bin\mmdc.cmd
# ============================================================================

[CmdletBinding()]
param(
    # mmdc 可执行文件（默认去几个常见位置找）
    [string] $MmdcPath,

    # puppeteer 配置（里面写本机浏览器的 executablePath）
    [string] $PuppeteerConfig = "C:\TEMP\mmdc\puppeteer.json",

    # 产物目录（相对本脚本所在目录）
    [string] $OutDir = "out",

    # **只校验语法、不产出产物**的目录（相对仓库根；这些文档里的图在 GitHub 上原生渲染，
    # 不需要再存一份 SVG —— 20 多篇教程全存下来是几 MB）
    [string[]] $ExtraDirs = @("Docs\教学", "Docs\排查手册")
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$root = $PSScriptRoot
$out = Join-Path $root $OutDir

# ---------------------------------------------------------------- 找 mmdc
if (-not $MmdcPath) {
    $candidates = @(
        "C:\TEMP\mmdc\node_modules\.bin\mmdc.cmd",
        (Join-Path $root "node_modules\.bin\mmdc.cmd")
    )

    foreach ($c in $candidates) {
        if (Test-Path $c) { $MmdcPath = $c; break }
    }
}

if (-not $MmdcPath -or -not (Test-Path $MmdcPath)) {
    Write-Output "[致命] 找不到 mmdc。装法见本脚本头部（装在仓库外面），或用 -MmdcPath 指定。"
    exit 2
}

if (-not (Test-Path $PuppeteerConfig)) {
    Write-Output "[致命] 找不到 puppeteer 配置：$PuppeteerConfig（里面要有 executablePath 指向本机 Chrome/Edge）"
    exit 2
}

if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }

# 清掉旧产物（免得改完名字后，旧 SVG 还躺在那里让人以为是最新的）
Get-ChildItem $out -Filter *.svg -ErrorAction SilentlyContinue | Remove-Item -Force

$temp = Join-Path $env:TEMP "nbc_mermaid"
if (-not (Test-Path $temp)) { New-Item -ItemType Directory -Path $temp | Out-Null }

# ---------------------------------------------------------------- 逐 md 抠块渲染
# 两类目录：
#   · 产物目录（本目录）：每块渲成 out\<篇名>-<序号>.svg 并**提交进仓库**
#   · 仅校验目录（Docs\教学、Docs\排查手册）：渲到临时目录，只回答"语法对不对"，
#     **不产出、不提交** —— 那些文档里的图在 GitHub 上是原生渲染的，
#     我们只需要保证"它不会画出个错误框"，不需要再存 20 多份 SVG（那是几 MB）
$repoRoot = Split-Path (Split-Path $root -Parent) -Parent
$artifactDocs = Get-ChildItem $root -Filter *.md | Where-Object { $_.Name -ne "README.md" } | Sort-Object Name
$checkDocs = @()

foreach ($dir in $ExtraDirs) {
    $full = if ([System.IO.Path]::IsPathRooted($dir)) { $dir } else { Join-Path $repoRoot $dir }

    if (Test-Path $full) {
        $checkDocs += Get-ChildItem $full -Filter *.md -Recurse | Sort-Object FullName
    }
}

$total = 0
$failed = 0
$artifactCount = 0
$checkCount = 0
$rows = @()

$targets = @()
foreach ($d in $artifactDocs) { $targets += [pscustomobject]@{ Doc = $d; Artifact = $true } }
foreach ($d in $checkDocs) { $targets += [pscustomobject]@{ Doc = $d; Artifact = $false } }

foreach ($target in $targets) {
    $doc = $target.Doc
    $isArtifact = $target.Artifact
    $text = [System.IO.File]::ReadAllText($doc.FullName, [System.Text.Encoding]::UTF8)
    $blocks = [regex]::Matches($text, '(?s)```mermaid\r?\n(.*?)\r?\n```')

    $n = 0

    foreach ($block in $blocks) {
        $n++
        $total++

        $base = [System.IO.Path]::GetFileNameWithoutExtension($doc.Name)

        # 同名文档在不同目录里会撞名（教程与排查手册各有一批），所以仅校验的那些带上前缀
        if ($isArtifact) {
            $svg = Join-Path $out ("{0}-{1}.svg" -f $base, $n)
            $shown = "{0}-{1}.svg" -f $base, $n
        }
        else {
            $svg = Join-Path $temp ("check-{0}-{1}.svg" -f $base, $n)
            $shown = "(仅校验)"
        }

        $mmd = Join-Path $temp ("{0}-{1}.mmd" -f $base, $n)

        # mmd 用 UTF-8 **带 BOM** 写：mmdc 在 Windows 上读无 BOM 的中文会乱码
        [System.IO.File]::WriteAllText($mmd, $block.Groups[1].Value, [System.Text.UTF8Encoding]::new($true))

        # ⚠️ 判据用 **mmdc 自己的退出码**（实测：语法错误 = 1，成功 = 0），不要自己写正则猜。
        #    我第一版的正则里带了 "error-icon"，而 mermaid **每一张图都会**带
        #    `.error-icon{...}` 这段默认 CSS ⇒ 六张图全被误判成"语法错误"。
        #    自己另写一套判据，迟早和工具的真实判定不一致（本项目的老教训）。
        $log = Join-Path $temp ("{0}-{1}.log" -f $base, $n)
        & $MmdcPath -i $mmd -o $svg -p $PuppeteerConfig -b white 2> $log
        $code = $LASTEXITCODE

        $ok = $false
        $why = ""

        if ($code -ne 0) {
            $detail = ""

            if (Test-Path $log) {
                $lines = [System.IO.File]::ReadAllLines($log, [System.Text.Encoding]::UTF8)
                $hit = $lines | Where-Object { $_ -match "Error" } | Select-Object -First 1
                if ($hit) { $detail = "，原文：" + $hit.Trim() }
            }

            $why = "mmdc 退出码 " + $code + $detail
        }
        elseif (-not (Test-Path $svg)) {
            $why = "退出码 0 但没有产物"
        }
        else {
            $content = [System.IO.File]::ReadAllText($svg, [System.Text.Encoding]::UTF8)

            if ($content.Length -lt 3000) {
                $why = "产物只有 " + $content.Length + " 字符（像是渲染失败）"
            }
            elseif ($content.Contains("Syntax error in text")) {
                $why = "产物里是 mermaid 的错误图形"
            }
            else {
                $ok = $true
            }
        }

        if ($isArtifact) { $artifactCount++ } else { $checkCount++ }

        if (-not $ok) { $failed++ }

        $rows += [pscustomobject]@{
            图    = "$($doc.Name) 第 $n 块"
            产物  = $shown
            大小  = if (Test-Path $svg) { (Get-Item $svg).Length } else { 0 }
            结果  = if ($ok) { "OK" } else { "失败：" + $why }
        }
    }
}

# ---------------------------------------------------------------- 汇总
# 先把表格渲染成纯文本再输出：把 Format-* 对象直接接进别的命令
# （例如 `| Select-Object -Last 3`）会让 PowerShell 报
# "GroupEndData is not valid or not in the correct sequence"。
# 只读工具必须能被管道组合（本次两个脚本都栽在这上面）。
Write-Output ($rows | Format-Table -AutoSize | Out-String).TrimEnd()

Write-Output ("渲染 {0} 块（产物 {1} 块、仅校验 {2} 块），失败 {3} 块；产物目录：{4}" -f `
    $total, $artifactCount, $checkCount, $failed, $out)

if ($failed -gt 0) { exit 1 }
