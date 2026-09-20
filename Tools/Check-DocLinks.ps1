<#
================================================================================
 Check-DocLinks.ps1 - read-only documentation link checker (with classification)
 Project: 3D Networking Battle Demo (NBC)

 PURPOSE
   The documents in this project reference each other and reference files that
   do not exist yet, because they are deliverables of FUTURE milestones
   (M1-M6). For example Docs\01 (requirements) plans to produce:
       Docs\06 (framework rework log)      Docs\07 (performance report)
       Docs\09 (design patterns)      Docs\02 (architecture doc)   ... etc.

   That is intentional. The problem is that after a while you can no longer tell
   the difference between:
     (a) "planned for a later milestone" - fine, expected
     (b) "typo / wrong path / file should exist NOW" - a real defect

   This script draws that line automatically by scanning the requirements
   document's deliverables tables to learn which paths are PLANNED, then
   classifying every missing reference as PLANNED or BROKEN.

 WHY A SCRIPT HERE (rule 15 consideration)
   This is a read-only checker: it prints a classification and changes nothing.
   It does not do the work for you - you still decide what to fix. Per rule 15,
   tools that only help you observe are allowed; tools that perform a multi-step
   workflow for you are not.

 HOW TO READ THE OUTPUT
   [OK]      target exists
   [PLANNED] target missing but declared as a future deliverable -> expected
   [BROKEN]  target missing and NOT declared anywhere -> investigate:
             is it a typo? a wrong relative path? or a file that should exist now?

 USAGE
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Check-DocLinks.ps1

   # Only show real problems (hide the expected PLANNED ones)
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Check-DocLinks.ps1 -BrokenOnly

 EXIT CODES
   0 = no BROKEN references (planned ones are fine)
   1 = at least one BROKEN reference found (needs attention)
================================================================================
#>

[CmdletBinding()]
param(
    [switch]$BrokenOnly
)

$ErrorActionPreference = 'Continue'

$Root      = Split-Path -Parent $PSScriptRoot
$PlanningDoc = Join-Path $Root 'Docs\01-项目需求文档.md'

function Write-Header($t) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
}
function Ok($t)      { Write-Host "  [OK]      $t" -ForegroundColor Green }
function Planned($t) { Write-Host "  [PLANNED] $t" -ForegroundColor Yellow }
function Broken($t)  { Write-Host "  [BROKEN]  $t" -ForegroundColor Red }

Write-Header 'DOC LINK CHECKER  -  classify missing references'

# ---------------------------------------------------------------------------
# 1. Learn which paths are PLANNED deliverables
# ---------------------------------------------------------------------------
# Strategy: scan every .md file for backtick-quoted project-relative paths that
# appear inside the REQUIREMENTS document (the single source of truth for what
# will be produced). Those become the "planned" allow-list.
$planned = New-Object System.Collections.Generic.HashSet[string]

