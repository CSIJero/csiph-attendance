[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SiteName = "AttendanceMonitoring-DevBox",
    [string]$AppPoolName = "AttendanceMonitoring-DevBox",
    [string]$PhysicalPath = "C:\inetpub\AttendanceMonitoring",
    [string]$PublishConfiguration = "Release",
    [int]$Port = 8080,
    [string]$HostHeader = "",
    [string]$EnvironmentName = "Production",
    [string]$ConnectionString,
    [string]$SourceDbPath,
    [string]$TargetDbName = "attendance.db",
    [switch]$SkipDataMigration,
    [switch]$RequireSecureCookie,
    [switch]$SkipBuild,
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
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        throw "IIS management module is missing. Enable IIS + Management Console first."
    }
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

    # Use // so we find system.webServer whether it's at root or inside a <location> element
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

Assert-Admin
Assert-IisAvailable

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "AttendanceMonitoring.csproj"
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Cannot find project file at $projectPath"
}

$publishPath = Join-Path $repoRoot "artifacts\publish\iis"

if (-not $SkipBuild) {
    Write-Step "Publishing ASP.NET app for IIS"
    if ($PSCmdlet.ShouldProcess($projectPath, "dotnet publish to $publishPath")) {
        dotnet publish $projectPath -c $PublishConfiguration -o $publishPath
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE"
        }
    }
}

Write-Step "Preparing target folder"
if ($PSCmdlet.ShouldProcess($PhysicalPath, "Create/refresh IIS site folder")) {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null
    if (-not $SkipBuild) {
        robocopy $publishPath $PhysicalPath /MIR /NFL /NDL /NJH /NJS /NC /NS | Out-Null
        if ($LASTEXITCODE -gt 7) {
            throw "robocopy failed with exit code $LASTEXITCODE"
        }
    }
}

$webConfigPath = Join-Path $PhysicalPath "web.config"
if (-not (Test-Path -LiteralPath $webConfigPath)) {
    throw "web.config was not found at $webConfigPath. Ensure publish completed successfully."
}

if (-not $SkipDataMigration) {
    Write-Step "Migrating SQLite data file (if provided)"

    $effectiveSourceDb = $SourceDbPath
    if ([string]::IsNullOrWhiteSpace($effectiveSourceDb)) {
        $defaultCandidate = Join-Path $repoRoot "attendance.db"
        if (Test-Path -LiteralPath $defaultCandidate) {
            $effectiveSourceDb = $defaultCandidate
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($effectiveSourceDb)) {
        if (-not (Test-Path -LiteralPath $effectiveSourceDb)) {
            throw "Source DB file not found: $effectiveSourceDb"
        }

        $targetDbPath = Join-Path $PhysicalPath $TargetDbName
        if ($PSCmdlet.ShouldProcess($targetDbPath, "Copy source SQLite DB into IIS app folder")) {
            if (Test-Path -LiteralPath $targetDbPath) {
                $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
                $backupPath = "$targetDbPath.bak.$stamp"
                Copy-Item -LiteralPath $targetDbPath -Destination $backupPath -Force
                Write-Host "Backed up existing target DB to: $backupPath"
            }

            Copy-Item -LiteralPath $effectiveSourceDb -Destination $targetDbPath -Force
            Write-Host "Migrated DB: $effectiveSourceDb -> $targetDbPath"
        }
    }
    else {
        Write-Host "No source DB detected. Skipping data migration." -ForegroundColor Yellow
        Write-Host "Tip: pass -SourceDbPath 'C:\path\to\attendance.db' to migrate existing data."
    }
}

Write-Step "Configuring app environment variables in web.config"
if ($PSCmdlet.ShouldProcess($webConfigPath, "Set ASPNETCORE_ENVIRONMENT and optional app settings")) {
    $appDllName = "{0}.dll" -f [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    Set-WebConfigEnvironmentVariable -WebConfigPath $webConfigPath -Name "ASPNETCORE_ENVIRONMENT" -Value $EnvironmentName -AppDllName $appDllName

    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        Set-WebConfigEnvironmentVariable -WebConfigPath $webConfigPath -Name "ConnectionStrings__DefaultConnection" -Value $ConnectionString -AppDllName $appDllName
    }

    if ($RequireSecureCookie.IsPresent) {
        Set-WebConfigEnvironmentVariable -WebConfigPath $webConfigPath -Name "AttendanceMonitoring__RequireSecureCookie" -Value "true" -AppDllName $appDllName
    }
}

Import-Module WebAdministration

Write-Step "Configuring IIS app pool"
if ($PSCmdlet.ShouldProcess("IIS AppPool $AppPoolName", "Create/update app pool")) {
    if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
        New-WebAppPool -Name $AppPoolName | Out-Null
    }

    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ""
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedPipelineMode -Value "Integrated"
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType -Value "ApplicationPoolIdentity"
}

Write-Step "Configuring IIS site"
if ($PSCmdlet.ShouldProcess("IIS Site $SiteName", "Create/update site and bindings")) {
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

if (-not $NoStartSite) {
    Write-Step "Starting app pool and site"
    if ($PSCmdlet.ShouldProcess("IIS Site $SiteName", "Start app pool/site")) {
        Start-WebAppPool -Name $AppPoolName
        Start-Website -Name $SiteName
    }
}

if (-not [string]::IsNullOrWhiteSpace($HostHeader)) {
    Write-Step "Registering $HostHeader in Windows hosts file"
    if ($PSCmdlet.ShouldProcess("C:\Windows\System32\drivers\etc\hosts", "Add $HostHeader -> 127.0.0.1")) {
        $hostsPath = "C:\Windows\System32\drivers\etc\hosts"
        $hostsContent = Get-Content $hostsPath -Raw
        $entry = "127.0.0.1`t$HostHeader"
        if ($hostsContent -notmatch [regex]::Escape($HostHeader)) {
            Add-Content -Path $hostsPath -Value "`n$entry"
            Write-Host "Added hosts entry: $entry"
        }
        else {
            Write-Host "Hosts entry already exists for $HostHeader"
        }
    }
}

Write-Step "Done"
Write-Host "Site Name        : $SiteName"
Write-Host "App Pool         : $AppPoolName"
Write-Host "Physical Path    : $PhysicalPath"
if (-not [string]::IsNullOrWhiteSpace($HostHeader)) {
    Write-Host "Binding          : http://${HostHeader}:${Port}/"
    Write-Host "Host Header      : $HostHeader"
} else {
    Write-Host "Binding          : http://localhost:${Port}/"
}
Write-Host "Environment      : $EnvironmentName"
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    Write-Host "ConnectionString : set in web.config environmentVariables"
}
