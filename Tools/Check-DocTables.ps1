# ============================================================================
#  Check-DocTables.ps1 —— 检查所有 md 的**表格结构**是否完整
#  项目：3D联网战斗Demo
#
#  ---------------------------------------------------------------------------
#  为什么需要它（我自己在同一个坑上栽了 3 次以上）
#  ---------------------------------------------------------------------------
#  Markdown 的表格**没有编译期检查**：表头 / 分隔行 / 数据行三者错了任何一个，
#  渲染出来就是"一段竖线文字"，而且**不会报错**。真实发生过的三种：
#
#      ① 往表里插一行时插到了**表头与分隔行之间** ⇒ 分隔行失效，整表变成普通文本
#      ② 分隔行被吃掉（替换时少留了一行）⇒ 同上
#      ③ 空行把一张表**断成两半** ⇒ 后半段那几行变成孤立文本
#
#  ⇒ 判据（机械可判）：
#      · 每个"表头行"（前面是空行、且看起来像表头）的**下一行**必须是分隔行
#      · 分隔行的**上一行**必须是表头行（不能悬空）
#      · 数据行与表头之间**不能有空行**（表被断开）
#
#  ⚠️ 它**不判断"表头写得对不对"**，只判断**结构在不在** —— 后者才是会静默坏掉的那部分。
#
#  用法：  powershell -ExecutionPolicy Bypass -File Tools\Check-DocTables.ps1
#  退出码：0 = 全绿；1 = 有坏表
# ============================================================================

[CmdletBinding()]
param(
    # 只扫这些目录（默认 Docs）
    [string[]]$Path = @('Docs')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# 分隔行的形状：| --- | :--: | --- |
# ⚠️ **必须要求"至少有一个 `-`"** —— 第一版是 `^\|[\s\-:|]+\|$`，而那个字符类
#    **允许 `|` 和空格** ⇒ 它会把**空表头行** `| | |` 也当成分隔行！
#    （`| | |` 是完全合法的"两列、表头留空"写法，本项目好几个文档都在用。）
#    后果：报出一堆"连续两个分隔行"的**误报** ⇒ 又一次"天天误报的警告等于没有警告"。
#    判据：用**前瞻**断言第一个单元格里有 `-`。
$sepPattern = '^\|(?=[^|]*-)[\s\-:|]+\|$'

# ⚠️ 判据**刻意不用任何"这行看起来像不像表头"的启发式**（第一版用了，结果**误报一堆**）：
#    它把 `| 约定 | 不这么做会怎样 |`、`| 场景 | 建议 |`、`| | 能不能从进度推导 |` 这种
#    **完全正常的表头**判成了"孤立表行"，只因为那几个词不在我的关键词表里。
#    ⇒ "天天误报的警告等于没有警告" —— 一个会喊狼来了的检查，最后没人看。
#
# 纯结构判据（只看相邻行，**没有任何词表**）：
#     ① 一行的**上一行不是表格行** ⇒ 它是这张表的**第一行**（表头），
#        那么它的**下一行必须是分隔行**。否则整张表渲染成普通文本。
#     ② 分隔行的**上一行必须是表格行且不是分隔行**（否则表头丢了 / 连续两个分隔行）。
$problems = New-Object System.Collections.Generic.List[string]
$scanned = 0
$tables = 0

# ⚠️ **必须先 Trim** —— 表格行经常以 `| `（**末尾一个空格**）结束，
#    而 `EndsWith('|')` 对此返回 false ⇒ 一堆正常表格被判成"缺分隔行"。
#    （这是本脚本第三处误报源；前两处是"表头关键词表"和"分隔行正则漏了 `-`"。
#      ⇒ 教训：**每一条被报出来的问题都要先去看原文**，不要拿"改文档"去迎合检查器。）
function Test-PipeRow([string]$line) {
    $s = $line.Trim()
    return $s.StartsWith('|') -and $s.EndsWith('|')
}

foreach ($dir in $Path) {
    # ⚠️ 支持**绝对路径**：否则拿临时目录做"变异测试"时会静默扫 0 个文件
    #    （第一版就是这样：`-Path C:\Temp` 被拼成 `<repo>\C:\Temp`，随后报"全绿"）
    $full = if ([System.IO.Path]::IsPathRooted($dir)) { $dir } else { Join-Path $repoRoot $dir }
    if (-not (Test-Path $full)) { continue }

    Get-ChildItem $full -Recurse -Filter '*.md' | ForEach-Object {
        $scanned++
        $lines = [System.IO.File]::ReadAllLines($_.FullName, [System.Text.Encoding]::UTF8)
        $rel = $_.FullName.Replace($repoRoot + '\', '')

        for ($i = 0; $i -lt $lines.Count; $i++) {
            $cur = $lines[$i]

            if (-not (Test-PipeRow $cur)) { continue }

            $isSep = $cur.Trim() -match $sepPattern
            $prevPipe = ($i -gt 0) -and (Test-PipeRow $lines[$i - 1])
            $prevSep = ($i -gt 0) -and ($lines[$i - 1].Trim() -match $sepPattern)
            $nextSep = ($i + 1 -lt $lines.Count) -and ($lines[$i + 1].Trim() -match $sepPattern)

            if ($isSep) {
                $tables++

                if (-not $prevPipe) {
                    $problems.Add("$rel 第 $($i + 1) 行：分隔行**悬空**（上一行不是表格行 ⇒ 表头丢了）")
                }
                elseif ($prevSep) {
                    $problems.Add("$rel 第 $($i + 1) 行：连续两个分隔行（中间的表头丢了）")
                }

                continue
            }

            # 表的"第一行"（上一行不是表格行）=> 它必须是表头，而表头下面必须紧跟分隔行
            if (-not $prevPipe -and -not $nextSep) {
                $problems.Add("$rel 第 $($i + 1) 行：表格缺分隔行（这行下面不是分隔行 ⇒ 整表渲染成普通文本）")
            }
        }
    }
}

Write-Host ''
Write-Host '=============================================================================='
Write-Host '  DOC TABLE CHECKER  -  md 表格结构（表头 / 分隔行 / 空行断开）'
Write-Host '=============================================================================='
Write-Host ''
Write-Host "markdown files scanned : $scanned"
Write-Host "table separators found : $tables"
Write-Host "problems               : $($problems.Count)"
Write-Host ''

if ($scanned -eq 0) {
    Write-Host "ERROR: scanned 0 markdown file -- that is almost always a wrong -Path, NOT a clean repo."
    Write-Host "       A gate that scans nothing must never report success."
    exit 1
}
if ($problems.Count -eq 0) {
    Write-Host 'No broken tables. Every header is followed by a separator.'
    exit 0
}

Write-Host 'Broken tables (each one renders as plain text, and nothing warns you):'
foreach ($p in $problems) { Write-Host "  - $p" }
exit 1
