param(
    [string]$Subdomain = "",
    [string]$AuthToken = "",
    [int]$LocalPort = 80,
    [string]$SiteName = "AttendanceMonitoring-DevBox",
    [string]$AppPoolName = "AttendanceMonitoring-DevBox",
    [string]$PhysicalPath = "C:\inetpub\AttendanceMonitoring",
    [switch]$EnableHttps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Set-WebConfigEnvironmentVariable {
    param(
        [Parameter(Mandatory = $true)][string]$WebConfigPath,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value,
        [string]$AppDllName = "AttendanceMonitoring.dll"
    )

    [xml]$xml = Get-Content -LiteralPath $WebConfigPath

    $configurationNode = $xml.SelectSingleNode("/*[local-name()='configuration']")
    if ($null -eq $configurationNode) {
        throw "Invalid web.config: missing /configuration root."
    }

    $systemWebServerNode = $xml.SelectSingleNode("//*[local-name()='system.webServer']")
    if ($null -eq $systemWebServerNode) {
        $systemWebServerNode = $xml.CreateElement("system.webServer")
        [void]$configurationNode.AppendChild($systemWebServerNode)
    }

    $aspNetCoreNode = $xml.SelectSingleNode("//*[local-name()='aspNetCore']")
    if ($null -eq $aspNetCoreNode) {
        $aspNetCoreNode = $xml.CreateElement("aspNetCore")
        [void]$aspNetCoreNode.SetAttribute("processPath", "dotnet")
        [void]$aspNetCoreNode.SetAttribute("arguments", ".\\$AppDllName")
        [void]$aspNetCoreNode.SetAttribute("stdoutLogEnabled", "false")
        [void]$aspNetCoreNode.SetAttribute("stdoutLogFile", ".\\logs\\stdout")
        [void]$aspNetCoreNode.SetAttribute("hostingModel", "inprocess")
        [void]$systemWebServerNode.AppendChild($aspNetCoreNode)
    }

    $envVarsNode = $xml.SelectSingleNode("//*[local-name()='aspNetCore']/*[local-name()='environmentVariables']")
    if ($null -eq $envVarsNode) {
        $envVarsNode = $xml.CreateElement("environmentVariables")
        [void]$aspNetCoreNode.AppendChild($envVarsNode)
    }

    $match = $xml.SelectSingleNode("//*[local-name()='environmentVariables']/*[local-name()='environmentVariable'][@name='$Name']")
    if ($null -eq $match) {
        $newVar = $xml.CreateElement("environmentVariable")
        [void]$newVar.SetAttribute("name", $Name)
        [void]$newVar.SetAttribute("value", $Value)
        [void]$envVarsNode.AppendChild($newVar)
    }
    else {
        [void]$match.SetAttribute("value", $Value)
    }

    $xml.Save($WebConfigPath)
}

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell window (Run as Administrator)."
    }
}

function Install-Cpolar {
    Write-Host "cpolar not found. Attempting to install..." -ForegroundColor Yellow

    $installed = $false

    # Try winget first
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install cpolar --source winget --accept-package-agreements --accept-source-agreements 2>$null
        if ($LASTEXITCODE -eq 0) {
            $installed = $true
        }
    }

    # Fallback: download zip installer
    if (-not $installed) {
        Write-Host "Downloading cpolar installer..." -ForegroundColor Yellow
        $zipPath = "$env:TEMP\cpolar-stable-windows-amd64-setup.zip"
        Invoke-WebRequest "https://www.cpolar.com/static/downloads/releases/3.3.12/cpolar-stable-windows-amd64-setup.zip" `
            -OutFile $zipPath -UseBasicParsing

        $extractPath = "$env:TEMP\cpolar-setup"
        Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

        # zip contains cpolar_amd64.msi
        $msi = Get-ChildItem $extractPath -Filter "*.msi" -Recurse | Select-Object -First 1
        if ($null -ne $msi) {
            Write-Host "Installing MSI: $($msi.FullName)" -ForegroundColor Yellow
            Start-Process msiexec.exe -ArgumentList "/i `"$($msi.FullName)`" /qn" -Wait
        }
        else {
            $exe = Get-ChildItem $extractPath -Filter "*.exe" -Recurse | Select-Object -First 1
            if ($null -ne $exe) {
                Start-Process $exe.FullName -ArgumentList "/S" -Wait
            }
            else {
                throw "Could not find installer (msi/exe) in downloaded zip at $extractPath"
            }
        }
    }

    # Refresh PATH
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path", "User")

    if (-not (Get-Command cpolar -ErrorAction SilentlyContinue)) {
        throw "cpolar installation failed or PATH not updated. Please install manually from https://www.cpolar.com and rerun this script."
    }

    Write-Host "cpolar installed successfully." -ForegroundColor Green
}

