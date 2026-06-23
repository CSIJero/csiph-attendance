[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SiteName = "AttendanceMonitoring",
    [string]$AppPoolName = "AttendanceMonitoring",
    [string]$PhysicalPath = "C:\inetpub\AttendanceMonitoring",
    [string]$PublishConfiguration = "Release",
    [int]$Port = 80,
    [string]$HostHeader = "",
    [string]$EnvironmentName = "Production",
    [string]$SourceDbPath,
    [string]$TargetDbName = "attendance.db",
    [switch]$InstallIisManagement,
    [switch]$SkipBuild,
    [switch]$SkipDataCopy,
    [switch]$RequireSecureCookie,
    [switch]$OpenFirewall = $true,
    [switch]$NoStartSite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell window (Run as Administrator)."
    }
}

function Assert-IisAvailable {
    if (-not (Test-Path "$env:windir\System32\inetsrv\appcmd.exe")) {
        throw "IIS appcmd.exe is missing. Install IIS Web Server role first."
    }
}

function Ensure-IisManagementFeatures {
    $hasModule = [bool](Get-Module -ListAvailable -Name WebAdministration)
    if ($hasModule) {
        return
    }

    Write-Step "Installing IIS management prerequisites"

    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        Install-WindowsFeature -Name Web-Server,Web-Mgmt-Console,Web-Scripting-Tools -IncludeManagementTools | Out-Null
    }
    elseif (Get-Command Enable-WindowsOptionalFeature -ErrorAction SilentlyContinue) {
        $features = @(
            "IIS-WebServerRole",
            "IIS-ManagementConsole",
            "IIS-ManagementScriptingTools"
        )

        foreach ($feature in $features) {
            Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart | Out-Null
        }
    }
    else {
        throw "Could not find Install-WindowsFeature or Enable-WindowsOptionalFeature on this machine."
    }

    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        Write-Host "WebAdministration module is still unavailable in this session. Continuing with appcmd fallback." -ForegroundColor Yellow
    }
}

function Ensure-FirewallRule {
    param(
        [int]$Port,
        [string]$RuleName
    )

    $existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Firewall rule already exists: $RuleName" -ForegroundColor Yellow
        return
    }

    New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Any | Out-Null
    Write-Host "Created firewall rule: $RuleName" -ForegroundColor Green
}

Assert-Admin
if ($InstallIisManagement) {
    Ensure-IisManagementFeatures
}
Assert-IisAvailable

$useWebAdministration = [bool](Get-Module -ListAvailable -Name WebAdministration)
if ($useWebAdministration) {
    Import-Module WebAdministration
}
else {
    Write-Host "Using appcmd fallback for IIS configuration in this run." -ForegroundColor Yellow
}

$appcmd = "$env:windir\System32\inetsrv\appcmd.exe"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "AttendanceMonitoring.csproj"
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Cannot find project file at $projectPath"
}

$publishPath = Join-Path $repoRoot "artifacts\publish\reachable"

if (-not $SkipBuild) {
    Write-Step "Publishing ASP.NET app"
    if ($PSCmdlet.ShouldProcess($projectPath, "dotnet publish to $publishPath")) {
        dotnet publish $projectPath -c $PublishConfiguration -o $publishPath
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
    }
}

Write-Step "Preparing deployment folder"
if ($PSCmdlet.ShouldProcess($PhysicalPath, "Create target folder and copy published files")) {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null

    if (-not $SkipBuild) {
        robocopy $publishPath $PhysicalPath /MIR /NFL /NDL /NJH /NJS /NC /NS | Out-Null
        if ($LASTEXITCODE -gt 7) {
            throw "robocopy failed with exit code $LASTEXITCODE"
        }
    }
}

if (-not $SkipDataCopy) {
    Write-Step "Copying SQLite database"

    $effectiveSourceDb = $SourceDbPath
    if ([string]::IsNullOrWhiteSpace($effectiveSourceDb)) {
        $candidate1 = Join-Path $repoRoot "attendance.db"
        $candidate2 = Join-Path $repoRoot "backups\attendance_from_extracted_iis.db"

        if (Test-Path -LiteralPath $candidate1) {
            $effectiveSourceDb = $candidate1
        }
        elseif (Test-Path -LiteralPath $candidate2) {
            $effectiveSourceDb = $candidate2
        }
    }

    if ([string]::IsNullOrWhiteSpace($effectiveSourceDb)) {
        Write-Host "No source DB file found automatically. Pass -SourceDbPath to copy a DB." -ForegroundColor Yellow
    }
    else {
        if (-not (Test-Path -LiteralPath $effectiveSourceDb)) {
            throw "Source DB file not found: $effectiveSourceDb"
        }

        $targetDbPath = Join-Path $PhysicalPath $TargetDbName
        if ($PSCmdlet.ShouldProcess($targetDbPath, "Copy SQLite DB")) {
            if (Test-Path -LiteralPath $targetDbPath) {
                $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
                Copy-Item -LiteralPath $targetDbPath -Destination "$targetDbPath.bak.$stamp" -Force
            }
            Copy-Item -LiteralPath $effectiveSourceDb -Destination $targetDbPath -Force
            Write-Host "Copied DB: $effectiveSourceDb -> $targetDbPath" -ForegroundColor Green
        }
    }
}

Write-Step "Updating app settings"
$appsettingsPath = Join-Path $PhysicalPath "appsettings.Production.json"
if (-not (Test-Path -LiteralPath $appsettingsPath)) {
    throw "Missing appsettings.Production.json at $appsettingsPath"
}

