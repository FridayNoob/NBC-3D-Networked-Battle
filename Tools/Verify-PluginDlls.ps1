<#
================================================================================
 Verify-PluginDlls.ps1 - verify third-party DLL assembly names and link.xml
 Project: 3D Networking Battle Demo (NBC)

 PURPOSE
   Two things in this project depend on "the assembly name inside a DLL file",
   and BOTH fail SILENTLY if the name is wrong:

     1. Assets\link.xml  - IL2CPP strip protection. If an <assembly fullname="..."
        /> entry does not match a real assembly name, Unity does not warn; the
        protection simply does not apply, and you only find out as a crash in an
        IL2CPP build (risk R4).
     2. Assets\ThirdParty\ProtobufNet\ - Unity needs the whole dependency closure
        (main package PLUS its transitive dependencies), not just the main DLL.

   This script reads the ACTUAL assembly names out of the DLL files on disk, so
   the link.xml entries and the required-DLL list can be confirmed by fact rather
   than by documentation.

 WHY A SCRIPT AND NOT A DOC
   Assembly names cannot be reliably inferred from package names. Example:
   the NuGet package "protobuf-net.Core" ships an assembly that must be verified
   on disk. Guessing here costs hours of AOT debugging later.

 THIS SCRIPT IS READ-ONLY
   It only reads files and prints a report. It does not modify anything.

 USAGE
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Verify-PluginDlls.ps1

   # Also check a custom DLL folder
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Verify-PluginDlls.ps1 -ExtraPaths "Client\Assets\ThirdParty"
================================================================================
#>

[CmdletBinding()]
param(
    [string[]]$ExtraPaths = @()
)

$ErrorActionPreference = 'Stop'

$Root         = Split-Path -Parent $PSScriptRoot
$ClientAssets = Join-Path $Root 'Client\Assets'
$LinkXml      = Join-Path $ClientAssets 'link.xml'

function Write-Header($t) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
}
function Write-Ok($t)   { Write-Host "  [OK]   $t" -ForegroundColor Green }
function Write-Miss($t) { Write-Host "  [MISS] $t" -ForegroundColor Yellow }
function Write-Bad($t)  { Write-Host "  [BAD]  $t" -ForegroundColor Red }
function Write-Info($t) { Write-Host "  [info] $t" -ForegroundColor DarkGray }

Write-Header 'VERIFY PLUGIN DLLS  -  assembly names and link.xml'

# ---------------------------------------------------------------------------
# 0. Pre-checks
# ---------------------------------------------------------------------------
if (-not (Test-Path $ClientAssets)) {
    Write-Bad "Unity project not found: $ClientAssets"
    Write-Info 'Create the Unity project first (M0 manual step D1), then re-run.'
    exit 1
}
Write-Ok "Unity project found: Client\Assets"

# ---------------------------------------------------------------------------
# 1. Collect candidate project paths
# ---------------------------------------------------------------------------
$searchPaths = New-Object System.Collections.Generic.List[string]

# Known locations the project expects
$thirdParty = Join-Path $ClientAssets 'ThirdParty'
if (Test-Path $thirdParty) { $searchPaths.Add($thirdParty) }

# Behavior Designer and A* Pathfinding (user-imported Asset Store plugins)
foreach ($folder in @('Behavior Designer', 'AstarPathfindingProject')) {
    $p = Join-Path $ClientAssets $folder
    if (Test-Path $p) { $searchPaths.Add($p) }
}

# Explicit extra paths
foreach ($rel in $ExtraPaths) {
    $p = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $Root $rel }
    if (Test-Path $p) { $searchPaths.Add($p) } else { Write-Miss "extra path not found: $rel" }
}

if ($searchPaths.Count -eq 0) {
    Write-Miss 'No candidate DLL folders found.'
    Write-Info 'Expected at least: Client\Assets\ThirdParty (protobuf-net)'
    Write-Info 'Import the plugins per M0 manual D6-D10, then re-run.'
    exit 0
}