function Ensure-AppRunning {
    param([string]$Port)

    Write-Step "Verifying IIS app is running on port $Port"

    $appcmd = "$env:windir\System32\inetsrv\appcmd.exe"

    $poolState = & $appcmd list apppool "$AppPoolName" /text:state 2>$null
    if ($poolState -ne "Started") {
        Write-Host "Starting app pool $AppPoolName..." -ForegroundColor Yellow
        & $appcmd start apppool /apppool.name:"$AppPoolName" | Out-Null
        Start-Sleep -Seconds 2
    }

    $siteState = & $appcmd list site "$SiteName" /text:state 2>$null
    if ($siteState -ne "Started") {
        Write-Host "Starting site $SiteName..." -ForegroundColor Yellow
        & $appcmd start site /site.name:"$SiteName" | Out-Null
        Start-Sleep -Seconds 2
    }

    try {
        $resp = Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing -TimeoutSec 10
        Write-Host ("Local app is running. Status: {0}" -f $resp.StatusCode) -ForegroundColor Green
    }
    catch {
        throw "Local app is not responding on http://localhost:$Port/. Check IIS and app settings. Error: $($_.Exception.Message)"
    }
}

function Set-TunnelCompatibility {
    Write-Step "Configuring app for tunnel compatibility"

    $webConfigPath = Join-Path $PhysicalPath "web.config"
    if (-not (Test-Path $webConfigPath)) {
        Write-Host "web.config not found. Skipping tunnel compatibility update." -ForegroundColor Yellow
        return
    }

    Set-WebConfigEnvironmentVariable -WebConfigPath $webConfigPath -Name "AttendanceMonitoring__RequireSecureCookie" -Value "false"
    Write-Host "Set AttendanceMonitoring__RequireSecureCookie = false in web.config." -ForegroundColor Green

    # The public tunnel can still be HTTPS while IIS sees localhost HTTP.
    # Force SecureCookie off here so antiforgery/login pages do not crash.
    $appcmd = "$env:windir\System32\inetsrv\appcmd.exe"
    & $appcmd recycle apppool /apppool.name:"$AppPoolName" | Out-Null
    Start-Sleep -Seconds 3
    Write-Host "App pool recycled." -ForegroundColor Green
}

function Ensure-CpolarAuth {
    param([string]$Token)

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        Write-Step "Setting cpolar authtoken"
        & cpolar authtoken $Token | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set cpolar authtoken. Verify token from https://dashboard.cpolar.com/getAuthToken"
        }
        return
    }

    $configPath = Join-Path $env:USERPROFILE ".cpolar\cpolar.yml"
    if (-not (Test-Path $configPath)) {
        throw "cpolar auth token is missing. Run: cpolar authtoken <YOUR_TOKEN> or rerun this script with -AuthToken."
    }

    $content = Get-Content $configPath -Raw
    if ($content -notmatch "(?im)^\s*authtoken\s*:\s*.+$") {
        throw "cpolar auth token is missing in $configPath. Run: cpolar authtoken <YOUR_TOKEN> or rerun this script with -AuthToken."
    }
}

Assert-Admin

# Install cpolar if missing
if (-not (Get-Command cpolar -ErrorAction SilentlyContinue)) {
    Install-Cpolar
}

Write-Step "cpolar version"
cpolar version

Ensure-CpolarAuth -Token $AuthToken

# Ensure app is running locally
Ensure-AppRunning -Port $LocalPort

# Configure for tunnel compatibility
Set-TunnelCompatibility

# Build tunnel command
Write-Step "Starting cpolar tunnel"

$tunnelArgs = @("http", $LocalPort.ToString())
if (-not [string]::IsNullOrWhiteSpace($Subdomain)) {
    $tunnelArgs += "--subdomain"
    $tunnelArgs += $Subdomain
}

Write-Host ""
Write-Host "Tunnel is starting. Look for lines like:" -ForegroundColor Cyan
Write-Host "  Forwarding  https://xxxx.cpolar.io -> http://localhost:$LocalPort" -ForegroundColor Green
Write-Host "  Forwarding  http://xxxx.cpolar.io  -> http://localhost:$LocalPort" -ForegroundColor Green
Write-Host ""
Write-Host "Press Ctrl+C to stop the tunnel." -ForegroundColor Yellow
Write-Host ""

& cpolar @tunnelArgs
