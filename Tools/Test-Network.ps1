<#
================================================================================
 Test-Network.ps1 - read-only HTTPS / proxy / NuGet connectivity diagnostic
 Project: 3D Networking Battle Demo (NBC)

 PURPOSE
   M0 round 5 found that `dotnet restore` fails on this machine. The error was:

     error NU1301: The SSL connection could not be established
     error NU1301: Authentication failed / no credentials available in the
                   security package

   This script pinpoints WHERE the connection actually breaks, in layers:
     DNS  ->  TCP  ->  TLS handshake  ->  HTTP  ->  proxy  ->  NuGet endpoint

   That distinction matters, because the fix is different:
     - DNS fails            -> network/hosts problem
     - TCP fails            -> firewall or no route
     - TLS fails, HTTP ok   -> TLS interception / certificate trust
     - everything direct fails but proxy works -> system proxy is off
     - proxy port not listening                -> start the proxy client

 RUN THIS SCRIPT OUTSIDE THE SANDBOX TOO
   The AI harness runs commands under a network-restricted sandbox, where ALL
   direct HTTPS from child processes fails. Running this same script yourself in
   a normal PowerShell window tells us whether the restriction only affects the
   sandbox or also affects your normal shell (which is what matters for M1).

 THIS SCRIPT IS READ-ONLY
   It makes outbound HEAD requests and reads configuration. It changes nothing.

 USAGE
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Test-Network.ps1

   # Probe a specific proxy port
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Test-Network.ps1 -ProxyPort 7890
================================================================================
#>

[CmdletBinding()]
param(
    [int]$ProxyPort = 7890
)

$ErrorActionPreference = 'Continue'

function Write-Header($t) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkCyan
}
function Ok($t)   { Write-Host "  [OK]   $t" -ForegroundColor Green }
function Bad($t)  { Write-Host "  [FAIL] $t" -ForegroundColor Red }
function Warn2($t){ Write-Host "  [WARN] $t" -ForegroundColor Yellow }
function Info($t) { Write-Host "  [info] $t" -ForegroundColor DarkGray }

# Enable modern TLS for this process (Windows PowerShell 5.1 may default low)
try {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
} catch { }

Write-Header 'NETWORK DIAGNOSTIC  -  HTTPS / proxy / NuGet'

# ---------------------------------------------------------------------------
# 1. Environment
# ---------------------------------------------------------------------------
Write-Header '1. environment'

Info ("PowerShell version : {0}" -f $PSVersionTable.PSVersion.ToString())
Info ("OS                 : {0}" -f ([System.Environment]::OSVersion.VersionString))
Info ("Machine            : {0}" -f $env:COMPUTERNAME)
Info ("Local time (UTC)   : {0}" -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))

$skew = $null
try {
    $r = Invoke-WebRequest -Uri 'https://www.baidu.com' -Method Head -TimeoutSec 8 -UseBasicParsing
    if ($r.Headers['Date']) {
        $srv = [datetime]::Parse($r.Headers['Date']).ToUniversalTime()
        $skew = [math]::Abs(((Get-Date).ToUniversalTime() - $srv).TotalSeconds)
    }
} catch { }

if ($skew -ne $null) {
    Info ("clock skew vs server: {0:N1} s" -f $skew)
    if ($skew -gt 300) { Bad 'clock skew > 5 min - this alone breaks TLS. Sync your system clock.' }
    else { Ok 'system clock is within tolerance' }
} else {
    Warn2 'could not read server time header (no HTTPS worked) - cannot check clock skew'
}

# ---------------------------------------------------------------------------
# 2. Proxy configuration
# ---------------------------------------------------------------------------
Write-Header '2. proxy configuration'

$proxyVars = @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','http_proxy','https_proxy','NO_PROXY','no_proxy')
$anyProxyVar = $false
foreach ($n in $proxyVars) {
    $v = [Environment]::GetEnvironmentVariable($n)
    if ($v) { Info ("{0} = {1}" -f $n, $v); $anyProxyVar = $true }
}
if (-not $anyProxyVar) { Info 'no proxy environment variables set' }

