param(
    [string]$CpolarExePath = "C:\Program Files\cpolar\cpolar.exe",
    [string]$CpolarSubdomain = "csi-attendance",
    [string]$CpolarRegion = "cn",
    [bool]$EnableRandomFallback = $true,
    [int]$LocalPort = 5000,
    [string]$PublicUrl = "https://csi-attendance.cpolar.cn",
    [string]$LocalHealthUrl = "http://localhost:5000/api/status",
    [string]$AppProjectPath = "C:\Code\csiph-attendance-main\AttendanceMonitoring.csproj",
    [string]$AppWorkingDir = "C:\Code\csiph-attendance-main",
    [string]$AppUrls = "http://localhost:5000",
    [string]$LogPath = "C:\Code\csiph-attendance-main\logs\cpolar_watchdog.log"
)

$ErrorActionPreference = "Stop"

try {
    $tls12 = [Net.SecurityProtocolType]::Tls12
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor $tls12
}
catch {
    # Ignore protocol tuning failures; fallback checks below still apply.
}

function Write-Log {
    param([string]$Message)
    $dir = Split-Path -Parent $LogPath
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Add-Content -Path $LogPath -Value ("[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message)
}

function Get-StatusCode {
    param([string]$Url)
    try {
        # Keep probes snappy so a single watchdog tick can recover faster.
        $res = Invoke-WebRequest -Uri $Url -TimeoutSec 4 -MaximumRedirection 0 -UseBasicParsing
        return [int]$res.StatusCode
    }
    catch {
        if ($_.Exception.Response) {
            return [int]$_.Exception.Response.StatusCode
        }
        if ($Url.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
            try {
                $httpUrl = "http://" + $Url.Substring(8)
                $res2 = Invoke-WebRequest -Uri $httpUrl -TimeoutSec 4 -MaximumRedirection 0 -UseBasicParsing
                return [int]$res2.StatusCode
            }
            catch {
                if ($_.Exception.Response) {
                    return [int]$_.Exception.Response.StatusCode
                }
            }
        }
        return -1
    }
}

function Find-LocalAppProcess {
    $projectPattern = [Regex]::Escape($AppProjectPath)
    Get-CimInstance Win32_Process | Where-Object {
        ($_.Name -ieq "AttendanceMonitoring.exe") -or
        (($_.Name -ieq "dotnet.exe") -and ($_.CommandLine -match $projectPattern)) -or
        (($_.Name -ieq "dotnet.exe") -and ($_.CommandLine -match "AttendanceMonitoring.csproj"))
    } | Select-Object -First 1
}

function Ensure-LocalAppRunning {
    if (-not (Test-Path $AppProjectPath)) {
        Write-Log "local app project not found at $AppProjectPath"
        return $false
    }

    $localStatus = Get-StatusCode -Url $LocalHealthUrl
    if ($localStatus -ne -1 -and $localStatus -ne 404) {
        return $true
    }

    $existing = Find-LocalAppProcess
    if ($existing) {
        # Process exists but endpoint is not healthy yet; give it a moment.
        Start-Sleep -Seconds 6
        $retryStatus = Get-StatusCode -Url $LocalHealthUrl
        if ($retryStatus -ne -1 -and $retryStatus -ne 404) {
            Write-Log "local app recovered without restart (status=$retryStatus)"
            return $true
        }
    }

    Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $AppProjectPath, "--urls", $AppUrls) `
        -WorkingDirectory $AppWorkingDir `
        -WindowStyle Hidden
    Write-Log "started local app (dotnet run)"

    Start-Sleep -Seconds 8
    $afterStart = Get-StatusCode -Url $LocalHealthUrl
    if ($afterStart -eq -1 -or $afterStart -eq 404) {
        Write-Log "local app still unhealthy after start attempt (status=$afterStart)"
        return $false
    }

    Write-Log "local app healthy after start (status=$afterStart)"
    return $true
}