Write-Host ''
Write-Host '  Searching:' -ForegroundColor DarkGray
foreach ($p in $searchPaths) { Write-Info $p.Substring($Root.Length).TrimStart('\') }

# ---------------------------------------------------------------------------
# 2. Read assembly names from DLLs
# ---------------------------------------------------------------------------
Write-Header 'DLL assembly names found on disk'

$found = @{}          # assemblyName (lower) -> list of file paths
$dllList = New-Object System.Collections.Generic.List[object]

foreach ($base in $searchPaths) {
    # Exclude Unity's own plugin folders that are unrelated, and skip huge trees
    Get-ChildItem -Path $base -Recurse -Filter *.dll -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $an = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName)
            $name = $an.Name
            if (-not $found.ContainsKey($name.ToLower())) { $found[$name.ToLower()] = @() }
            $found[$name.ToLower()] += $_.FullName
            $dllList.Add([pscustomobject]@{
                Assembly = $name
                Version  = $an.Version.ToString()
                File     = $_.Name
                RelPath  = $_.FullName.Substring($Root.Length).TrimStart('\')
                KB       = [math]::Round($_.Length / 1KB, 1)
            })
        } catch {
            Write-Bad "cannot read assembly name: $($_.Name)  ($($_.Exception.Message))"
        }
    }
}

if ($dllList.Count -eq 0) {
    Write-Miss 'No readable DLLs found in the searched folders.'
    exit 0
}

# ---------------------------------------------------------------------------
# 2b. Index Unity-compiled assemblies (outside Assets) for link.xml validation
#
# WHY THIS IS NEEDED
#   IL2CPP strips the assemblies Unity *compiles*, not only the DLLs that sit in
#   Assets. When a plugin ships loose .cs files with NO .asmdef, its code lands in
#   Assembly-CSharp - so a correct link.xml entry may name "Assembly-CSharp".
#   That is exactly how xLua is packaged (verified 2026-09-20: Assembly-CSharp.dll
#   contains 109 XLua.* types). Without this index such an entry is reported as a
#   false "no matching DLL" defect.
#
#   These assemblies are only used to validate link.xml names. They are NOT added
#   to the plugin DLL listing above, to keep that report about imported plugins.
# ---------------------------------------------------------------------------
$foundGenerated = @{}
$scriptAsmDir = Join-Path $Root 'Client\Library\ScriptAssemblies'
if (Test-Path $scriptAsmDir) {
    Get-ChildItem -Path $scriptAsmDir -Filter *.dll -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $anGen = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName)
            $foundGenerated[$anGen.Name.ToLower()] = $_.Name
        } catch { }
    }
    Write-Info "Unity-compiled assemblies indexed for link.xml check: $($foundGenerated.Count)"
} else {
    Write-Info 'Library\ScriptAssemblies not found - open the Unity project once so it compiles, then re-run.'
}

Write-Host ''
Write-Host ('  {0,-32} {1,-16} {2}' -f 'ASSEMBLY NAME', 'VERSION', 'FILE') -ForegroundColor White
Write-Host ('  ' + ('-' * 74)) -ForegroundColor DarkGray
foreach ($d in ($dllList | Sort-Object Assembly)) {
    Write-Host ('  {0,-32} {1,-16} {2}' -f $d.Assembly, $d.Version, $d.File)
}
Write-Host ''
Write-Info "total DLLs read: $($dllList.Count)"

# ---------------------------------------------------------------------------
# 3. Check the protobuf-net dependency closure
# ---------------------------------------------------------------------------
Write-Header 'protobuf-net dependency closure (Unity needs ALL of these)'

