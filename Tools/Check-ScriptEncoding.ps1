# ============================================================================
#  Check-ScriptEncoding.ps1 - read-only encoding/syntax check for .ps1 scripts
#  Rule: W7 / W7a / W7b / W7c (see Docs\00, the project rules document)
#
#  ---------------------------------------------------------------------------
#  Why this file is pure ASCII (no Chinese at all)
#  ---------------------------------------------------------------------------
#  This machine's shell is Windows PowerShell 5.1. It decodes a .ps1 WITHOUT a
#  BOM using the system ANSI code page (GBK here). So:
#      .ps1 with non-ASCII  -> MUST be saved as UTF-8 WITH BOM
#      .ps1 pure ASCII      -> no BOM (the repo's W4 convention)
#  A checker that is itself broken cannot check anything, so this one stays
#  pure ASCII and therefore needs no BOM.
#
#  ---------------------------------------------------------------------------
#  Why it exists (incident 2026-09-26)
#  ---------------------------------------------------------------------------
#  I created the diagram folder's render.ps1 (Chinese comments/messages) WITHOUT
#  a BOM. PowerShell read it as GBK -> string literals mangled -> some mangled
#  bytes formed invalid tokens -> "Array index expression is missing or not valid."
#  The old self-check in Docs\00 only swept `Tools\*.ps1`, so it never looked
#  at the new file. This script sweeps the WHOLE repo, and also runs the real
#  PowerShell parser (that is what actually failed), instead of trusting my eyes.
#
#  ---------------------------------------------------------------------------
#  Usage
#  ---------------------------------------------------------------------------
#      powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Check-ScriptEncoding.ps1
#      powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Check-ScriptEncoding.ps1 -Root C:\TEMP\ps1check
#
#  Exit code: 0 = all good, 1 = at least one problem (BAD encoding or parse error)
# ============================================================================

[CmdletBinding()]
param(
    # Directory to sweep recursively. Defaults to the repo root (parent of Tools).
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

# Directories that never contain source scripts we care about.
$skipParts = @('\bin\', '\obj\', '\node_modules\', '\Library\', '\Temp\', '\.git\', '\.vs\', '\Builds\')

$files = Get-ChildItem -Path $Root -Filter *.ps1 -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $full = $_.FullName
        -not ($skipParts | Where-Object { $full.Contains($_) })
    } |
    Sort-Object FullName

if (-not $files) {
    Write-Output "[info] no .ps1 found under $Root"
    exit 0
}

$rows = @()
$badEncoding = 0
$badSyntax = 0

foreach ($f in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)

    # count non-ASCII bytes
    $nonAscii = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 127) { $nonAscii++ }
    }

    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)

    if ($nonAscii -eq 0 -and -not $hasBom) {
        $verdict = "OK (pure ASCII, no BOM)"
    }
    elseif ($nonAscii -gt 0 -and $hasBom) {
        $verdict = "OK (non-ASCII, BOM present)"
    }
    elseif ($nonAscii -gt 0 -and -not $hasBom) {
        $verdict = "BAD (non-ASCII WITHOUT BOM -> PowerShell 5.1 reads it as GBK)"
        $badEncoding++
    }
    else {
        $verdict = "BAD (pure ASCII but has a BOM -> breaks repo convention W4)"
        $badEncoding++
    }

    # the real verdict: ask the PowerShell parser (that is what actually failed)
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errors)
    $errCount = 0
    if ($errors -and $errors.Count) { $errCount = $errors.Count }
    if ($errCount -gt 0) { $badSyntax++ }

    $rel = $f.FullName
    if ($rel.StartsWith($Root)) { $rel = $rel.Substring($Root.Length).TrimStart('\') }

    $rows += [pscustomobject]@{
        Script      = $rel
        NonAscii    = $nonAscii
        Bom         = $hasBom
        ParseErrors = $errCount
        Verdict     = $verdict
    }
}

# Render the table to plain text first: piping raw Format-* objects into another
# command (e.g. `| Select-Object -Last 3`) makes PowerShell throw
# "GroupEndData is not valid or not in the correct sequence". A read-only tool
# must survive being composed with other commands.
Write-Output ($rows | Format-Table -AutoSize | Out-String).TrimEnd()

Write-Output ("scanned {0} script(s): encoding problems = {1}, parse errors = {2}" -f $files.Count, $badEncoding, $badSyntax)

if ($badEncoding -gt 0 -or $badSyntax -gt 0) { exit 1 }
exit 0
