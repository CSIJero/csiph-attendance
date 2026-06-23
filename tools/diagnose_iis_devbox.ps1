[CmdletBinding()]
param(
    [string]$SiteName = "AttendanceMonitoring-DevBox",
    [string]$AppPoolName = "AttendanceMonitoring-DevBox",
    [string]$PhysicalPath = "C:\inetpub\AttendanceMonitoring",
    [int]$Port = 8080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Try-Run {
    param([scriptblock]$Script)

    try {
        & $Script
    }
    catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
}

Write-Host "IIS diagnostics for Attendance Monitoring" -ForegroundColor Green

$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"

Write-Step "Basic machine checks"
Write-Host "Computer        : $env:COMPUTERNAME"
Write-Host "User            : $env:USERNAME"
Write-Host "Port            : $Port"
Write-Host "Site            : $SiteName"
Write-Host "App Pool        : $AppPoolName"
Write-Host "Physical Path   : $PhysicalPath"
Write-Host "appcmd exists   : $(Test-Path $appcmd)"
Write-Host "Site path exists: $(Test-Path $PhysicalPath)"

Write-Step "Local HTTP probe"
Try-Run {
    $response = Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 15
    Write-Host "HTTP status     : $($response.StatusCode)"
    if ($response.Headers.Location) {
        Write-Host "Location        : $($response.Headers.Location)"
    }
}

Try-Run {
    try {
        Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 15 | Out-Null
    }
    catch {
        $response = $null
        if ($_.Exception.PSObject.Properties.Match("Response").Count -gt 0) {
            $response = $_.Exception.Response
        }

        if ($null -ne $response) {
            $status = $response.StatusCode.value__
            Write-Host "HTTP status     : $status" -ForegroundColor Yellow
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
            $body = $reader.ReadToEnd()
            if (-not [string]::IsNullOrWhiteSpace($body)) {
                Write-Host "Response body   :"
                if ($body.Length -gt 2000) {
                    Write-Host $body.Substring(0, 2000)
                }
                else {
                    Write-Host $body
                }
            }
        }
        else {
            throw
        }
    }
}

if (Test-Path $appcmd) {
    Write-Step "IIS site and app pool"
    Try-Run {
        & $appcmd list apppool "$AppPoolName" /text:* | Out-Host
    }
    Try-Run {
        & $appcmd list site "$SiteName" /text:* | Out-Host
    }
}

Write-Step ".NET hosting bundle/runtime"
Try-Run {
    Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" |
        ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object {
            $_.DisplayName -match "ASP.NET Core|Hosting Bundle|\.NET Runtime|Windows Server Hosting"
        } |
        Sort-Object DisplayName |
        Select-Object DisplayName, DisplayVersion |
        Format-Table -AutoSize | Out-Host
}

if (Test-Path $PhysicalPath) {
    $webConfigPath = Join-Path $PhysicalPath "web.config"
    $appSettingsPath = Join-Path $PhysicalPath "appsettings.Production.json"
    $logsPath = Join-Path $PhysicalPath "logs"
    $dbPath = Join-Path $PhysicalPath "attendance.db"

    Write-Step "Deployed files"
    Try-Run {
        Get-ChildItem $PhysicalPath -Force |
            Select-Object Name, Length, LastWriteTime |
            Format-Table -AutoSize | Out-Host
    }

    if (Test-Path $webConfigPath) {
        Write-Step "web.config"
        Try-Run {
            Get-Content $webConfigPath -First 200 | Out-Host
        }
    }

    if (Test-Path $appSettingsPath) {
        Write-Step "appsettings.Production.json"
        Try-Run {
            Get-Content $appSettingsPath -First 200 | Out-Host
        }
    }

    Write-Step "Database file"
    Write-Host "attendance.db exists: $(Test-Path $dbPath)"
    if (Test-Path $dbPath) {
        Try-Run {
            Get-Item $dbPath | Select-Object FullName, Length, LastWriteTime | Format-List | Out-Host
        }
    }

    Write-Step "Folder permissions"
    Try-Run {
        icacls $PhysicalPath | Out-Host
    }

    if (Test-Path $logsPath) {
        Write-Step "App logs folder"
        Try-Run {
            Get-ChildItem $logsPath -Force |
                Select-Object Name, Length, LastWriteTime |
                Format-Table -AutoSize | Out-Host
        }
    }
    else {
        Write-Step "App logs folder"
        Write-Host "No logs folder found at $logsPath" -ForegroundColor Yellow
    }
}

Write-Step "Recent Windows event log entries"
Try-Run {
    Get-WinEvent -LogName Application -MaxEvents 80 |
        Where-Object {
            $_.ProviderName -match "IIS|AspNetCore|\.NET Runtime|Application Error"
        } |
        Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message |
        Format-List | Out-Host
}

Write-Step "Done"
Write-Host "If the local probe shows 500.30/500.31, the hosting bundle/runtime is the likely fix." -ForegroundColor Yellow
Write-Host "If the local probe shows SQLite or access denied errors, fix folder/db permissions for the IIS app pool identity." -ForegroundColor Yellow