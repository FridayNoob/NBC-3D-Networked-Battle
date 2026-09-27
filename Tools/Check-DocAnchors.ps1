# ============================================================================
#  Check-DocAnchors.ps1 -- read-only check: do "Name.cs:NNN" citations in the
#  docs still point at what the surrounding sentence claims?
#  Project: 3D networking combat demo
#
#  ---------------------------------------------------------------------------
#  WHY THIS EXISTS (2026-09-27)
#  ---------------------------------------------------------------------------
#  Our docs cite evidence as `File.cs:NNN` in hundreds of places.  Line numbers
#  ROT SILENTLY: a source file gains 200 lines and every citation in the doc now
#  points at an unrelated statement -- and nothing warns you.  That is the
#  failure mode this project hates most: looks authoritative, is wrong, no error.
#
#  Measured on 2026-09-27 while adding class/data-flow figures to five teaching
#  docs: 10 of the cited .cs files had changed after they were read (up to +234
#  lines).  Two real examples, both of which looked perfectly plausible:
#      BossBrain.cs:128   used to be IdleState      -> after the edit: a comment
#      FrameCodec.cs:53   used to be a framing rule -> after the edit: "#nullable disable"
#
#  ---------------------------------------------------------------------------
#  WHAT IT FAILS ON (objective checks -- these cannot be argued with)
#  ---------------------------------------------------------------------------
#    H1 file-not-found      no .cs with that basename exists anywhere in the repo
#    H2 line-out-of-range   NNN is greater than the file's current line count
#    H3 line-not-positive   NNN <= 0
#
#  H1 is classified first: a citation to a source file of the PRE-REFACTOR
#  framework (see $retiredSources) is a historical note, not a defect.
#  deleted by the M1 rewrite.  For those docs a missing file is a historical
#  note, not a defect -- same convention as $retired in Check-DocLinks.ps1.
#  They are counted and listed separately, never as a failure.
#
#  ---------------------------------------------------------------------------
#  WHAT IT ONLY REPORTS (opt-in, see -IncludeSoft)
#  ---------------------------------------------------------------------------
#    S1 anchor-drifted      no identifier named in backticks on that doc line
#                           which also occurs in the target file appears within
#                           +/-Window lines of NNN
#
#  S1 IS OFF BY DEFAULT ON PURPOSE.  Measured on the real corpus (412 citations):
#  99 hits, and nearly all of them were FALSE POSITIVES.  The three causes, all
#  of them features of how these docs legitimately write evidence:
#    1. a table cell cites 3-5 files at once; identifiers belonging to the OTHER
#       files still exist in this one, and land far from NNN
#    2. the cell names the enclosing CLASS (declared once, hundreds of lines
#       above) while citing a MEMBER far below it
#    3. a range like Foo.cs:166 ... :172~:181 cites a REGION, not the symbol
#  => a gate that is wrong ~95% of the time trains people to ignore it
#     ("a warning that always cries wolf is not a warning").  Use -IncludeSoft
#     to print it as a LEAD list and read each one by hand.
#
#  ---------------------------------------------------------------------------
#  WHY BARE ":NNN" NUMBERS ARE COUNTED BUT NEVER JUDGED
#  ---------------------------------------------------------------------------
#  Docs write Foo.cs:12, then bare :34~:56 for the rest of the same cell.  Only
#  the explicitly file-qualified form is checked, because this corpus uses two
#  INCOMPATIBLE conventions for the bare form and the text alone cannot tell
#  them apart:
#    * after a full-width semicolon the number sometimes still means the same
#      file, and sometimes means a file that was only named in prose
#      ("... IState.cs:80 ... EventId.cs ... its only use is at :119", where
#       :119 really is EventId.cs:119)
#  v1 of this script guessed "most recent numbered mention" and produced three
#  bogus H2 failures in the A10 state-machine doc as a result.  The DOC was right
#  and the checker was wrong.
#  => a guess inside a gate is worse than a documented gap, so bare numbers are
#     reported as a count (continuations) and skipped, never attributed.
#  Lesson kept here on purpose: verify the checker before believing its verdict.
#
#  ---------------------------------------------------------------------------
#  OTHER BOUNDARIES
#  ---------------------------------------------------------------------------
#  * Ambiguous basename (e.g. a Client\Library\PackageCache copy of a name that
#    also exists under Assets\): length checks take the MAXIMUM over candidates
#    (deliberately lenient) and the citation is counted as ambiguous.
#  * scanned == 0 is an ERROR, not a pass: a mistyped -Path must never look green
#    (the trap Check-DocTables.ps1 documents for its own -Path).
#  * Nothing is ever edited.  Fixing an anchor is a human decision: sometimes the
#    number is stale, sometimes the sentence is.
#
#  ---------------------------------------------------------------------------
#  USAGE
#  ---------------------------------------------------------------------------
#      powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Check-DocAnchors.ps1
#      ... -Path 'Docs\<one folder>'     # restrict to one folder
#      ... -IncludeSoft                    # also fail on the S1 heuristic
#      ... -Path C:\TEMP\mut -Window 3     # mutation test (absolute path works)
#
#      # mutation test that must go red (H1 + H2 + S1) and must stay green on a
#      # correct citation -- run it after ANY edit to this script:
#      $mut = "$env:TEMP\nbc_anchor_mut"; ni -ItemType Directory -Force $mut | Out-Null
#      "- H1: `Resolve` is at `NoSuchFile.cs:1`"      | Set-Content "$mut\bad.md"
#      "- H2: `Resolve` is at `DamageMath.cs:99999`"  | Add-Content  "$mut\bad.md"
#      "- S1: `Resolve` is at `DamageMath.cs:1`"      | Add-Content  "$mut\bad.md"
#      "- OK: `Resolve` is at `DamageMath.cs:125`"    | Add-Content  "$mut\bad.md"
#      powershell -File Tools\Check-DocAnchors.ps1 -Path $mut -IncludeSoft   # expect 1
#
#  EXIT CODES: 0 = clean; 1 = findings (or nothing scanned); 2 = fatal
#
#  ASCII-only on purpose: Tools\ scripts are all-ASCII by project convention
#  (see Tools\README.md), which also sidesteps the W7 BOM trap entirely.
# ============================================================================

