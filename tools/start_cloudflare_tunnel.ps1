#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Starts Cloudflare Tunnel for external access to Attendance Monitoring app
    
.DESCRIPTION
    - Installs cloudflared if needed
    - Authenticates with Cloudflare (browser popup)
    - Creates tunnel named "attendance-monitoring"
    - Routes to local IIS app
    - Runs tunnel continuously
    
.PARAMETER LocalPort
    Local port the IIS app is running on (default: 80)
    
.PARAMETER TunnelName
    Name of the Cloudflare tunnel (default: attendance-monitoring)
    
.PARAMETER Hostname
    Custom hostname for tunnel (optional, like "attendance.yourdomain.com")
    
.EXAMPLE
    .\start_cloudflare_tunnel.ps1
    
.EXAMPLE
    .\start_cloudflare_tunnel.ps1 -LocalPort 443 -TunnelName my-attendance
#>

param(
    [int]$LocalPort = 80,
    [string]$TunnelName = "attendance-monitoring",
    [string]$Hostname = ""
)

# Colors for output
function Write-Header { Write-Host "`n=== $args ===`n" -ForegroundColor Cyan }
function Write-Success { Write-Host "[OK] $args" -ForegroundColor Green }
function Write-Error_ { Write-Host "[FAIL] $args" -ForegroundColor Red }
function Write-Info { Write-Host "[-->] $args" -ForegroundColor Yellow }

Write-Host "Cloudflare Tunnel Setup for Attendance Monitoring`n" -ForegroundColor Cyan

# Step 1: Check/Install cloudflared
Write-Header "Step 1: Checking Cloudflared Installation"

$installDir = "C:\Tools\cloudflared"
$cloudflaredPath = "$installDir\cloudflared.exe"

if (Test-Path $cloudflaredPath) {
    Write-Success "Cloudflared already installed"
    & $cloudflaredPath --version
} else {
    Write-Info "Installing Cloudflared..."
    
    try {
        # Create directory
        if (-not (Test-Path $installDir)) {
            New-Item -ItemType Directory -Path $installDir -Force | Out-Null
        }
        
        # Download executable
        Write-Info "Downloading cloudflared EXE..."
        $exeUrl = "https://github.com/cloudflare/cloudflared/releases/download/2024.6.1/cloudflared-windows-amd64.exe"
        
        Invoke-WebRequest -Uri $exeUrl -OutFile $cloudflaredPath -UseBasicParsing -ErrorAction Stop
        
        Write-Success "Downloaded to $cloudflaredPath"
        
        # Verify it works
        & $cloudflaredPath --version
        Write-Success "Cloudflared installed successfully"
        
        # Add to PATH if not already
        $currentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
        if ($currentPath -notlike "*$installDir*") {
            Write-Info "Adding $installDir to PATH..."
            [Environment]::SetEnvironmentVariable("PATH", "$currentPath;$installDir", "User")
            $env:PATH += ";$installDir"
        }
    } catch {
        Write-Error_ "Installation failed: $_"
        Write-Info "Try downloading manually: https://github.com/cloudflare/cloudflared/releases/"
        exit 1
    }
}

# Step 2: Authenticate with Cloudflare
Write-Header "Step 2: Cloudflare Authentication"

$configPath = "$env:USERPROFILE\.cloudflared\cert.pem"
$credPath = "$env:USERPROFILE\.cloudflared"

if (Test-Path $configPath) {
    Write-Success "Already authenticated"
} else {
    Write-Info "Opening browser for Cloudflare login..."
    Write-Info "You will be asked to select a domain and authorize"
    
    Start-Sleep -Seconds 2
    & $cloudflaredPath login
    
    if (-not (Test-Path $configPath)) {
        Write-Error_ "Authentication failed or cancelled"
        exit 1
    }
    
    Write-Success "Authentication successful"
}

# Step 3: Create tunnel
Write-Header "Step 3: Creating Tunnel"

$tunnelPath = "$env:USERPROFILE\.cloudflared\$TunnelName.json"

if (Test-Path $tunnelPath) {
    Write-Success "Tunnel '$TunnelName' already exists"
    & $cloudflaredPath tunnel list | findstr $TunnelName
} else {
    Write-Info "Creating new tunnel: $TunnelName"
    & $cloudflaredPath tunnel create $TunnelName
    
    if (-not (Test-Path $tunnelPath)) {
        Write-Error_ "Tunnel creation failed"
        exit 1
    }
    
    Write-Success "Tunnel created"
}

# Step 4: Configure routing
Write-Header "Step 4: Configuring Route"

$configFile = "$env:USERPROFILE\.cloudflared\config.yml"
$localUrl = "http://localhost:$LocalPort"

if ($Hostname) {
    Write-Info "Configuring tunnel to route $Hostname -> $localUrl"
    $routeConfig = @"
tunnel: $TunnelName
credentials-file: $tunnelPath

ingress:
  - hostname: $Hostname
    service: $localUrl
  - service: http_status:404
"@
} else {
    Write-Info "Creating catchall tunnel configuration"
    $routeConfig = @"
tunnel: $TunnelName
credentials-file: $tunnelPath

ingress:
  - service: $localUrl
"@
}

$routeConfig | Out-File -FilePath $configFile -Encoding UTF8
Write-Success "Route configured in $configFile"

# Step 5: Route DNS (if hostname provided)
if ($Hostname) {
    Write-Header "Step 5: DNS Routing"
    
    Write-Info "Routing $Hostname to tunnel..."
    & $cloudflaredPath tunnel route dns $TunnelName $Hostname
    Write-Success "DNS routed - $Hostname should be live shortly"
}

# Step 6: Start tunnel
Write-Header "Step 6: Starting Tunnel"

Write-Info "Starting Cloudflare tunnel..."
Write-Success "Tunnel is now active!"
Write-Info "Any network requests will use Cloudflare's global network"

if ($Hostname) {
    Write-Info "`nAccess your app at: https://$Hostname"
} else {
    Write-Info "`nAfter DNS setup, you can access the app via your custom domain"
}

Write-Info "Tunnel logs:"
Write-Info "Press Ctrl+C to stop"
Write-Host ""

# Run tunnel
& $cloudflaredPath tunnel run $TunnelName
