# ============================================================================
#  Check-FloatFree.ps1 - "logic layer must not use floating point" gate
#  Project: NBC 3D Networked Battle Demo   Ref: Docs\27 s29.2 (criterion 1)
#
#  WHY A GATE (and not "the type system will stop us")
#      Fix64 publishes FromFloat/FromDouble/ToFloat/ToDouble on purpose
#      (the presentation layer needs them at the boundary), and the logic
#      layer can call them just as easily. So "no float in deterministic
#      logic" CANNOT be enforced by types - it has to be scanned for.
#
#  WHAT IT DOES
#      * scans *.cs under -Path (default: the shared battle logic folder)
#      * STRIPS COMMENTS FIRST, then matches float / double / Mathf /
#        Vector3 / Quaternion / Time.deltaTime / UnityEngine
#        (without stripping comments this gate is pure noise: the battle
#         folder's 3 hits are ALL comments explaining why float is banned)
#      * reports file:line for every hit
#      * files listed in -Allow are skipped (presentation-boundary files)
#
#  EXIT CODES
#      0 = clean
#      1 = hits found, OR nothing was scanned (see the W17 rule below)
#      2 = bad arguments
#
#  W17 GUARD: a gate that scans zero files must never report success.
#      An empty result usually means a wrong -Path, not a clean repo.
#
#  KNOWN LIMITATION (stated, not hidden)
#      comment stripping is regex-based: a string literal containing "//"
#      or "/*" could confuse it. This gate is a coarse net for a coarse
#      rule (word-level presence), so that is acceptable - it is not a
#      C# parser.
# ============================================================================

[CmdletBinding()]
param(
    # Folders (relative to the repo root) to scan.
    #
    # NOTE (2026-09-28): this used to be only Shared\Battle. Scanning the WHOLE
    # Shared folder is deliberately stricter: a NEW deterministic logic folder
    # (e.g. Shared\Sim for lockstep) would otherwise be a BLIND SPOT -- the gate
    # would keep passing while nobody scanned the new code.
    # Measured: whole Shared = 15 files, 0 hits (with the 3 fix files whitelisted).
    [string[]] $Path = @("Client\Assets\_Project\Shared"),

    # File names (no folder) that are allowed to mention floating point.
    #
    # The three fix-point types ARE the boundary: FromFloat/ToFloat exist on
    # purpose (Docs\27 s29.2 criterion 1). Everything else under Shared is logic.
    [string[]] $Allow = @("Fix64.cs", "FixMath.cs", "FixVector3.cs"),

    # Repo root (null = parent of this script's folder; resolved lazily -
    # $PSScriptRoot is EMPTY inside param() defaults under Windows
    # PowerShell 5.1, which is this machine's shell; see Docs\00 section 5).
    [string] $Root = $null
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }

# The words that must not appear in deterministic logic.
$Pattern = '\b(float|double|Mathf|Vector3|Quaternion|UnityEngine)\b|Time\.deltaTime'

function Strip-Comments([string] $text)
{
    # /* ... */ (may span lines), then // ... to end of line.
    $noBlock = [regex]::Replace($text, '(?s)/\*.*?\*/', ' ')
    return [regex]::Replace($noBlock, '//[^\r\n]*', ' ')
}

Write-Output ""
Write-Output ("=" * 78)
Write-Output "  FLOAT-FREE CHECKER  -  logic layer must not use float/double"
Write-Output ("=" * 78)

$files = @()
$missing = @()

foreach ($dir in $Path)
{
    $full = if ([System.IO.Path]::IsPathRooted($dir)) { $dir } else { Join-Path $Root $dir }

    if (-not (Test-Path $full))
    {
        $missing += $full
        continue
    }

    $files += Get-ChildItem -Path $full -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue
}

if ($missing.Count -gt 0)
{
    Write-Output ("ERROR: these -Path entries do not exist: " + ($missing -join ", "))
    Write-Output "       A gate that silently scans less must never report success."
    exit 1
}

$scanned = 0
$hits = @()

foreach ($file in $files)
{
    if ($Allow -contains $file.Name) { continue }

    $scanned++
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $code = Strip-Comments $text

    $lineNo = 0

    foreach ($line in ($code -split "`n"))
    {
        $lineNo++

        if ($line -match $Pattern)
        {
            $rel = $file.FullName.Replace($Root + [System.IO.Path]::DirectorySeparatorChar, "")
            $hits += ("  " + $rel + ":" + $lineNo + "  ->  " + $line.Trim())
        }
    }
}

Write-Output ("C# files scanned : " + $scanned)
Write-Output ("hits             : " + $hits.Count)

if ($scanned -eq 0)
{
    Write-Output ""
    Write-Output "ERROR: scanned 0 C# file -- that is almost always a wrong -Path,"
    Write-Output "       NOT a clean repo. A gate that scans nothing must never"
    Write-Output "       report success."
    exit 1
}

if ($hits.Count -gt 0)
{
    Write-Output ""
    Write-Output "FLOATING POINT IN DETERMINISTIC LOGIC (this breaks lockstep):"
    foreach ($h in $hits) { Write-Output $h }
    Write-Output ""
    Write-Output "Fix: use Fix64 / FixVector3 / FixMath (shared layer). If a hit is a"
    Write-Output "     presentation-boundary conversion point, add the file name to"
    Write-Output "     -Allow (and say why in a comment next to it)."
    exit 1
}

Write-Output ""
Write-Output "PASSED: no floating point in the scanned logic folders."
exit 0