function Ensure-TunnelOnline {
    param(
        [int]$Attempts = 3,
        [int]$DelaySeconds = 2
    )

    for ($i = 1; $i -le $Attempts; $i++) {
        $status = Get-StatusCode -Url "$PublicUrl/api/status"
        if ($status -ne -1 -and $status -ne 404) {
            if ($i -gt 1) {
                Write-Log "tunnel healthy after retry #$i (status=$status)"
            }
            return $true
        }

        if ($i -lt $Attempts) {
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    return $false
}

function Get-CpolarPublicUrls {
    $urls = New-Object System.Collections.Generic.HashSet[string]
    foreach ($port in @(4040, 4042, 4044, 4046)) {
        try {
            $html = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}" -f $port) -TimeoutSec 3 -UseBasicParsing
            $matches = [Regex]::Matches($html.Content, 'https?://[A-Za-z0-9\.-]+')
            foreach ($m in $matches) {
                if ($m.Value -match 'cpolar\.') {
                    [void]$urls.Add($m.Value.TrimEnd('/'))
                }
            }
        }
        catch {
            # Ignore missing/closed inspect ports.
        }
    }
    return @($urls)
}

function Ensure-AnyTunnelOnline {
    param(
        [int]$Attempts = 3,
        [int]$DelaySeconds = 2
    )

    for ($i = 1; $i -le $Attempts; $i++) {
        $urls = Get-CpolarPublicUrls
        foreach ($url in $urls) {
            $status = Get-StatusCode -Url "$url/api/status"
            if ($status -ne -1 -and $status -ne 404) {
                return [PSCustomObject]@{
                    Online = $true
                    Url = $url
                    Status = $status
                    Attempt = $i
                }
            }
        }

        if ($i -lt $Attempts) {
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    return [PSCustomObject]@{
        Online = $false
        Url = ""
        Status = -1
        Attempt = $Attempts
    }
}

function Start-CpolarTunnel {
    param([switch]$UseSubdomain)

    if ($UseSubdomain) {
        $args = "http -region=$CpolarRegion -subdomain=$CpolarSubdomain $LocalPort"
    }
    else {
        $args = "http -region=$CpolarRegion $LocalPort"
    }

    # cpolar v3 can fail to initialize correctly when launched hidden from
    # scheduled/system contexts. Launch via cmd/start to mimic manual runs.
    $cmd = 'start "cpolar-watchdog" /min "{0}" {1}' -f $CpolarExePath, $args
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c", $cmd -WindowStyle Hidden
    Start-Sleep -Seconds 4

    $cpolarProc = Get-Process -Name cpolar -ErrorAction SilentlyContinue
    if (-not $cpolarProc) {
        Write-Log "cpolar failed to stay running after start command: $args"
        return $false
    }

    return $true
}

function Try-RandomTunnelFallback {
    Write-Log "subdomain tunnel still unhealthy; trying random tunnel fallback"
    Get-Process -Name cpolar -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $fallbackStarted = Start-CpolarTunnel
    if (-not $fallbackStarted) {
        Write-Log "random tunnel fallback failed to start"
        return
    }

    $fallback = Ensure-AnyTunnelOnline -Attempts 10 -DelaySeconds 2
    if ($fallback.Online) {
        Write-Log "random tunnel fallback online (status=$($fallback.Status)): $($fallback.Url)"
        $statusPath = Join-Path (Split-Path -Parent $LogPath) "cpolar_active_url.txt"
        Set-Content -Path $statusPath -Value $fallback.Url -Encoding UTF8
    }
    else {
        Write-Log "random tunnel fallback failed to become healthy"
    }
}

function Test-CpolarControlPlaneReachable {
    # cpolar currently fails against a fixed control-plane endpoint when the
    # tunnel cannot establish. Probe it directly so we can stop restarting the
    # tunnel as soon as the broker is unreachable.
    try {
        $connection = Test-NetConnection -ComputerName "116.211.150.221" -Port 4443 -WarningAction SilentlyContinue
        return [bool]$connection.TcpTestSucceeded
    }
    catch {
        return $false
    }
}

if (-not (Test-Path $CpolarExePath)) {
    Write-Log "cpolar executable not found at $CpolarExePath"
    exit 1
}

Write-Log "tick: watchdog started"

$localHealthy = Ensure-LocalAppRunning
if (-not $localHealthy) {
    Write-Log "local app not healthy; continuing tunnel recovery attempt"
}

$needRestart = $false
$reason = ""

$cpolarProc = Get-Process -Name cpolar -ErrorAction SilentlyContinue
if (-not $cpolarProc) {
    $needRestart = $true
    $reason = "cpolar process missing"
}
else {
    $webUiHealthy = $false
    try {
        $webUi = Invoke-WebRequest -Uri "http://127.0.0.1:4042" -TimeoutSec 5 -UseBasicParsing
        if ([int]$webUi.StatusCode -ne 200) {
            $webUiHealthy = $false
        }
        else {
            $webUiHealthy = $true
        }
    }
    catch {
        $webUiHealthy = $false
    }

    # Treat any active cpolar forwarding URL as healthy so fallback mode
    # (random tunnel) does not flap every tick.
    $anyOnline = Ensure-AnyTunnelOnline -Attempts 1 -DelaySeconds 0
    if ($anyOnline.Online) {
        if ($anyOnline.Url -ne $PublicUrl) {
            Write-Log "healthy via fallback tunnel (status=$($anyOnline.Status)): $($anyOnline.Url)"
        }
    }
    elseif (-not (Ensure-TunnelOnline -Attempts 4 -DelaySeconds 2)) {
        $publicStatus = Get-StatusCode -Url "$PublicUrl/api/status"
        $needRestart = $true
        if (-not $webUiHealthy) {
            $reason = "public tunnel unhealthy (status=$publicStatus), cpolar web UI unreachable"
        }
        else {
            $reason = "public tunnel unhealthy (status=$publicStatus)"
        }
    }
}

if ($needRestart) {
    if (-not (Test-CpolarControlPlaneReachable)) {
        Write-Log "cpolar control plane unreachable; skipping restart loop until network access is restored"
        exit 0
    }

    Get-Process -Name cpolar -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $started = Start-CpolarTunnel -UseSubdomain
    if (-not $started) {
        Write-Log "cpolar restart failed immediately; verify cpolar account/token and tunnel permissions"
        if ($EnableRandomFallback) {
            Try-RandomTunnelFallback
        }
    }
    elseif (Ensure-TunnelOnline -Attempts 10 -DelaySeconds 2) {
        Write-Log "restarted cpolar via subdomain tunnel and recovered: $reason"
    }
    elseif ($EnableRandomFallback) {
        Try-RandomTunnelFallback
    }
    else {
        $postStatus = Get-StatusCode -Url "$PublicUrl/api/status"
        Write-Log "restarted cpolar via subdomain tunnel but still unhealthy (status=$postStatus): $reason; cpolar is stuck connecting, verify account region/subdomain in cpolar dashboard"
    }
}
else {
    Write-Log "healthy"
}
