# ============================================================================
#  Check-AsmdefBoundaries.ps1 —— 按 **asmdef 边界**分别编译（补上单程序集闸门的盲区）
#  项目：3D联网战斗Demo   对应：W10「闸门绿 ≠ Unity 绿」、Docs\00 §五
#
#  ---------------------------------------------------------------------------
#  它解决什么（一个反复咬人的盲区）
#  ---------------------------------------------------------------------------
#  `Server\_api-probe\ApiProbe.csproj` 把所有客户端源码编进**一个**程序集 ——
#  它验的是"语法 / API 存不存在"，但**没有"程序集边界"这回事**。
#
#  于是这一类错误它**永远抓不到**（CS0012 / CS0246 的跨程序集形态）：
#
#      error CS0012: The type 'IRewardLedger' is defined in an assembly that is not
#                    referenced. You must add a reference to assembly 'NBC.Shared'.
#
#  2026-09-26 真实发生：`BattleSession.FromConfigMgr` 多了一个**可选参数**
#  （类型在 `NBC.Shared`），而调用方 `M2DemoBehaviour`（在 `NBC.Boot`）没引用 `NBC.Shared`。
#  ⚠️ 新形态：**类型是通过「被调方法的签名」泄漏的，调用方源码里一个字都没提它** ——
#     所以 `AsmdefReferenceGuardTests`（按 `using` 扫）也看不见。
#
#  ---------------------------------------------------------------------------
#  做法：把每个 asmdef 的**文件集合**与**引用集合**照抄成 csproj，一个个真编
#  ---------------------------------------------------------------------------
#      · `<Compile Include>` = 该 asmdef 目录下的 `**\*.cs`
#        ⚠️ 但**排除嵌套 asmdef 的目录**（`Game\Model\Model.asmdef` 就在 `Game\` 里面 ——
#           不排除就会把 NBC.Model 的源码**也**编进 NBC.Game，边界当场失效）
#      · `<ProjectReference>` = 它 `references` 里那些"也是 asmdef"的
#      · `<Reference HintPath>` = 引擎模块 / 包程序集 / 第三方 DLL
#      · `noEngineReferences: true` 的程序集（`NBC.Shared`）**一个引擎引用都不给**
#        ⇒ 顺带把 D1/DET-02「共享层不许碰 UnityEngine」也变成**机械保证**
#
#  依赖顺序不用自己排：`ProjectReference` 交给 MSBuild 拓扑排序。
#
#  ⚠️ 用法（必须 -m:1，见 Docs\11 §四之二）：
#        pwsh -File Tools\Check-AsmdefBoundaries.ps1
#     或   powershell -ExecutionPolicy Bypass -File Tools\Check-AsmdefBoundaries.ps1
#
#  ⚠️ 这个脚本只读 `Client\` 和 `Server\` 的源码，产物全写在 `Server\_asmdef-probe\gen\`
#     （那个目录在 `Server\.gitignore` 里）。
# ============================================================================