[CmdletBinding()]
param(
    # Folders to scan (relative to the repo root, or absolute -- absolute paths
    # are what make the mutation test above possible).
    [string[]] $Path = @('Docs'),

    # Also run (and fail on) the noisy S1 heuristic.  Off by default: see the
    # false-positive analysis in the header.
    [switch] $IncludeSoft,

    # How many lines around the citation still count as "the anchor is nearby".
    [int] $Window = 3
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $repoRoot -or -not (Test-Path $repoRoot)) {
    Write-Host '[fatal] cannot locate the repo root from this script location.'
    exit 2
}

# Source files of the PRE-REFACTOR framework, deleted by the M1 rewrite.  A doc
# citing one of these is writing a historical record, not a broken anchor -- same
# idea as $retired in Check-DocLinks.ps1: surface it, never fail on it.
# Keyed by basename (not by doc) so it stays ASCII-only, and so it also covers any
# other doc that happens to quote the old framework.
$retiredSources = @(
    'BaseManager.cs',
    'PoolMgr.cs',
    'ResMgr.cs',
    'ScenesMgr.cs',
    'InputMgr.cs',
    'MusicMgr.cs',
    'AStarMgr.cs',
    'MonoMgr.cs',
    'MonoController.cs'
)

# ---------------------------------------------------------------- index .cs files
$csIndex = @{}

foreach ($f in (Get-ChildItem $repoRoot -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue)) {
    # bin/obj = stale build copies, never what a doc means.
    # PackageCache IS indexed on purpose: the docs legitimately cite YooAsset and
    # ScriptableBuildPipeline sources that only exist there, and excluding it
    # turned 18 real citations into bogus file-not-found errors.
    if ($f.FullName -match '\\(bin|obj)\\') { continue }

    if (-not $csIndex.ContainsKey($f.Name)) { $csIndex[$f.Name] = @() }
    $csIndex[$f.Name] += $f.FullName
}

