#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SiteName = "AttendanceMonitoring-DevBox",
    [string]$AppPoolName = "AttendanceMonitoring-DevBox",
    [string]$PhysicalPath = "C:\inetpub\AttendanceMonitoring",
    [int]$Port = 8080,
    [string]$EnvironmentName = "Production",
    [string]$SourceDbPath,
    [switch]$SkipBuild,
    [switch]$SkipDataMigration,
    [switch]$RequireSecureCookie,
    [switch]$SkipIisDeploy,
    [switch]$StartTunnel,
    [switch]$UseNamedTunnel,
    [string]$TunnelName = "attendance-monitoring",
    [string]$Hostname = "csi-attendance.cloudns.net",
    [string]$CloudflaredPath = ""
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Resolve-Cloudflared {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "Cloudflared not found at: $ExplicitPath"
        }
        return $ExplicitPath
    }

    $cmd = Get-Command cloudflared -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return $cmd.Source
    }

    $fallback = "C:\Tools\cloudflared\cloudflared.exe"
    if (Test-Path -LiteralPath $fallback) {
        return $fallback
    }

    throw "cloudflared is not installed. Install it first or pass -CloudflaredPath."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$deployIisScript = Join-Path $PSScriptRoot "deploy_iis_devbox.ps1"
$namedTunnelScript = Join-Path $PSScriptRoot "start_cloudflare_tunnel.ps1"

if (-not (Test-Path -LiteralPath $deployIisScript)) {
    throw "Missing script: $deployIisScript"
}

if (-not $SkipIisDeploy) {
    Write-Step "Deploying app to IIS"

    $deployArgs = @{
        SiteName = $SiteName
        AppPoolName = $AppPoolName
        PhysicalPath = $PhysicalPath
        Port = $Port
        EnvironmentName = $EnvironmentName
    }

    if (-not [string]::IsNullOrWhiteSpace($SourceDbPath)) {
        $deployArgs.SourceDbPath = $SourceDbPath
    }
    if ($SkipBuild.IsPresent) {
        $deployArgs.SkipBuild = $true
    }
    if ($SkipDataMigration.IsPresent) {
        $deployArgs.SkipDataMigration = $true
    }
    if ($RequireSecureCookie.IsPresent) {
        $deployArgs.RequireSecureCookie = $true
    }

    if ($PSCmdlet.ShouldProcess($deployIisScript, "Run IIS deployment")) {
        & $deployIisScript @deployArgs
        # robocopy exit codes 0-7 are all success variants; 8+ are real errors.
        if ($LASTEXITCODE -gt 7) {
            throw "IIS deployment script failed with exit code $LASTEXITCODE"
        }
    }
}
else {
    Write-Step "Skipping IIS deployment"
}

Write-Step "Local endpoint"
Write-Host "App should be reachable at: http://localhost:$Port/" -ForegroundColor Green

if (-not $StartTunnel) {
    Write-Host ""
    Write-Host "Tunnel not started (pass -StartTunnel to expose publicly)." -ForegroundColor Yellow
    Write-Host "Quick public URL command:" -ForegroundColor Yellow
    Write-Host "  cloudflared tunnel --url http://localhost:$Port --no-autoupdate" -ForegroundColor Gray
    exit 0
}

if ($UseNamedTunnel) {
    Write-Step "Starting named Cloudflare tunnel"

    if (-not (Test-Path -LiteralPath $namedTunnelScript)) {
        throw "Missing script: $namedTunnelScript"
    }

    $tunnelArgs = @{
        LocalPort = $Port
        TunnelName = $TunnelName
    }
    if (-not [string]::IsNullOrWhiteSpace($Hostname)) {
        $tunnelArgs.Hostname = $Hostname
    }

    if ($PSCmdlet.ShouldProcess($namedTunnelScript, "Start named tunnel")) {
        & $namedTunnelScript @tunnelArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Named tunnel script failed with exit code $LASTEXITCODE"
        }
    }
}
else {
    Write-Step "Starting quick Cloudflare tunnel"
    $cloudflared = Resolve-Cloudflared -ExplicitPath $CloudflaredPath

    Write-Host "Starting: $cloudflared tunnel --url http://localhost:$Port --no-autoupdate" -ForegroundColor Yellow
    Write-Host "Press Ctrl+C to stop tunnel." -ForegroundColor Yellow

    if ($PSCmdlet.ShouldProcess("cloudflared", "Start quick tunnel")) {
        & $cloudflared tunnel --url "http://localhost:$Port" --no-autoupdate
        if ($LASTEXITCODE -ne 0) {
            throw "cloudflared quick tunnel failed with exit code $LASTEXITCODE"
        }
    }
}
