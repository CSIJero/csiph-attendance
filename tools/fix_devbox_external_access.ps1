param(
    [string]$SiteName = "AttendanceMonitoring-DevBox",
    [string]$HostName = "csi-attendance",
    [switch]$OpenFirewall = $true,
    [switch]$AddDevBoxHostsEntry = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script in an elevated PowerShell session (Run as Administrator)."
    }
}

function Get-PrimaryIPv4 {
    $all = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -notlike "169.254.*" -and
            $_.IPAddress -ne "127.0.0.1" -and
            $_.PrefixOrigin -ne "WellKnown"
        }

    if (-not $all) {
        throw "Could not determine a usable IPv4 address for this machine."
    }

    # Prefer RFC1918 addresses typically used in corp networks.
    $preferred = $all | Where-Object {
        $_.IPAddress -like "10.*" -or
        $_.IPAddress -like "192.168.*" -or
        $_.IPAddress -like "172.16.*" -or
        $_.IPAddress -like "172.17.*" -or
        $_.IPAddress -like "172.18.*" -or
        $_.IPAddress -like "172.19.*" -or
        $_.IPAddress -like "172.2?.*" -or
        $_.IPAddress -like "172.30.*" -or
        $_.IPAddress -like "172.31.*"
    } | Sort-Object InterfaceMetric

    if ($preferred) {
        return $preferred[0].IPAddress
    }

    return ($all | Sort-Object InterfaceMetric)[0].IPAddress
}

function Get-SiteBindingLines([string]$siteName) {
    $output = & "$env:windir\System32\inetsrv\appcmd.exe" list site "$siteName" /text:bindings 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "IIS site '$siteName' was not found."
    }
    return $output
}

function Ensure-Binding([string]$siteName, [string]$bindingInfo) {
    $existing = Get-SiteBindingLines -siteName $siteName
    if ($existing -match [Regex]::Escape($bindingInfo)) {
        Write-Host "Binding already exists: $bindingInfo" -ForegroundColor Yellow
        return
    }

    & "$env:windir\System32\inetsrv\appcmd.exe" set site /site.name:"$siteName" "/+bindings.[protocol='http',bindingInformation='$bindingInfo']" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to add binding '$bindingInfo' to site '$siteName'."
    }

    Write-Host "Added binding: $bindingInfo" -ForegroundColor Green
}

function Ensure-FirewallRule([string]$ruleName) {
    $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($rule) {
        Write-Host "Firewall rule already exists: $ruleName" -ForegroundColor Yellow
        return
    }

    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 80 | Out-Null
    Write-Host "Created firewall rule: $ruleName" -ForegroundColor Green
}

function Add-HostsEntry([string]$ip, [string]$hostName) {
    $hostsPath = "$env:WINDIR\System32\drivers\etc\hosts"
    $line = "$ip`t$hostName"

    $content = Get-Content $hostsPath -ErrorAction Stop
    if ($content | Where-Object { $_ -match "(^|\s)$([Regex]::Escape($hostName))($|\s)" }) {
        Write-Host "Hosts file already has an entry for $hostName." -ForegroundColor Yellow
        return
    }

    Add-Content -Path $hostsPath -Value $line
    ipconfig /flushdns | Out-Null
    Write-Host "Added hosts entry on Dev Box: $line" -ForegroundColor Green
}

Assert-Admin

$ip = Get-PrimaryIPv4
$ipBinding = ("{0}:80:" -f $ip)
$hostBinding = "*:80:$HostName"

Write-Host "Detected Dev Box IPv4: $ip" -ForegroundColor Cyan
Write-Host "Target IIS site: $SiteName" -ForegroundColor Cyan

# Keep existing host-header binding and add an IP-specific binding for external access.
Ensure-Binding -siteName $SiteName -bindingInfo $hostBinding
Ensure-Binding -siteName $SiteName -bindingInfo $ipBinding

if ($OpenFirewall) {
    Ensure-FirewallRule -ruleName "AttendanceMonitoring DevBox HTTP 80"
}

if ($AddDevBoxHostsEntry) {
    Add-HostsEntry -ip $ip -hostName $HostName
}

& "$env:windir\System32\inetsrv\appcmd.exe" recycle apppool /apppool.name:"$SiteName" | Out-Null

Write-Host "" 
Write-Host "Done. Test from another machine using:" -ForegroundColor Green
Write-Host "  http://$ip/" -ForegroundColor Green
Write-Host "" 
Write-Host "If you want to use http://$HostName/ from other machines, add this on each client (or in DNS):" -ForegroundColor Cyan
Write-Host "  $ip`t$HostName" -ForegroundColor Cyan
