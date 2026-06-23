param(
    [Parameter(Mandatory = $true)]
    [string]$ZoneName,

    [Parameter(Mandatory = $true)]
    [string]$RecordName,

    [Parameter(Mandatory = $true)]
    [string]$IPv4Address,

    [string]$DnsServer,

    [switch]$ForceUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-DnsCmdlets {
    if (-not (Get-Command Get-DnsServerResourceRecord -ErrorAction SilentlyContinue)) {
        throw "DnsServer PowerShell module is missing. Install RSAT DNS tools or run this on a DNS server."
    }
}

function Get-RecordFqdn {
    param([string]$Name, [string]$Zone)
    if ($Name -eq "@") {
        return $Zone
    }
    return ("{0}.{1}" -f $Name, $Zone)
}

Assert-DnsCmdlets

$common = @{
    ZoneName = $ZoneName
    Name = $RecordName
    RRType = "A"
}
if (-not [string]::IsNullOrWhiteSpace($DnsServer)) {
    $common["ComputerName"] = $DnsServer
}

$existing = Get-DnsServerResourceRecord @common -ErrorAction SilentlyContinue

if ($existing) {
    $current = @($existing | ForEach-Object { $_.RecordData.IPv4Address.IPAddressToString })
    if ($current -contains $IPv4Address) {
        Write-Host ("DNS already correct: {0} -> {1}" -f (Get-RecordFqdn -Name $RecordName -Zone $ZoneName), $IPv4Address) -ForegroundColor Green
    }
    else {
        if (-not $ForceUpdate) {
            throw ("Record exists with different IP(s): {0}. Re-run with -ForceUpdate to replace." -f ($current -join ", "))
        }

        foreach ($r in $existing) {
            Remove-DnsServerResourceRecord -ZoneName $ZoneName -InputObject $r -Force -ComputerName $DnsServer -ErrorAction SilentlyContinue | Out-Null
        }

        Add-DnsServerResourceRecordA -ZoneName $ZoneName -Name $RecordName -IPv4Address $IPv4Address -TimeToLive ([TimeSpan]::FromMinutes(10)) -ComputerName $DnsServer | Out-Null
        Write-Host ("DNS updated: {0} -> {1}" -f (Get-RecordFqdn -Name $RecordName -Zone $ZoneName), $IPv4Address) -ForegroundColor Green
    }
}
else {
    Add-DnsServerResourceRecordA -ZoneName $ZoneName -Name $RecordName -IPv4Address $IPv4Address -TimeToLive ([TimeSpan]::FromMinutes(10)) -ComputerName $DnsServer | Out-Null
    Write-Host ("DNS created: {0} -> {1}" -f (Get-RecordFqdn -Name $RecordName -Zone $ZoneName), $IPv4Address) -ForegroundColor Green
}

Write-Host "" 
Write-Host "Validation command (client):" -ForegroundColor Cyan
Write-Host ("  nslookup {0}" -f (Get-RecordFqdn -Name $RecordName -Zone $ZoneName))
Write-Host ("  Invoke-WebRequest http://{0}/ -UseBasicParsing" -f (Get-RecordFqdn -Name $RecordName -Zone $ZoneName))