$lineCache = @{}
$symCache = @{}

function Get-SourceLines([string] $full) {
    if (-not $lineCache.ContainsKey($full)) {
        $lineCache[$full] = [System.IO.File]::ReadAllLines($full, [System.Text.Encoding]::UTF8)
    }
    return $lineCache[$full]
}

# identifier -> line numbers inside one file (used only by the S1 heuristic)
function Get-SymbolLines([string] $full) {
    if (-not $symCache.ContainsKey($full)) {
        $lines = Get-SourceLines $full
        $map = @{}

        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($m in [regex]::Matches($lines[$i], '\b[A-Za-z_][A-Za-z0-9_]*\b')) {
                $s = $m.Value

                # 1-2 char identifiers (`i`, `id`, `hp`) match almost anywhere and
                # would turn S1 into a check that can never go red.
                if ($s.Length -lt 3) { continue }

                if (-not $map.ContainsKey($s)) { $map[$s] = New-Object System.Collections.Generic.List[int] }
                $map[$s].Add($i + 1)
            }
        }

        $symCache[$full] = $map
    }

    return $symCache[$full]
}

# ---------------------------------------------------------------- scan
# NOTE: Test-Anchor is defined ABOVE this block on purpose -- PowerShell only
# makes a function callable once its definition has been executed, so a function
# defined after the loop would fail at run time with "not recognized".
$docsScanned   = 0
$citations     = 0
$unjudged      = 0
$continuations = 0
$ambiguous     = 0
$historical    = New-Object System.Collections.Generic.List[string]
$hard          = New-Object System.Collections.Generic.List[string]
$soft          = New-Object System.Collections.Generic.List[string]

function Test-Anchor {
    param([string] $File, [int] $Num)

    $where = "$script:curRel line $script:curLine -> $File`:$Num"

    if ($Num -le 0) {
        $script:hard.Add("H3 line-not-positive | $where")
        return
    }

    if (-not $script:csIndex.ContainsKey($File)) {
        if ($script:retiredSources -contains $File) { $script:historical.Add("HISTORICAL retired source | $where") }
        else { $script:hard.Add("H1 file-not-found    | $where") }
        return
    }

    $cands = $script:csIndex[$File]
    if ($cands.Count -gt 1) { $script:ambiguous++ }

    $maxLines = 0
    $near = @{}

    foreach ($c in $cands) {
        $src = Get-SourceLines $c
        if ($src.Count -gt $maxLines) { $maxLines = $src.Count }

        if (-not $script:IncludeSoft) { continue }

        $map = Get-SymbolLines $c

        foreach ($s in $script:curSyms) {
            if ($map.ContainsKey($s)) {
                if (-not $near.ContainsKey($s)) { $near[$s] = New-Object System.Collections.Generic.List[int] }
                foreach ($ln in $map[$s]) { $near[$s].Add($ln) }
            }
        }
    }

    if ($Num -gt $maxLines) {
        $hard.Add("H2 line-out-of-range | $where (file has $maxLines lines)")
        return
    }

    if (-not $script:IncludeSoft) { return }

    if ($near.Count -eq 0) { $script:unjudged++; return }

    $hit = $false
    $closest = @{}

    foreach ($s in $near.Keys) {
        foreach ($ln in $near[$s]) {
            $d = [math]::Abs($ln - $Num)
            if (-not $closest.ContainsKey($s) -or $d -lt $closest[$s]) { $closest[$s] = $d }
            if ($d -le $script:Window) { $hit = $true }
        }
    }

    if (-not $hit) {
        $detail = ($closest.GetEnumerator() |
            Sort-Object -Property Value |
            ForEach-Object { "$($_.Key)@$($_.Value) lines away" }) -join ', '
        $script:soft.Add("S1 anchor-drifted    | $where (nearest: $detail)")
    }
}

$targets = @()