# Required assemblies, verified empirically on 2026-09-20 by reading the DLLs'
# own assembly-reference metadata (Assembly.ReflectionOnlyLoadFrom + GetReferencedAssemblies).
# The real chain is:
#   protobuf-net.dll            -> protobuf-net.Core + System.Collections.Immutable
#   protobuf-net.Core.dll       -> System.Collections.Immutable
#   System.Collections.Immutable-> System.Memory + System.Runtime.CompilerServices.Unsafe
# Unity 2022.3 provides System.Memory / System.Buffers / System.Numerics.Vectors via
# its NetStandard shim folder, BUT NOT System.Collections.Immutable and NOT
# System.Runtime.CompilerServices.Unsafe - so those two must ship with the plugin.
$required = @(
    @{ Name = 'protobuf-net';                           Why = 'main package assembly' }
    @{ Name = 'protobuf-net.Core';                      Why = 'transitive dependency of protobuf-net' }
    @{ Name = 'System.Collections.Immutable';           Why = 'transitive dependency (NOT provided by Unity)' }
    @{ Name = 'System.Runtime.CompilerServices.Unsafe'; Why = 'dependency of Immutable (NOT provided by Unity)' }
)

$missing = @()
foreach ($r in $required) {
    $key = $r.Name.ToLower()
    if ($found.ContainsKey($key)) {
        Write-Ok "$($r.Name)   ($($r.Why))"
    } else {
        Write-Miss "$($r.Name)   ($($r.Why))"
        $missing += $r.Name
    }
}

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Info 'If any of the above is missing, Unity will fail to compile with a'
    Write-Info '"type is defined in an assembly that is not referenced" style error.'
    Write-Info 'See Docs\05-dependency manifest section 2.2 for the restore script.'
}

# ---------------------------------------------------------------------------
# 4. Validate link.xml entries against real assembly names
# ---------------------------------------------------------------------------
Write-Header 'link.xml validation'

