param(
    [string]$ProjectPath = "C:\Code\csiph-attendance-main\AttendanceMonitoring.csproj",
    [string]$WorkingDir = "C:\Code\csiph-attendance-main",
    [int]$LocalPort = 54169,
    [string]$CpolarRegion = "cn",
    [string]$CpolarSubdomain = "csi-attendance",
    [string]$PublicLoginUrl = "https://csi-attendance.cpolar.cn/Account/Login",
    [string]$KeepaliveScriptPath = "C:\Code\csiph-attendance-main\tools\cpolar_keepalive.ps1"
)

$ErrorActionPreference = "Stop"

function Stop-ExistingApp {
    $procs = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -ieq "dotnet.exe" -and $_.CommandLine -match [Regex]::Escape($ProjectPath)
    }

    foreach ($p in $procs) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-AppStarted {
    $url = "http://localhost:$LocalPort/Account/Login"

    Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $ProjectPath, "--urls", "http://localhost:$LocalPort") `
        -WorkingDirectory $WorkingDir `
        -WindowStyle Hidden

    Start-Sleep -Seconds 3

    try {
        $null = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 8
    }
    catch {
        # Let the app continue warming up even if first probe fails.
    }
}

function Restart-Cpolar {
    Get-Process cpolar -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $args = @(
        "http",
        $LocalPort.ToString(),
        "-region", $CpolarRegion,
        "-subdomain", $CpolarSubdomain,
        "-daemon", "on"
    )

    Start-Process -FilePath "cpolar" -ArgumentList $args -WindowStyle Hidden
}

function Ensure-KeepaliveStarted {
    if (-not (Test-Path $KeepaliveScriptPath)) {
        return
    }

    $existing = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -ieq "powershell.exe" -and $_.CommandLine -match [Regex]::Escape($KeepaliveScriptPath)
    } | Select-Object -First 1

    if ($existing) {
        return
    }

    $args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-WindowStyle", "Hidden",
        "-File", $KeepaliveScriptPath,
        "-LocalPort", $LocalPort.ToString(),
        "-CpolarRegion", $CpolarRegion,
        "-CpolarSubdomain", $CpolarSubdomain,
        "-PublicLoginUrl", $PublicLoginUrl
    )

    Start-Process -FilePath "powershell.exe" -ArgumentList $args -WindowStyle Hidden
}

Stop-ExistingApp
Ensure-AppStarted
Restart-Cpolar
Ensure-KeepaliveStarted
