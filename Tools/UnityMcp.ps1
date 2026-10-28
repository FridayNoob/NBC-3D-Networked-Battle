# UnityMcp.ps1 -- one-shot bridge to the Unity Editor through the AnkleBreaker MCP server.
#
# Why this exists: DSH has no MCP client registered in this build, so the AI cannot call
# unity_* tools directly. This wrapper lets either of us run one read-only MCP call and get
# the JSON/text back on stdout. It is tooling, not game code.
#
# Usage:
#   .\Tools\UnityMcp.ps1 -Tool unity_editor_state
#   .\Tools\UnityMcp.ps1 -Tool unity_scene_hierarchy -Arguments '{"depth":2}'
#   .\Tools\UnityMcp.ps1 -Tool unity_console_logs -Raw
#
# ASCII only on purpose: .ps1 files with non-ASCII must carry a BOM (rule W7c).

param(
    [Parameter(Mandatory = $false)][string]$Tool,
    [string]$Arguments = '{}',
    [switch]$Raw,
    [switch]$List,
    [switch]$Schema,
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'

if (-not $List -and -not $Schema -and [string]::IsNullOrWhiteSpace($Tool)) {
    throw 'one of -List / -Schema / -Tool is required'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $PSScriptRoot 'unity-mcp-call.js'
if (-not (Test-Path $helper)) { throw "helper not found: $helper" }

# Where the Unity plugin records which port it grabbed (7890-7899, dynamic).
$instancesFile = Join-Path $env:LOCALAPPDATA 'UnityMCP\instances.json'
if (Test-Path $instancesFile) {
    $instances = Get-Content $instancesFile -Raw | ConvertFrom-Json
    $live = @($instances | Where-Object { $_.projectPath -like "$repoRoot\Client*" })
    if ($live.Count -eq 0) { $live = @($instances) }
    if ($live.Count -gt 0) {
        Write-Host ("[UnityMcp] editor for {0} on port {1} (last seen {2})" -f $live[0].projectName, $live[0].port, $live[0].lastSeen)
    }
} else {
    Write-Host '[UnityMcp] no instances.json -- is the Unity Editor open with the MCP plugin?'
}

$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw 'node not found on PATH' }

if ($List) {
    $out = & $node $helper '--list' $Filter 2>$null
} elseif ($Schema) {
    $out = & $node $helper '--schema' $Tool 2>$null
} else {
    # JSON goes through a temp file: PowerShell mangles inline quotes when calling native exes.
    $argsFile = Join-Path $env:TEMP ("nbc-mcp-args-{0}.json" -f $PID)
    [System.IO.File]::WriteAllText($argsFile, $Arguments, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $out = & $node $helper $Tool $argsFile 2>$null
    } finally {
        Remove-Item $argsFile -ErrorAction SilentlyContinue
    }
}
$code = $LASTEXITCODE

if ($Raw) {
    $out
} else {
    # Pretty-print when the payload is JSON, otherwise pass text through unchanged.
    foreach ($chunk in $out) {
        $text = [string]$chunk
        if ($text.TrimStart().StartsWith('{')) {
            try { $text | ConvertFrom-Json | ConvertTo-Json -Depth 8 } catch { $text }
        } else {
            $text
        }
    }
}

exit $code
