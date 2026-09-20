<#
================================================================================
 Inspect-AssemblyRefs.ps1 - read-only assembly metadata inspector
 Project: 3D Networking Battle Demo (NBC)

 PURPOSE
   Shows what an assembly is and what it depends on, WITHOUT using a decompiler or
   any Unity Editor operation. Used by M0 step D14 to prove a claim by fact rather
   than assertion:

     Claim: "Unity assembly X cannot be referenced by the pure .NET server process
             because X depends on other Unity assemblies / on a Unity-only
             framework that a plain net8.0 console project does not have."

   This script answers the "what does it depend on" half. The compiler answers the
   "can net8.0 actually reference it" half (see Docs\12 for the companion step).

 WHY IT MATTERS FOR THIS PROJECT
   The architecture decision "AI runs in two implementations" (IAgentBrain, see
   Docs\01 section 5.4.5 / 9.6) rests on the fact that Behavior Designer - and any
   Unity assembly - cannot run inside the standalone .NET server. Verifying the
   dependency chain here makes that a measured fact instead of an assumption.

 USAGE
   # Inspect one or more assemblies
   # NOTE: both Unity editors on this machine work here. Since 2026-09-20 the
   #       project uses 2022.3.62f3c1 (installed on D: via the Unity Hub
   #       secondary install path); the older 2022.3.17f1c1 is kept for RPG_Git
   #       and for reproducing the D14 probe output verbatim. Either path is fine -
   #       this script only reads assembly metadata.
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Inspect-AssemblyRefs.ps1 `
       -Path "D:\SOFT\Unity\Hub\Editor\2022.3.62f3c1\Editor\Data\Managed\UnityEngine\UnityEngine.CoreModule.dll"

   # Inspect every DLL in a folder (summary only)
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Inspect-AssemblyRefs.ps1 `
       -Folder "Client\Assets\ThirdParty\ProtobufNet"

   # Follow the dependency chain automatically (up to -Depth levels)
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Inspect-AssemblyRefs.ps1 `
       -Path "<...>\UnityEngine.CoreModule.dll" -SearchFolder "<...>\Managed\UnityEngine" -Depth 3

 NOTES
   - Read-only. Loads assemblies in reflection-only mode; executes nothing.
   - ReflectionOnlyLoadFrom is used instead of Assembly.LoadFrom so that no code
     from the inspected assembly ever runs.
================================================================================
#>

[CmdletBinding()]
param(
    [string[]]$Path = @(),
    [string]$Folder,
    [string]$SearchFolder,
    [int]$Depth = 1
)

$ErrorActionPreference = 'Continue'
$Root = Split-Path -Parent $PSScriptRoot

function Write-Header($t) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
}
function Info($t) { Write-Host "  [info] $t" -ForegroundColor DarkGray }
function Ok($t)   { Write-Host "  [ok]   $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  [warn] $t" -ForegroundColor Yellow }

function Resolve-Target([string]$p) {
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    return (Join-Path $Root $p)
}

# ---------------------------------------------------------------------------
# Collect targets
# ---------------------------------------------------------------------------
$targets = New-Object System.Collections.Generic.List[string]
foreach ($p in $Path) {
    $full = Resolve-Target $p
    if (Test-Path $full) { $targets.Add((Resolve-Path $full).Path) }
    else { Warn "not found: $p" }
}
if ($Folder) {
    $f = Resolve-Target $Folder
    if (Test-Path $f) {
        Get-ChildItem $f -Recurse -Filter *.dll | ForEach-Object { $targets.Add($_.FullName) }
    } else { Warn "folder not found: $Folder" }
}

if ($targets.Count -eq 0) {
    Write-Header 'INSPECT ASSEMBLY REFS'
    Warn 'no targets. Pass -Path <dll> or -Folder <dir>.'
    exit 1
}

# Build a lookup of "assembly name -> file path" so we can follow the chain
$index = @{}
if ($SearchFolder) {
    $sf = Resolve-Target $SearchFolder
    if (Test-Path $sf) {
        foreach ($f in Get-ChildItem $sf -Recurse -Filter *.dll) {
            try {
                $n = [System.Reflection.AssemblyName]::GetAssemblyName($f.FullName).Name
                if (-not $index.ContainsKey($n.ToLower())) { $index[$n.ToLower()] = $f.FullName }
            } catch { }
        }
        Info "indexed $($index.Count) assemblies from $sf"
    } else { Warn "search folder not found: $SearchFolder" }
}

# ---------------------------------------------------------------------------
# Inspect
# ---------------------------------------------------------------------------
function Get-AssemblyInfo([string]$file) {
    $result = [ordered]@{
        File    = $file
        Name    = $null
        Version = $null
        SizeKB  = [math]::Round((Get-Item $file).Length / 1KB, 1)
        Refs    = @()
        Error   = $null
    }
    try {
        $an = [System.Reflection.AssemblyName]::GetAssemblyName($file)
        $result.Name = $an.Name
        $result.Version = $an.Version.ToString()
    } catch {
        $result.Error = "AssemblyName: $($_.Exception.Message)"
        return $result
    }
    try {
        $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($file)
        $result.Refs = @($asm.GetReferencedAssemblies() | ForEach-Object { $_.Name } | Sort-Object)
    } catch {
        $result.Error = "ReflectionOnlyLoadFrom: $($_.Exception.Message)"
    }
    return $result
}

$seen = New-Object System.Collections.Generic.HashSet[string]
$queue = New-Object System.Collections.Generic.Queue[object]
foreach ($t in $targets) { $queue.Enqueue([pscustomobject]@{ File = $t; Level = 0 }) }

$reports = New-Object System.Collections.Generic.List[object]

while ($queue.Count -gt 0) {
    $item = $queue.Dequeue()
    $key = $item.File.ToLower()
    if ($seen.Contains($key)) { continue }
    [void]$seen.Add($key)

    $info = Get-AssemblyInfo $item.File
    $info | Add-Member -NotePropertyName Level -NotePropertyValue $item.Level -Force
    $reports.Add($info)

    # Follow the chain only when we have an index and budget left
    if ($index.Count -gt 0 -and $item.Level -lt $Depth) {
        foreach ($r in $info.Refs) {
            $rk = $r.ToLower()
            if ($index.ContainsKey($rk)) {
                $childFile = $index[$rk]
                if (-not $seen.Contains($childFile.ToLower())) {
                    $queue.Enqueue([pscustomobject]@{ File = $childFile; Level = $item.Level + 1 })
                }
            }
        }
    }
}

Write-Header 'ASSEMBLY REFERENCE INSPECTOR'

foreach ($r in ($reports | Sort-Object Level, Name)) {
    $indent = '  ' * $r.Level
    Write-Host ''
    Write-Host ("{0}{1}" -f $indent, $r.Name) -ForegroundColor White
    Write-Host ("{0}  version : {1}" -f $indent, $r.Version) -ForegroundColor DarkGray
    Write-Host ("{0}  size    : {1} KB" -f $indent, $r.SizeKB) -ForegroundColor DarkGray
    Write-Host ("{0}  file    : {1}" -f $indent, $r.File) -ForegroundColor DarkGray
    if ($r.Error) { Write-Host ("{0}  ERROR   : {1}" -f $indent, $r.Error) -ForegroundColor Red }
    if ($r.Refs.Count -gt 0) {
        Write-Host ("{0}  references ({1}):" -f $indent, $r.Refs.Count) -ForegroundColor DarkGray
        foreach ($rf in $r.Refs) {
            $short = if ($rf -match 'UnityEngine|UnityEditor') { $rf } else { "$rf  (framework)" }
            Write-Host ("{0}     - {1}" -f $indent, $short) -ForegroundColor Gray
        }

        # Highlight Unity-only dependencies - they are the reason a plain .NET
        # project cannot reference this assembly.
        $unityRefs = @($r.Refs | Where-Object { $_ -match '^Unity' })
        if ($unityRefs.Count -gt 0) {
            Write-Host ("{0}  => depends on {1} Unity assembly/assemblies: {2}" -f `
                $indent, $unityRefs.Count, ($unityRefs -join ', ')) -ForegroundColor Yellow
        }
    }
}

Write-Header 'SUMMARY'
Write-Host ("  assemblies inspected : {0}" -f $reports.Count)
$withUnityRefs = @($reports | Where-Object { @($_.Refs | Where-Object { $_ -match '^Unity' }).Count -gt 0 })
Write-Host ("  depending on Unity   : {0}" -f $withUnityRefs.Count) -ForegroundColor Yellow
if ($withUnityRefs.Count -gt 0) {
    Write-Host ''
    Info 'These assemblies reference other Unity assemblies. A plain net8.0 project'
    Info 'can only reference them if it also supplies every Unity dependency - which is'
    Info 'what M0 step D14 verifies with the compiler.'
}
Write-Host ''
Write-Host '  Read-only report complete. Nothing was executed or modified.' -ForegroundColor DarkGray
Write-Host ''