foreach ($dir in $Path) {
    $full = if ([System.IO.Path]::IsPathRooted($dir)) { $dir } else { Join-Path $repoRoot $dir }

    if (-not (Test-Path $full)) {
        Write-Host "[warn] path does not exist, skipped: $dir"
        continue
    }

    $targets += Get-ChildItem $full -Recurse -Filter '*.md' -File -ErrorAction SilentlyContinue
}

foreach ($doc in $targets) {
    $docsScanned++
    $lines = [System.IO.File]::ReadAllLines($doc.FullName, [System.Text.Encoding]::UTF8)
    $rel = $doc.FullName.Replace($repoRoot + '\', '')

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -notmatch '\.cs') { continue }

        $proseSyms = @()

        if ($IncludeSoft) {
            foreach ($m in [regex]::Matches($line, '`([A-Za-z_][A-Za-z0-9_]*)`')) {
                if ($proseSyms -notcontains $m.Groups[1].Value) { $proseSyms += $m.Groups[1].Value }
            }
        }

        # Walk every backticked `.cs` mention (numbered or not) and every bare
        # Only EXPLICITLY file-qualified citations (`Foo.cs:12`) are judged.
        # A bare `:34` is a continuation of an earlier mention, and this corpus
        # uses two incompatible conventions for it: after a full-width semicolon
        # the number sometimes still means the same file, sometimes a file that
        # was only named in prose.  Any attribution would be a guess, and a guess
        # in a gate is worse than a gap -- so they are counted, never judged.
        $rx = '`([A-Za-z0-9_]+\.cs):(\d+)`|:(\d+)'

        $script:curRel = $rel
        $script:curLine = $i + 1
        $script:curSyms = $proseSyms

        foreach ($m in [regex]::Matches($line, $rx)) {
            if ($m.Groups[1].Success) {
                $citations++
                Test-Anchor -File $m.Groups[1].Value -Num ([int]$m.Groups[2].Value)
                continue
            }

            $continuations++
        }
    }
}

# ---------------------------------------------------------------- report
Write-Host ''
Write-Host '=============================================================================='
Write-Host '  DOC ANCHOR CHECKER  -  do File.cs:NNN citations still point at the claim?'
Write-Host '=============================================================================='
Write-Host ''
Write-Host "markdown files scanned : $docsScanned"
Write-Host "citations found        : $citations"
Write-Host "HARD findings          : $($hard.Count)"
Write-Host "HISTORICAL (retired src): $($historical.Count)   [$($retiredSources.Count) known retired basenames]"
Write-Host "bare :NNN continuations: $continuations   [not judged -- ambiguous by convention]"
Write-Host "SOFT findings (S1)     : $($soft.Count)   [only collected with -IncludeSoft]"
Write-Host "unjudged               : $unjudged   [no in-file symbol named on the line]"
Write-Host "ambiguous basename     : $ambiguous"
Write-Host ''

if ($citations -eq 0) {
    Write-Host 'ERROR: no File.cs:NNN citation was found at all.'
    Write-Host 'That is almost always a wrong -Path, NOT a clean repo -- a check that'
    Write-Host 'scans nothing must never report success.'
    exit 1
}

if ($hard.Count -gt 0) {
    Write-Host 'HARD findings (the citation cannot be right -- file or line does not exist):'
    foreach ($p in $hard) { Write-Host "  - $p" }
    Write-Host ''
}

if ($soft.Count -gt 0) {
    Write-Host 'SOFT findings (heuristic LEADS, not verdicts -- open the source and decide'
    Write-Host 'whether the number or the sentence is the stale one):'
    foreach ($p in $soft) { Write-Host "  - $p" }
    Write-Host ''
}

if ($hard.Count -gt 0 -or $soft.Count -gt 0) {
    Write-Host "FAILED: $($hard.Count) hard, $($soft.Count) soft."
    exit 1
}

if ($historical.Count -gt 0) {
    Write-Host "PASSED (hard findings only). $($historical.Count) historical citation(s) in the"
    Write-Host 'pre-refactor docs, listed here so they do not rot unnoticed:'
    foreach ($p in $historical) { Write-Host "  - $p" }
    Write-Host ''
}

Write-Host 'PASSED: every live citation names an existing file and an existing line.'
exit 0