try {
    $ie = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction Stop
    Info ("WinINET ProxyEnable = {0}" -f $ie.ProxyEnable)
    Info ("WinINET ProxyServer = {0}" -f $ie.ProxyServer)
    if ($ie.ProxyEnable -eq 1) { Ok 'Windows system proxy is ENABLED' }
    else { Warn2 'Windows system proxy is DISABLED (ProxyEnable=0)' }
} catch { Warn2 "cannot read WinINET proxy settings: $($_.Exception.Message)" }

Write-Host ''
Info 'WinHTTP proxy:'
$wh = netsh winhttp show proxy 2>&1
foreach ($line in $wh) {
    if ($line -match '\S') { Write-Host ("    {0}" -f $line.Trim()) -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------------------
# 3. Is a local proxy actually listening?
# ---------------------------------------------------------------------------
Write-Header '3. local proxy listeners'

$portsToCheck = @($ProxyPort, 7890, 7891, 7897, 1080, 10808, 10809, 8080, 8888) | Sort-Object -Unique
$listening = @()
foreach ($p in $portsToCheck) {
    $c = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    if ($c) {
        $procName = 'unknown'
        try { $procName = (Get-Process -Id $c[0].OwningProcess -ErrorAction Stop).ProcessName } catch { }
        Ok ("port {0} LISTENING  (pid {1}  {2})" -f $p, $c[0].OwningProcess, $procName)
        $listening += $p
    }
}
if ($listening.Count -eq 0) {
    Warn2 'no local proxy port is listening.'
    Info 'If you use Clash / v2ray / similar, it is running but its proxy is OFF.'
    Info 'Turn on the system proxy in that tool, then re-run this script.'
}

$proxyProcs = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match 'clash|v2ray|sing|verge|mihomo|nekoray|trojan|shadowsocks' }
if ($proxyProcs) {
    Write-Host ''
    Info 'proxy-related processes running:'
    foreach ($p in $proxyProcs) { Write-Host ("    {0} (pid {1})" -f $p.ProcessName, $p.Id) -ForegroundColor DarkGray }
} else {
    Info 'no proxy client process detected'
}

# ---------------------------------------------------------------------------
# 4. Layered connectivity test
# ---------------------------------------------------------------------------
Write-Header '4. layered connectivity test'

$targets = @(
    @{ url = 'https://www.baidu.com';                    label = 'generic HTTPS (baidu)' }
    @{ url = 'https://api.nuget.org/v3/index.json';      label = 'NuGet official feed' }
    @{ url = 'https://nuget.azure.cn/v3/index.json';     label = 'NuGet CN mirror' }
)

function Test-Target([string]$url, [string]$label, [string]$proxy) {
    $host_ = ([uri]$url).Host
    Write-Host ''
    Write-Host ("  --- {0}" -f $label) -ForegroundColor White
    Write-Host ("      {0}" -f $url) -ForegroundColor DarkGray

    # DNS
    try {
        $ip = [System.Net.Dns]::GetHostAddresses($host_) | Select-Object -First 1
        Ok ("DNS  resolved -> {0}" -f $ip)
    } catch {
        Bad ("DNS  failed: {0}" -f $_.Exception.Message)
        return
    }

    # TCP
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $iar = $tcp.BeginConnect($host_, 443, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(6000)
        if ($ok -and $tcp.Connected) { Ok 'TCP  443 reachable' }
        else { Bad 'TCP  443 not reachable (firewall or no route)' }
        $tcp.Close()
    } catch {
        Bad ("TCP  failed: {0}" -f $_.Exception.Message)
    }

    # HTTP(S)
    $args = @{ Uri = $url; Method = 'Head'; TimeoutSec = 12; UseBasicParsing = $true }
    if ($proxy) { $args['Proxy'] = $proxy }
    try {
        $resp = Invoke-WebRequest @args
        $via = if ($proxy) { "via $proxy" } else { 'direct' }
        Ok ("HTTP {0}  ({1})" -f $resp.StatusCode, $via)
    } catch {
        $via = if ($proxy) { "via $proxy" } else { 'direct' }
        Bad ("HTTP failed ({0}): {1}" -f $via, $_.Exception.Message)
        if ($_.Exception.InnerException) {
            Info ("inner: {0}" -f $_.Exception.InnerException.Message)
        }
    }
}

foreach ($t in $targets) { Test-Target $t.url $t.label $null }

if ($listening.Count -gt 0) {
    Write-Header '5. retry through the detected local proxy'
    $px = "http://127.0.0.1:$($listening[0])"
    Info "using proxy $px"
    foreach ($t in $targets) { Test-Target $t.url $t.label $px }
} else {
    Write-Header '5. proxy retry skipped'
    Info 'No listening local proxy port, so there is nothing to route through.'
    Info "If you start your proxy client, re-run with: -ProxyPort <port>"
}

# ---------------------------------------------------------------------------
# 6. Verdict and what to do
# ---------------------------------------------------------------------------
Write-Header '6. verdict'

Write-Host '  Read the section 4 results top-down. The FIRST failing layer tells you the cause:' -ForegroundColor White
Write-Host ''
Write-Host '    DNS fails                 -> hosts file / DNS server problem' -ForegroundColor Gray
Write-Host '    TCP fails                 -> firewall or no route to the internet' -ForegroundColor Gray
Write-Host '    TCP ok but HTTP fails     -> TLS interception, certificate trust, or a' -ForegroundColor Gray
Write-Host '                                 network sandbox blocking outbound TLS' -ForegroundColor Gray
Write-Host '    direct fails, proxy works -> enable the system proxy in your proxy client' -ForegroundColor Gray
Write-Host '    nothing listening on proxy-> your proxy client is running but its proxy is OFF' -ForegroundColor Gray
Write-Host ''
Write-Host '  WHY THIS BLOCKS M1 (not M0):' -ForegroundColor White
Write-Host '    The M0 server skeleton deliberately references NO NuGet packages, so' -ForegroundColor Gray
Write-Host '    `dotnet build` succeeds. From M1 onward we need Dapper, MySqlConnector,' -ForegroundColor Gray
Write-Host '    protobuf-net, xUnit and NPOI - all of which require a working restore.' -ForegroundColor Gray
Write-Host ''
Write-Host '  FIX OPTIONS (cheapest first):' -ForegroundColor White
Write-Host '    1. Turn ON the system proxy in Clash Verge (config says mixed-port 7890),' -ForegroundColor Gray
Write-Host '       then re-run this script. If HTTPS then works, `dotnet restore` should too.' -ForegroundColor Gray
Write-Host '    2. Retry restore through the proxy explicitly:' -ForegroundColor Gray
Write-Host '         dotnet restore --source https://api.nuget.org/v3/index.json \' -ForegroundColor DarkGray
Write-Host '           -p:HttpProxy=http://127.0.0.1:7890 -p:HttpsProxy=http://127.0.0.1:7890' -ForegroundColor DarkGray
Write-Host '       or set env vars HTTPS_PROXY / HTTP_PROXY for the session.' -ForegroundColor Gray
Write-Host '    3. Use Visual Studio NuGet Package Manager (GUI). VS may use its own' -ForegroundColor Gray
Write-Host '       TLS stack / the Windows proxy, so it can succeed where the CLI fails.' -ForegroundColor Gray
Write-Host '    4. Download .nupkg files in a browser (browser has its own proxy settings)' -ForegroundColor Gray
Write-Host '       and extract lib/netstandard2.1/*.dll manually. Unity needs manual DLL' -ForegroundColor Gray
Write-Host '       placement anyway, so this hurts the client little.' -ForegroundColor Gray
Write-Host ''
Write-Host '  WHAT THIS SCRIPT CANNOT TELL YOU BY ITSELF:' -ForegroundColor White
Write-Host '    If you run it inside the AI sandbox, ALL direct HTTPS fails by design,' -ForegroundColor Gray
Write-Host '    so you cannot distinguish "sandbox restriction" from "your machine has' -ForegroundColor Gray
Write-Host '    a real network problem". Run it in YOUR OWN PowerShell window - that is' -ForegroundColor Gray
Write-Host '    the environment that matters for M1.' -ForegroundColor Gray
Write-Host ''
Write-Host '  Read-only diagnostic complete. Nothing was modified.' -ForegroundColor DarkGray
Write-Host ''