$json = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
if (-not $json.PSObject.Properties['ConnectionStrings']) {
    $json | Add-Member -NotePropertyName "ConnectionStrings" -NotePropertyValue @{}
}

$absoluteDb = Join-Path $PhysicalPath $TargetDbName
$json.ConnectionStrings.DefaultConnection = "Data Source=$absoluteDb"

if (-not $json.PSObject.Properties['AttendanceMonitoring']) {
    $json | Add-Member -NotePropertyName "AttendanceMonitoring" -NotePropertyValue @{}
}

$json.AttendanceMonitoring.RequireSecureCookie = [bool]$RequireSecureCookie
$json | ConvertTo-Json -Depth 20 | Set-Content $appsettingsPath -Encoding UTF8

Write-Host "Set DefaultConnection to: Data Source=$absoluteDb" -ForegroundColor Green
Write-Host ("Set AttendanceMonitoring.RequireSecureCookie to: {0}" -f ([bool]$RequireSecureCookie)) -ForegroundColor Green

Write-Step "Configuring IIS app pool"
if ($PSCmdlet.ShouldProcess($AppPoolName, "Create/update app pool")) {
    if ($useWebAdministration) {
        if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
            New-WebAppPool -Name $AppPoolName | Out-Null
        }

        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value "Integrated"
        Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value "ApplicationPoolIdentity"
    }
    else {
        & $appcmd list apppool "$AppPoolName" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            & $appcmd add apppool /name:"$AppPoolName" | Out-Null
        }

        & $appcmd set apppool "$AppPoolName" /managedRuntimeVersion:"" /managedPipelineMode:Integrated /processModel.identityType:ApplicationPoolIdentity | Out-Null
    }
}

Write-Step "Configuring IIS site"
if ($PSCmdlet.ShouldProcess($SiteName, "Create/update IIS site and binding")) {
    if ($useWebAdministration) {
        if (-not (Test-Path "IIS:\Sites\$SiteName")) {
            New-Website -Name $SiteName -PhysicalPath $PhysicalPath -Port $Port -HostHeader $HostHeader -ApplicationPool $AppPoolName | Out-Null
        }
        else {
            Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
            Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath

            Get-WebBinding -Name $SiteName | Remove-WebBinding
            New-WebBinding -Name $SiteName -Protocol "http" -Port $Port -HostHeader $HostHeader | Out-Null
        }
    }
    else {
        $bindingInfo = if ([string]::IsNullOrWhiteSpace($HostHeader)) {
            ("*:{0}:" -f $Port)
        }
        else {
            ("*:{0}:{1}" -f $Port, $HostHeader)
        }

        & $appcmd list site "$SiteName" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            & $appcmd add site /name:"$SiteName" /physicalPath:"$PhysicalPath" /bindings:"http/$bindingInfo" | Out-Null
        }
        else {
            & $appcmd set app "$SiteName/" /applicationPool:"$AppPoolName" | Out-Null
            & $appcmd set vdir /vdir.name:"$SiteName/" /physicalPath:"$PhysicalPath" | Out-Null

            $bindings = & $appcmd list site "$SiteName" /text:bindings 2>$null
            if (-not ($bindings | Select-String -SimpleMatch $bindingInfo)) {
                & $appcmd set site /site.name:"$SiteName" "/+bindings.[protocol='http',bindingInformation='$bindingInfo']" | Out-Null
            }
        }
    }
}

if ($OpenFirewall) {
    Write-Step "Ensuring inbound firewall rule"
    Ensure-FirewallRule -Port $Port -RuleName ("AttendanceMonitoring HTTP {0}" -f $Port)
}

if (-not $NoStartSite) {
    Write-Step "Starting app pool and site"
    if ($PSCmdlet.ShouldProcess($SiteName, "Start IIS app pool/site")) {
        if ($useWebAdministration) {
            Start-WebAppPool -Name $AppPoolName
            Start-Website -Name $SiteName
        }
        else {
            & $appcmd start apppool /apppool.name:"$AppPoolName" | Out-Null
            & $appcmd start site /site.name:"$SiteName" | Out-Null
        }
    }
}

Write-Step "Validation"
try {
    $localUrl = if ([string]::IsNullOrWhiteSpace($HostHeader)) { "http://localhost:$Port/" } else { "http://localhost:$Port/" }
    $resp = Invoke-WebRequest $localUrl -UseBasicParsing -TimeoutSec 20
    Write-Host "Local probe OK: $localUrl -> $($resp.StatusCode)" -ForegroundColor Green
}
catch {
    Write-Host "Local probe failed: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Deployment complete." -ForegroundColor Green
Write-Host ("Site Name     : {0}" -f $SiteName)
Write-Host ("App Pool      : {0}" -f $AppPoolName)
Write-Host ("Physical Path : {0}" -f $PhysicalPath)
if ([string]::IsNullOrWhiteSpace($HostHeader)) {
    Write-Host ("Binding       : http://*:{0}/" -f $Port)
}
else {
    Write-Host ("Binding       : http://{0}:{1}/" -f $HostHeader, $Port)
    Write-Host "DNS Note      : Point DNS A record for $HostHeader to this server IP."
}
Write-Host ""
Write-Host "Client tests:" -ForegroundColor Cyan
if ([string]::IsNullOrWhiteSpace($HostHeader)) {
    Write-Host ("  Invoke-WebRequest http://<server-ip>:{0}/ -UseBasicParsing" -f $Port)
}
else {
    Write-Host ("  Invoke-WebRequest http://{0}:{1}/ -UseBasicParsing" -f $HostHeader, $Port)
}
