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

# ⚠️ 临时目录**按进程号隔离**（2026-09-26 实测踩到）：两个 agent 同时跑本脚本时，
#    共享 `%TEMP%\nbc_mermaid` 会让两边互相覆写 .mmd / .svg / .log，
#    表现是"某一方偶发退出码 1、而且连汇总表格都没打出来" —— 假失败里最难查的一类。
$temp = Join-Path $env:TEMP ("nbc_mermaid_" + $PID)
if (-not (Test-Path $temp)) { New-Item -ItemType Directory -Path $temp | Out-Null }

# ⚠️ 旧产物**不在这里清**（原来开局就把 out\*.svg 全删了）：并发跑时，
#    一方正在渲染、另一方把它的产物删掉 → "退出码 0 但没有产物"。
#    改成**跑完再清**：算出本次应有的产物清单，把清单外的旧 SVG 删掉（见文件末）。
$expectedSvgs = @()

# ============================================================================
#  调一次 mmdc，**返回它的退出码**
# ============================================================================
#  ⚠️ 为什么必须包一层、还要临时改 `$ErrorActionPreference`（2026-09-26 实测复现）：
#     本脚本开头是 `$ErrorActionPreference = "Stop"`，而 Windows PowerShell 5.1
#     会把**原生程序写到 stderr 的内容**包成一个 ErrorRecord ——
#     mmdc 遇到 mermaid 语法错时正好往 stderr 打 "Error: Parse error on line N"，
#     于是脚本**在第 N 块当场终止**：既看不到是哪一块坏的、也**打不出汇总表**
#     （子代理实测"跑到第 19 块终止、连表都没打"，我一开始还误判成并发问题）。
#     ⇒ 只在这一次调用期间把 EAP 设回 Continue，让"失败"变成**可记录的数据**而不是终止信号。
#       这也是本项目的老规矩：**一个坏输入不该让整批校验失去报告能力**。
function Invoke-Mmdc {
    param(
        # ⚠️ 参数名**不能叫 `Input` / `Output`**：`$Input` 是 PowerShell 的**自动变量**
        #    （管道输入的枚举器），拿它当参数名 → `-i $Input` 会展开成一堆参数，
        #    报错形状是 `mmdc.cmd : error: too many arguments`（2026-09-26 实测踩到）。
        [Parameter(Mandatory = $true)][string] $MmdPath,
        [Parameter(Mandatory = $true)][string] $SvgPath,
        [Parameter(Mandatory = $true)][string] $LogPath
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"

    try {
        # ⚠️ `$null = ` **不能省**：函数里没被接住的 stdout 会混进**返回值**
        #    （mmdc 会打一行 "Generating single mermaid chart"），
        #    于是 `$code -ne 0` 恒真、所有图都被判失败 —— 2026-09-26 我包函数时自己踩过。
        $null = & $MmdcPath -i $MmdPath -o $SvgPath -p $PuppeteerConfig -b white 2> $LogPath
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# ---------------------------------------------------------------- 逐 md 抠块渲染
# 两类目录：
#   · 产物目录（本目录）：每块渲成 out\<篇名>-<序号>.svg 并**提交进仓库**
#   · 仅校验目录（Docs\教学、Docs\排查手册）：渲到临时目录，只回答"语法对不对"，
#     **不产出、不提交** —— 那些文档里的图在 GitHub 上是原生渲染的，
#     我们只需要保证"它不会画出个错误框"，不需要再存 20 多份 SVG（那是几 MB）
# ---------------------------------------------------------------- 名字核对
# ⚠️ 为什么"mmdc 退出码 0"不够（2026-09-27 补这条检查的直接原因）：
#    mermaid **遇到不认识的东西不会报错**：关系里引用一个没声明的类，它会**当场新建一个节点**画出来；
#    而一段它解析不了的成员行，它**直接丢掉**。
#    ⇒ 退出码 0 + 不是错误图**只证明"没画成错误框"**，**不证明"声明的东西都画出来了"**。
#    所以这里再加一道：把 SVG 剥成纯文本，逐个核对**每个类名 / 每个节点标签**是否真出现在产物里。
#
# ⚠️ 它是个**启发式**检查（如实记三条边界）：
#    ① 长标签会被 mermaid 折成多个 `<tspan>`，剥标签时会插进空格 ⇒ 只拿**前 16 个字符**去比；
#    ② 标签里**允许有排版标记**（本项目大量用 `<br/>` 换行）：`<br/>` 被剥成空格，
#       所以核对前两边都要**把空白归一化** —— 否则会把"明明画出来了"报成缺失。
#       2026-09-27 实测踩到：第一版把 3 块图报红，而那 3 块一行不缺，缺的是我对 `<br/>` 的假设。
#    ③ 因此它**可能误报**（报"找不到"但其实只是折行/排版不同）—— 误报时**人看一眼**即可。
#       要记住方向：**"报绿"是真的绿（名字确实在产物里），"报红"值得看一眼**。
function Get-MissingDiagramName {
    param(
        [Parameter(Mandatory = $true)][string] $Body,
        [Parameter(Mandatory = $true)][string] $SvgPath
    )

    $svg = [System.IO.File]::ReadAllText($SvgPath, [System.Text.Encoding]::UTF8)
    $svg = [regex]::Replace($svg, '(?s)<style.*?</style>', ' ')
    $svg = [regex]::Replace($svg, '(?s)<script.*?</script>', ' ')
    $svg = [regex]::Replace($svg, '<[^>]+>', ' ')
    $svg = $svg.Replace('&lt;', '<').Replace('&gt;', '>').Replace('&amp;', '&')
    $svg = $svg.Replace('&quot;', '"').Replace('&#39;', "'")
    $svg = [regex]::Replace($svg, '\s+', ' ')       # 归一化空白（见边界 ②）

    $expected = @()

    if ($Body -match '(?m)^\s*classDiagram') {
        # 类声明：`class Foo {` / `class Foo`
        foreach ($m in [regex]::Matches($Body, '(?m)^\s*class\s+([^\s{]+)')) {
            $expected += $m.Groups[1].Value
        }

        # 关系两端：`A --> B`（两端都该被画出来）
        $rel = '(?m)^\s*([A-Za-z_]\w*(?:~[^~]+~)?)\s*' +
               '(?:\*--|<\|\.\.|<\|--|o--|-->|\.\.>|--\*|--\|>|\.\.\|>)\s*([A-Za-z_]\w*)'

        foreach ($m in [regex]::Matches($Body, $rel)) {
            $expected += $m.Groups[1].Value
            $expected += $m.Groups[2].Value
        }
    }
    else {
        # flowchart / sequenceDiagram：引号里的节点标签 + participant 的别名
        foreach ($m in [regex]::Matches($Body, '"([^"]+)"')) {
            $expected += $m.Groups[1].Value
        }

        foreach ($m in [regex]::Matches($Body, '(?m)^\s*participant\s+\w+\s+as\s+(.+)$')) {
            $expected += $m.Groups[1].Value.Trim()
        }
    }

    $expected = $expected |
        ForEach-Object { ($_ -replace '~.*$', '').Trim() } |
        Where-Object { $_ -ne '' } |
        Sort-Object -Unique

    $missing = @()

    foreach ($e in $expected) {
        # 归一化：`<br/>` → 空格、连续空白压成一个（与上面 SVG 的处理对称）
        $probe = ($e -replace '<br\s*/?>', ' ') -replace '\s+', ' '
        $probe = $probe.Trim()

        if ($probe.Length -gt 16) { $probe = $probe.Substring(0, 16).Trim() }
        if (-not $svg.Contains($probe)) { $missing += $e }
    }

    return $missing
}

$repoRoot = Split-Path (Split-Path $root -Parent) -Parent
$artifactDocs = Get-ChildItem $root -Filter *.md | Where-Object { $_.Name -ne "README.md" } | Sort-Object Name
$checkDocs = @()

$missingDirs = @()

foreach ($dir in $ExtraDirs) {
    $full = if ([System.IO.Path]::IsPathRooted($dir)) { $dir } else { Join-Path $repoRoot $dir }

    if (Test-Path $full) {
        $checkDocs += Get-ChildItem $full -Filter *.md -Recurse | Sort-Object FullName
    }
    else {
        # ⚠️ 一个**扫不到的目录**必须报错，不能静默少扫（2026-09-27）：
        #    原来的写法 `if (Test-Path ...)` 会把"目录改名/路径写错"变成"这次少校验一批"，
        #    而**汇总行照样报成功** —— 正是本项目最忌讳的假绿。
        $missingDirs += $full
    }
}

if ($missingDirs.Count -gt 0) {
    Write-Output ("ERROR: these -ExtraDirs do not exist: " + ($missingDirs -join "、"))
    Write-Output "       A gate that silently scans less must never report success."
    exit 1
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
            $expectedSvgs += $svg
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
        $code = Invoke-Mmdc -MmdPath $mmd -SvgPath $svg -LogPath $log

        $ok = $false
        $why = ""

        if ($code -ne 0) {
            $detail = ""

            if (Test-Path $log) {
                $lines = [System.IO.File]::ReadAllLines($log, [System.Text.Encoding]::UTF8)
                $hit = -1

                # ⚠️ 要抓的是 **mmdc 自己那一行**（`Error: Parse error on line N:`），
                #    不是 PowerShell 包在外面的 `+ FullyQualifiedErrorId : NativeCommandError`
                #    —— 后者对定位毫无用处（2026-09-26 实测：日志里两种都有，第一版抓错了）。
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($lines[$i] -match '^\s*Error:' -or $lines[$i] -match 'Parse error') {
                        $hit = $i
                        break
                    }
                }

                if ($hit -ge 0) {
                    $detail = "，原文：" + $lines[$hit].Trim()

                    # 再带上**出错的那一行源码片段**（mmdc 会打在下一行）—— 一眼就知道是哪个字符坏了
                    if ($hit + 1 -lt $lines.Count -and $lines[$hit + 1].Trim().Length -gt 0) {
                        $detail += " ／ " + $lines[$hit + 1].Trim()
                    }
                }
                else {
                    $fallback = $lines |
                        Where-Object { $_ -match 'error' -and $_ -notmatch '^\s*\+' } |
                        Select-Object -First 1

                    if ($fallback) { $detail = "，原文：" + $fallback.Trim() }
                }
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
                # 名称核对：产物里必须真的出现**每个类名 / 每个节点标签**（理由见 `Get-MissingDiagramName`）
                $missing = Get-MissingDiagramName -Body $block.Groups[1].Value -SvgPath $svg

                if ($missing.Count -gt 0) {
                    $why = "产物里找不到 " + $missing.Count + " 个名字：" + ($missing -join "、")
                }
                else {
                    $ok = $true
                }
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

# ---------------------------------------------------------------- 清掉清单外的旧产物
# （改完图的名字后，旧的 SVG 还躺在 out\ 里会让人以为它是最新的 —— 所以必须清，
#   但只能在**跑完之后**清，见文件前面对并发的说明）
$stale = @(Get-ChildItem $out -Filter *.svg -ErrorAction SilentlyContinue |
    Where-Object { $expectedSvgs -notcontains $_.FullName })

if ($stale.Count -gt 0) {
    foreach ($s in $stale) { Remove-Item $s.FullName -Force }
    Write-Output ("清掉 {0} 个不再对应的旧产物：{1}" -f $stale.Count, (($stale | ForEach-Object { $_.Name }) -join "、"))
}

if ($total -eq 0) {
    Write-Output "ERROR: 0 mermaid block was found -- that is almost always a wrong path, NOT a clean repo."
    Write-Output "       A gate that scans nothing must never report success."
    exit 1
}
if ($failed -gt 0) { exit 1 }