if (-not (Test-Path $LinkXml)) {
    Write-Miss 'Assets\link.xml not found - IL2CPP strip protection is NOT in place.'
    Write-Info 'Copy it from ClientStaging\link.xml by hand (see M0 manual, section 1.3).'
} else {
    Write-Ok "found: Assets\link.xml"

    try {
        [xml]$xml = Get-Content $LinkXml -Raw -Encoding UTF8
    } catch {
        Write-Bad "link.xml is not valid XML: $($_.Exception.Message)"
        $xml = $null
    }

    if ($xml) {
        $entries = @($xml.linker.assembly)
        Write-Info ("declared entries: {0}" -f $entries.Count)

        # An entry may be annotated in link.xml as intentionally pending because the
        # plugin has not been imported yet, e.g.:
        #     <!-- pending:plugin-not-imported (xLua, M0 step D6) -->
        #     <assembly fullname="XLua" preserve="all" />
        #     <assembly fullname="XLua.Example" preserve="all" />
        # Such entries legitimately match nothing yet, so they must NOT be reported as
        # defects. One annotation may cover SEVERAL following <assembly> elements, so we
        # collect every assembly up to the next comment (an earlier version matched only
        # the first one and produced a false positive for the second).
        $rawLink = Get-Content $LinkXml -Raw -Encoding UTF8
        $pending = New-Object System.Collections.Generic.HashSet[string]
        foreach ($block in [regex]::Matches($rawLink, '<!--\s*pending:plugin-not-imported.*?-->(.*?)(?=<!--|</linker>)', 'Singleline')) {
            foreach ($a in [regex]::Matches($block.Groups[1].Value, '<assembly\s+fullname="([^"]+)"')) {
                [void]$pending.Add($a.Groups[1].Value)
            }
        }

        $unmatched = @()
        $pendingMissing = @()
        foreach ($e in $entries) {
            $fn = $e.fullname
            if ([string]::IsNullOrWhiteSpace($fn)) { continue }
            if ($found.ContainsKey($fn.ToLower())) {
                Write-Ok "link.xml entry matches a real assembly: $fn"
            } elseif ($foundGenerated.ContainsKey($fn.ToLower())) {
                Write-Ok "link.xml entry matches a Unity-compiled assembly: $fn  (Library\ScriptAssemblies\$($foundGenerated[$fn.ToLower()]))"
            } elseif ($pending.Contains($fn)) {
                Write-Info "pending (annotated as not-yet-imported plugin): $fn"
                $pendingMissing += $fn
            } else {
                Write-Miss "link.xml entry has NO matching DLL: $fn"
                $unmatched += $fn
            }
        }

        if ($pendingMissing.Count -gt 0) {
            Write-Host ''
            Write-Info ("{0} entry/entries marked 'pending:plugin-not-imported' - expected, verify again after importing that plugin." -f $pendingMissing.Count)
        }

        Write-Host ''
        if ($unmatched.Count -gt 0) {
            Write-Bad ("{0} link.xml entry/entries do not match any DLL found on disk." -f $unmatched.Count)
            Write-Info 'A non-matching entry is SILENTLY ignored by Unity - no warning, no error.'
            Write-Info 'Consequences: the protection does not apply, and you may see a crash'
            Write-Info 'or wrong data only in an IL2CPP build (risk R4).'
            Write-Info ''
            Write-Info 'Action: either fix the name to the exact assembly name listed in'
            Write-Info 'section 2 above, or remove the stale entry.'
            Write-Info 'Note: an entry may be legitimately absent if that plugin was not imported.'
        } else {
            Write-Ok 'All link.xml entries match real assemblies.'
        }

        # Reverse view: for each DLL that looks like a reflection-dependent library,
        # show which link.xml entry (if any) covers it. This is the actionable output:
        # it tells you the EXACT fullname string to put in link.xml.
        Write-Host ''
        Write-Info 'Reverse check - reflection-dependent DLLs and the link.xml entry that covers them:'
        $declaredNames = @($entries | ForEach-Object { $_.fullname })
        $deps = $dllList | Where-Object {
            $n = $_.Assembly.ToLower()
            $n -like '*protobuf*' -or $n -like '*lua*' -or $n -like '*immutable*'
        } | Sort-Object Assembly -Unique

        if (-not $deps) {
            Write-Info '  (no protobuf / lua / immutable assemblies found on disk yet)'
        } else {
            foreach ($d in $deps) {
                if ($declaredNames -contains $d.Assembly) {
                    Write-Ok ("covered by link.xml : {0}" -f $d.Assembly)
                } else {
                    Write-Miss ("NOT in link.xml    : {0}" -f $d.Assembly)
                    Write-Info ("    -> add:  <assembly fullname=`"{0}`" preserve=`"all`" />" -f $d.Assembly)
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# 5. Report project asset folders (for completeness)
# ---------------------------------------------------------------------------
Write-Header 'Asset folder inventory'

$inventory = @(
    @{ p = '_Project\Framework';   need = 'NBC.Framework source (M1 fills this)' }
    @{ p = '_Project\Game\Model';  need = 'NBC.Model - pure C#, noEngineReferences' }
    @{ p = '_Project\Game';        need = 'NBC.Game - View/Controller/Service' }
    @{ p = '_Project\Editor';      need = 'NBC.Editor tools' }
    @{ p = '_Project\Tests\Manual';need = 'NBV0 AOT probe script' }
    @{ p = 'LuaScripts';           need = 'Lua sources (hot-update target)' }
    @{ p = 'Scenes';               need = 'Unity scenes' }
    @{ p = 'ThirdParty\ProtobufNet'; need = 'protobuf-net DLLs (4 needed: net + Core + Immutable + Unsafe)' }
    @{ p = 'ThirdParty\XLua';      need = 'xLua sources' }
)
foreach ($i in $inventory) {
    $full = Join-Path $ClientAssets $i.p
    if (Test-Path $full) {
        $n = (Get-ChildItem $full -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
        Write-Ok ("{0,-34} {1,4} files   {2}" -f $i.p, $n, $i.need)
    } else {
        Write-Miss ("{0,-34}       -        {1}" -f $i.p, $i.need)
    }
}

Write-Host ''
Write-Host '  Read-only report complete. Nothing was modified.' -ForegroundColor DarkGray
Write-Host ''