if (-not (Test-Path $PlanningDoc)) {
    Write-Host "  [warn] requirements doc not found: Docs\01-项目需求文档.md" -ForegroundColor Yellow
    Write-Host "         every missing reference will be reported as BROKEN." -ForegroundColor Yellow
} else {
    $reqText = Get-Content $PlanningDoc -Raw -Encoding UTF8
    $rxPath = '(?:Docs|Tools|ClientStaging|Server|DeepSeekOutput|Client)[\\/][^\s`\)\|,;]*?\.(?:md|ps1|sql|cs|json|xml|asmdef|sln|csproj)'
    foreach ($m in [regex]::Matches($reqText, $rxPath)) {
        $p = $m.Value.Trim().TrimEnd('.', ',', ';', [char]0x3001, [char]0xFF0C, [char]0xFF1B)
        [void]$planned.Add($p.Replace('/', '\'))
    }
    Write-Host ("  learned {0} planned deliverable paths from the requirements doc" -f $planned.Count) -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 2. Walk all markdown files and extract backtick-quoted project paths
# ---------------------------------------------------------------------------
$mdFiles = Get-ChildItem -Path $Root -Recurse -Filter *.md -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\node_modules\\' }

$results = New-Object System.Collections.Generic.List[object]
# Note: this pattern deliberately has NO capture group - we use $m[0] (the whole
# match) and strip the surrounding backticks ourselves. An earlier version used
# a capture group but still read $m[1], which is null when the group is absent.
$rxRef   = '`(?:Docs|Tools|ClientStaging|Server|DeepSeekOutput|Client)[\\/][^`\s]*?\.(?:md|ps1|sql|cs|json|xml|asmdef|sln|csproj)`'

# Paths referenced as HANDOFF DESTINATIONS (where a staged file will be copied to
# once the user creates the Unity project in M0 step D1). They legitimately do not
# exist yet, so they must NOT be reported as broken.
$pendingDstPrefix = 'Client\'

# Files that were deliberately retired. A reference to one is a historical note,
# not a defect - but it is surfaced separately so stale links eventually get tidied.
$retired = @('Tools\Prepare-M0.ps1')

foreach ($f in $mdFiles) {
    $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
    $text = Get-Content $f.FullName -Raw -Encoding UTF8
    if ([string]::IsNullOrEmpty($text)) { continue }

    foreach ($m in [regex]::Matches($text, $rxRef)) {
        # strip the leading and trailing backtick from the whole match
        $ref = $m.Value.Trim('`').Replace('/', '\')
        # skip glob/placeholder patterns
        if ($ref -match '\*' -or $ref -match '\.\.\.' -or $ref -match 'YYYY|XX|<|>') { continue }

        $abs = Join-Path $Root $ref
        $exists = Test-Path $abs
        $isPlanned = $planned.Contains($ref)
        $isPending = $ref.StartsWith($pendingDstPrefix)
        $isRetired = $retired -contains $ref

        $kind = if ($exists) { 'OK' }
                elseif ($isRetired) { 'RETIRED' }
                elseif ($isPending) { 'PENDING' }
                elseif ($isPlanned) { 'PLANNED' }
                else { 'BROKEN' }
        $results.Add([pscustomobject]@{
            Kind = $kind
            From = $rel
            Ref  = $ref
        })
    }
}

$okCount      = @($results | Where-Object { $_.Kind -eq 'OK' }).Count
$plannedCount = @($results | Where-Object { $_.Kind -eq 'PLANNED' }).Count
$pendingCount = @($results | Where-Object { $_.Kind -eq 'PENDING' }).Count
$retiredCount = @($results | Where-Object { $_.Kind -eq 'RETIRED' }).Count
$brokenCount  = @($results | Where-Object { $_.Kind -eq 'BROKEN' }).Count

Write-Header '3. summary'

Write-Host ("  markdown files scanned : {0}" -f $mdFiles.Count)
Write-Host ("  references found       : {0}" -f $results.Count)
Write-Host ("  [OK]      exists            : {0}" -f $okCount) -ForegroundColor Green
Write-Host ("  [PLANNED] future deliverable: {0}" -f $plannedCount) -ForegroundColor Yellow
Write-Host ("  [PENDING] awaits step D1    : {0}" -f $pendingCount) -ForegroundColor Cyan
Write-Host ("  [RETIRED] deliberately gone : {0}" -f $retiredCount) -ForegroundColor Magenta
if ($brokenCount -gt 0) {
    Write-Host ("  [BROKEN]  needs fixing      : {0}" -f $brokenCount) -ForegroundColor Red
} else {
    Write-Host ("  [BROKEN]  needs fixing      : 0") -ForegroundColor Green
}
Write-Host ''
Write-Host '  Legend:' -ForegroundColor White
Write-Host '    OK      - target exists' -ForegroundColor Gray
Write-Host '    PLANNED - declared as a future deliverable in the requirements doc' -ForegroundColor Gray
Write-Host '    PENDING - a handoff destination under Client\; appears after M0 step D1' -ForegroundColor Gray
Write-Host '    RETIRED - a file deliberately removed (kept only as a historical note)' -ForegroundColor Gray
Write-Host '    BROKEN  - missing and unexplained; investigate (typo? wrong path? should exist?)' -ForegroundColor Gray

# ---------------------------------------------------------------------------
# 4. Detail: broken references (the actionable part)
# ---------------------------------------------------------------------------
if ($brokenCount -gt 0) {
    Write-Header '4. BROKEN references (these need attention)'
    $results | Where-Object { $_.Kind -eq 'BROKEN' } | Sort-Object Ref, From | ForEach-Object {
        Write-Host ("  {0}" -f $_.Ref) -ForegroundColor Red
        Write-Host ("      referenced by: {0}" -f $_.From) -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host '  For each one, decide:' -ForegroundColor White
    Write-Host '    - is it a typo or wrong relative path?          -> fix the reference' -ForegroundColor Gray
    Write-Host '    - is it a file that should exist by now?        -> create it' -ForegroundColor Gray
    Write-Host '    - is it a future deliverable I forgot to list?  -> add it to the' -ForegroundColor Gray
    Write-Host '      requirements doc deliverables table, so it is classified PLANNED' -ForegroundColor Gray
} else {
    Write-Host ''
    Write-Host '  No broken references. Every missing target is a declared future deliverable.' -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 5. Detail: planned (informational only)
# ---------------------------------------------------------------------------
if (-not $BrokenOnly -and $plannedCount -gt 0) {
    Write-Header '5. PLANNED references (expected - future deliverables)'
    $results | Where-Object { $_.Kind -eq 'PLANNED' } |
        Select-Object -ExpandProperty Ref -Unique | Sort-Object | ForEach-Object {
            Write-Host ("  {0}" -f $_) -ForegroundColor Yellow
        }
}

Write-Host ''
Write-Host '  Read-only report complete. Nothing was modified.' -ForegroundColor DarkGray
Write-Host ''

if ($brokenCount -gt 0) { exit 1 } else { exit 0 }