[CmdletBinding()]
param(
    # 只生成 csproj、不编译（排查生成物时用）
    [switch]$GenerateOnly,

    # 保留上一次的生成物（默认每次重新生成，避免用到过期项目文件）
    [switch]$KeepGenerated
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'Client\Assets\_Project'
$probeDir = Join-Path $repoRoot 'Server\_asmdef-probe'
$genDir = Join-Path $probeDir 'gen'

$unityManaged = 'D:\SOFT\Unity\Hub\Editor\2022.3.62f3c1\Editor\Data\Managed'
$scriptAssemblies = Join-Path $repoRoot 'Client\Library\ScriptAssemblies'

# ---------------------------------------------------------------------------
#  引用名 → 程序集 DLL 的表（**不在 asmdef 里的**那些）
# ---------------------------------------------------------------------------
$precompiled = @{
    'YooAsset'                = Join-Path $scriptAssemblies 'YooAsset.dll'
    'Unity.TextMeshPro'       = Join-Path $scriptAssemblies 'Unity.TextMeshPro.dll'
    'UnityEngine.UI'          = Join-Path $scriptAssemblies 'UnityEngine.UI.dll'
    'UnityEngine.TestRunner'  = Join-Path $scriptAssemblies 'UnityEngine.TestRunner.dll'
    'UnityEditor.TestRunner'  = Join-Path $scriptAssemblies 'UnityEditor.TestRunner.dll'
    'nunit.framework'         = Join-Path $repoRoot 'Client\Library\PackageCache\com.unity.ext.nunit@1.0.6\net35\unity-custom\nunit.framework.dll'
    'Google.Protobuf'         = Join-Path $repoRoot 'Client\Assets\ThirdParty\GoogleProtobuf\Google.Protobuf.dll'
}

# ---------------------------------------------------------------------------
#  Unity 的**引擎模块**（asmdef 只要 noEngineReferences=false，Unity 就全给它）
#  ⇒ 这里也全给：**这不是"宽松"，这就是 Unity 的行为**
# ---------------------------------------------------------------------------
$engineModules = @(
    'UnityEngine',
    'UnityEngine.CoreModule',
    'UnityEngine.SharedInternalsModule',
    'UnityEngine.InputLegacyModule',
    'UnityEngine.AudioModule',
    'UnityEngine.UIModule',
    'UnityEngine.IMGUIModule',
    'UnityEngine.TextRenderingModule',
    'UnityEngine.TextCoreFontEngineModule'
)

function Get-EnginePath([string]$name) {
    if ($name -eq 'UnityEngine') { return (Join-Path $unityManaged 'UnityEngine\UnityEngine.dll') }
    return (Join-Path $unityManaged "UnityEngine\$name.dll")
}

# `precompiledReferences` 写的是 `Google.Protobuf.dll`，而表里的键是 `Google.Protobuf`
# ⇒ 统一去掉 `.dll` 再查表（两种写法都能对上）
function Normalize-DllName([string]$name) {
    if ($null -eq $name) { return '' }
    $n = $name.Trim()
    if ($n.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        return $n.Substring(0, $n.Length - 4)
    }
    return $n
}

$unityEditorDll = Join-Path $unityManaged 'UnityEditor.dll'

# ---------------------------------------------------------------------------
#  「**自动引用**的插件 DLL」——`overrideReferences: false` 时 Unity 会给的那些
#  ---------------------------------------------------------------------------
#  ⚠️ `overrideReferences` 的含义很容易记反，而它决定**要不要**补这一批：
#      · `false`（默认）= **不覆盖** ⇒ Unity 用它默认的那套预编译程序集
#        （即 `Assets\ThirdParty\` 下 autoReference 打开的插件 DLL）
#      · `true`         = **覆盖**   ⇒ 只用 `precompiledReferences` 里点名的那几个
#
#  实测（2026-09-26）：`NBC.Game` 的 `overrideReferences` 是 false、`precompiledReferences` 是空，
#  而 `Game\Net\NetSession.cs` 用了 `using Google.Protobuf;` —— 它能编过，
#  **完全靠这条默认规则**。⇒ 漏掉它就会让 NBC.Game 报 CS0246「找不到 Google」，
#  而且会**连带**把 Boot / Editor / Tests 全带红（它们都引用 Game）——
#  看着像"到处都错"，其实只有一个根因。
# ===========================================================================
$autoPlugins = @(
    'Google.Protobuf'
)

# ===========================================================================
#  一、扫出所有 asmdef
# ===========================================================================
$asmdefFiles = Get-ChildItem $projectDir -Recurse -Filter '*.asmdef' | Sort-Object FullName
if ($asmdefFiles.Count -lt 5) {
    throw "只扫到 $($asmdefFiles.Count) 个 asmdef，太少 —— 目录结构变了的话这个检查会**空过**（假绿）。"
}

$asmdefs = @{}
foreach ($f in $asmdefFiles) {
    $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
    $asmdefs[$json.name] = [pscustomobject]@{
        Name              = $json.name
        Dir               = $f.Directory.FullName
        References        = @($json.references)
        # ⚠️ asmdef 有**两个**引用字段，很容易只读一个（我第一版就漏了）：
        #    · `references`            = 其它 asmdef 的名字，**或**预编译程序集名
        #    · `precompiledReferences` = 预编译程序集，**写的是带 .dll 的文件名**
        #    而 `NBC.Protocol` 正是靠后者拿 `Google.Protobuf.dll`（它的 `references` 是空的）
        #    ⇒ 只读前者的后果：Protocol 报 CS0400「找不到 Google」，还会**连带**把
        #      Game / Boot / Editor / Tests 全带红（它们都引用 Protocol）——
        #      看起来像"到处都错"，其实只有一个根因。
        Precompiled       = @($json.precompiledReferences)
        OverrideRefs      = [bool]$json.overrideReferences
        NoEngine          = [bool]$json.noEngineReferences
        IncludePlatforms  = @($json.includePlatforms)
        RootNamespace     = $json.rootNamespace
    }
}

Write-Host "扫到 $($asmdefs.Count) 个 asmdef：$(( $asmdefs.Keys | Sort-Object ) -join ', ')"

# 每个目录属于哪个 asmdef（用于"嵌套 asmdef 排除"）
$dirToAsmdef = @{}
foreach ($a in $asmdefs.Values) { $dirToAsmdef[$a.Dir] = $a.Name }

function Get-NestedAsmdefDirs([string]$asmdefDir) {
    $result = @()
    foreach ($a in $asmdefs.Values) {
        if ($a.Dir -ne $asmdefDir -and $a.Dir.StartsWith($asmdefDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $result += $a.Dir
        }
    }
    return $result
}

# ===========================================================================
#  二、生成 csproj
# ===========================================================================
if (-not $KeepGenerated -and (Test-Path $genDir)) {
    Remove-Item $genDir -Recurse -Force
}
New-Item -ItemType Directory -Path $genDir -Force | Out-Null

$generated = @()

foreach ($a in ($asmdefs.Values | Sort-Object Name)) {
    $dir = Join-Path $genDir $a.Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<Project Sdk="Microsoft.NET.Sdk">')
    [void]$sb.AppendLine('  <PropertyGroup>')
    [void]$sb.AppendLine('    <TargetFramework>net8.0</TargetFramework>')
    [void]$sb.AppendLine("    <AssemblyName>$($a.Name)</AssemblyName>")
    [void]$sb.AppendLine("    <RootNamespace>$($a.Name)</RootNamespace>")
    # 与 Unity 2022.3 对齐：C# 9、无隐式 using、可空引用关闭
    [void]$sb.AppendLine('    <LangVersion>9.0</LangVersion>')
    [void]$sb.AppendLine('    <ImplicitUsings>disable</ImplicitUsings>')
    [void]$sb.AppendLine('    <Nullable>disable</Nullable>')
    [void]$sb.AppendLine('    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>')
    # ⚠️⚠️ **这一行是整个脚本的命门**（我第一版漏了它，于是这个闸门**抓不到它要抓的那个错**）：
    #
    #   MSBuild 的 `ProjectReference` **默认会把引用传递下去**
    #   （Boot → Game → Shared ⇒ Boot 也看得见 Shared），
    #   而 **Unity 的 asmdef 引用绝不传递** —— 这正是本项目记了 11 次以上的那条
    #   「asmdef / ProjectReference 引用不传递」。
    #
    #   后果（2026-09-26 实测）：我把那个"可选参数"变异加回去之后，
    #   单程序集闸门绿、**这个闸门也绿** —— 也就是说它当时是个**摆设**。
    #   加上这一行之后，同一个变异立刻让 NBC.Boot 报出与 Unity 一模一样的 CS0012。
    #
    #   📌 教训：**一个"新加的检查"必须先用变异证明它会红**，
    #      否则你只是多了一个看起来很像检查的东西（同「阴性对照天生容易假绿」那一族）。
    [void]$sb.AppendLine('    <DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>')
    [void]$sb.AppendLine('    <NoWarn>$(NoWarn);CS8019;CS0169;CS0649;CS0067</NoWarn>')
    [void]$sb.AppendLine('  </PropertyGroup>')

    # ---- 源码：本目录的全部 .cs，**排除嵌套 asmdef 的目录** ----
    [void]$sb.AppendLine('  <ItemGroup>')
    [void]$sb.AppendLine("    <Compile Include=`"$($a.Dir)\**\*.cs`">")
    foreach ($nested in (Get-NestedAsmdefDirs $a.Dir)) {
        [void]$sb.AppendLine("      <Exclude>`"$nested\**\*.cs`"</Exclude>")
    }
    [void]$sb.AppendLine('    </Compile>')
    [void]$sb.AppendLine('  </ItemGroup>')

    # ---- 引用 ----
    [void]$sb.AppendLine('  <ItemGroup>')
    $projectRefs = @()
    $dllRefs = @()

    foreach ($r in $a.References) {
        if ($null -eq $r -or $r -eq '') { continue }
        if ($asmdefs.ContainsKey($r)) {
            $projectRefs += $r
        }
        elseif ($precompiled.ContainsKey((Normalize-DllName $r))) {
            $dllRefs += (Normalize-DllName $r)
        }
        else {
            # 没登记的名字要**响亮地报出来**，不能静默跳过（那会让"少一个引用"变成假绿）
            Write-Warning "asmdef「$($a.Name)」的 references 里有个没登记的程序集「$r」—— 请在 `$precompiled 表里补上它的 HintPath。"
        }
    }

    # ---- `precompiledReferences`（**写的是带 .dll 的文件名**）----
    foreach ($p in $a.Precompiled) {
        if ($null -eq $p -or $p -eq '') { continue }
        $name = Normalize-DllName $p
        if ($precompiled.ContainsKey($name)) {
            if ($dllRefs -notcontains $name) { $dllRefs += $name }
        }
        else {
            Write-Warning "asmdef「$($a.Name)」的 precompiledReferences 里有个没登记的程序集「$p」—— 请在 `$precompiled 表里补上它的 HintPath。"
        }
    }

    # ---- `overrideReferences: false` ⇒ 补上 Unity 默认会给的**自动引用插件**（见 $autoPlugins 的说明）----
    if (-not $a.OverrideRefs) {
        foreach ($p in $autoPlugins) {
            if (($precompiled.ContainsKey($p)) -and ($dllRefs -notcontains $p)) {
                $dllRefs += $p
            }
        }
    }

    # 引擎模块（noEngineReferences=false 时**全给**，这就是 Unity 的行为）
    if (-not $a.NoEngine) {
        foreach ($m in $engineModules) {
            $p = Get-EnginePath $m
            if (Test-Path $p) {
                [void]$sb.AppendLine("    <Reference Include=`"$m`"><HintPath>$p</HintPath><Private>false</Private></Reference>")
            }
        }
        [void]$sb.AppendLine("    <Reference Include=`"UnityEditor`"><HintPath>$unityEditorDll</HintPath><Private>false</Private></Reference>")
    }

    foreach ($d in $dllRefs) {
        [void]$sb.AppendLine("    <Reference Include=`"$d`"><HintPath>$($precompiled[$d])</HintPath><Private>false</Private></Reference>")
    }

    foreach ($p in $projectRefs) {
        [void]$sb.AppendLine("    <ProjectReference Include=`"..\$p\$p.csproj`" />")
    }

    [void]$sb.AppendLine('  </ItemGroup>')
    [void]$sb.AppendLine('</Project>')

    $csprojPath = Join-Path $dir "$($a.Name).csproj"
    # ⚠️ 无 BOM 的 UTF-8（csproj 里有中文路径）
    [System.IO.File]::WriteAllText($csprojPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    $generated += $csprojPath
}

Write-Host "已生成 $($generated.Count) 个 csproj 到 $genDir"

if ($GenerateOnly) {
    Write-Host '（-GenerateOnly：不编译）'
    return
}

# ===========================================================================
#  三、编译（对**每个** asmdef 都编一遍）
#      ⚠️ 为什么要每个都编：只编一个"顶层"的话，如果那个顶层没人引用，
#        它的错误就永远不会被发现。全编一遍最直白。
# ===========================================================================
$failed = @()

foreach ($a in ($asmdefs.Values | Sort-Object Name)) {
    $csprojPath = Join-Path (Join-Path $genDir $a.Name) "$($a.Name).csproj"

    $output = & dotnet build $csprojPath -m:1 -v:q --nologo 2>&1
    $code = $LASTEXITCODE

    if ($code -ne 0) {
        $failed += $a.Name
        Write-Host "  [红] $($a.Name)" -ForegroundColor Red
        # 只打错误行（把 noise 滤掉，方便一眼看到 CS 编号）
        $output | Where-Object { $_ -match 'error [A-Z]+\d+' } | Select-Object -First 6 | ForEach-Object {
            Write-Host "        $($_.ToString().Trim())"
        }
    }
    else {
        Write-Host "  [绿] $($a.Name)"
    }
}

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "asmdef 边界检查：$($asmdefs.Count) 个程序集全绿 ✅"
    exit 0
}

Write-Host "asmdef 边界检查：$($failed.Count)/$($asmdefs.Count) 个程序集**编译失败** ❌ —— $($failed -join ', ')"
Write-Host '（这类错误单程序集闸门 _api-probe **抓不到**，只有这里能提前发现）'
exit 1
