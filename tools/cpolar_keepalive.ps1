param(
    [int]$LocalPort = 54169,
    [string]$CpolarRegion = "cn",
    [string]$CpolarSubdomain = "csi-attendance",
    [string]$PublicLoginUrl = "https://csi-attendance.cpolar.cn/Account/Login",
    [int]$CheckIntervalSeconds = 30,
    [switch]$RunOnce
)

$ErrorActionPreference = "SilentlyContinue"

function Get-StatusCode {
    param([string]$Url)

    try {
        $res = Invoke-WebRequest -Uri $Url -TimeoutSec 10 -MaximumRedirection 0 -UseBasicParsing
        return [int]$res.StatusCode
    }
    catch {
        if ($_.Exception.Response) {
            return [int]$_.Exception.Response.StatusCode
        }

        return -1
    }
}

function Start-CpolarTunnel {
    $args = @(
        "http",
        $LocalPort.ToString(),
        "-region", $CpolarRegion,
        "-subdomain", $CpolarSubdomain,
        "-daemon", "on"
    )

    Start-Process -FilePath "cpolar" -ArgumentList $args -WindowStyle Hidden
}

function Ensure-CpolarHealthy {
    $localStatus = Get-StatusCode -Url "http://localhost:$LocalPort/Account/Login"
    $publicStatus = Get-StatusCode -Url $PublicLoginUrl

    # Only repair the tunnel when local app is serving and public edge is down.
    if (($localStatus -eq 200 -or $localStatus -eq 302) -and ($publicStatus -lt 200 -or $publicStatus -ge 400)) {
        Get-Process cpolar -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-CpolarTunnel
    }
}

if ($RunOnce) {
    Ensure-CpolarHealthy
    exit 0
}

while ($true) {
    Ensure-CpolarHealthy
    Start-Sleep -Seconds $CheckIntervalSeconds
}
