[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^https?://')]
    [string]$BaseUrl,

    [ValidateRange(1, 10000)]
    [int]$Users = 100,

    [ValidateRange(1, 86400)]
    [int]$DurationSeconds = 60,

    [ValidateRange(1, 3600)]
    [int]$HeartbeatIntervalSeconds = 15,

    [ValidateRange(0, 3600)]
    [int]$StartupSpreadSeconds = 15,

    [string]$Username = 'admin',

    [string]$Password = 'Admin123!',

    [string]$CredentialCsv,

    [switch]$SkipCertificateCheck,

    [ValidateRange(0, 100)]
    [double]$MaximumErrorPercent = 1,

    [ValidateRange(1, 60000)]
    [double]$MaximumP95Milliseconds = 1000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$BaseUrl = $BaseUrl.TrimEnd('/')

function Get-StatusCode {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)

    if ($ErrorRecord.Exception.PSObject.Properties['Response'] -and
        $null -ne $ErrorRecord.Exception.Response) {
        return [int]$ErrorRecord.Exception.Response.StatusCode
    }

    return 0
}

function Invoke-TestRequest {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Parameters
    )

    if ($SkipCertificateCheck) {
        $Parameters.SkipCertificateCheck = $true
    }

    Invoke-WebRequest @Parameters
}

function New-AuthenticatedSession {
    param(
        [Parameter(Mandatory)]
        [string]$LoginUsername,

        [Parameter(Mandatory)]
        [string]$LoginPassword
    )

    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $loginPage = Invoke-TestRequest @{
        Uri = "$BaseUrl/Account/Login"
        Method = 'GET'
        WebSession = $session
    }

    $tokenMatch = [regex]::Match(
        $loginPage.Content,
        'name="__RequestVerificationToken"[^>]*value="([^"]+)"',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    if (-not $tokenMatch.Success) {
        throw 'The login page did not contain an anti-forgery token.'
    }

    $loginResponse = Invoke-TestRequest @{
        Uri = "$BaseUrl/Account/Login"
        Method = 'POST'
        WebSession = $session
        ContentType = 'application/x-www-form-urlencoded'
        Body = @{
            Username = $LoginUsername
            Password = $LoginPassword
            __RequestVerificationToken = $tokenMatch.Groups[1].Value
        }
    }

    if ($loginResponse.BaseResponse.RequestMessage.RequestUri.AbsolutePath -match '/Account/Login$') {
        throw "Login failed for '$LoginUsername'."
    }

    return $session
}

function Invoke-SecurityProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Request,

        [Parameter(Mandatory)]
        [int[]]$SafeStatusCodes,

        [Parameter(Mandatory)]
        [string]$Risk
    )

    try {
        $response = & $Request
        $statusCode = [int]$response.StatusCode
    }
    catch {
        $statusCode = Get-StatusCode $_
    }

    [pscustomobject]@{
        Probe = $Name
        Status = $statusCode
        Result = if ($statusCode -in $SafeStatusCodes) { 'PASS' } else { 'REVIEW' }
        Risk = $Risk
    }
}

if ($CredentialCsv) {
    if (-not (Test-Path -LiteralPath $CredentialCsv -PathType Leaf)) {
        throw "Credential CSV not found: $CredentialCsv"
    }

    $credentials = @(Import-Csv -LiteralPath $CredentialCsv)
    if ($credentials.Count -lt $Users) {
        throw "Credential CSV needs at least $Users rows with Username and Password columns."
    }
    if ($credentials | Where-Object { -not $_.Username -or -not $_.Password }) {
        throw 'Every credential CSV row must contain Username and Password.'
    }
    $credentialMode = "$Users distinct credential rows"
}
else {
    $credentials = 1..$Users | ForEach-Object {
        [pscustomobject]@{ Username = $Username; Password = $Password }
    }
    $credentialMode = "$Users independent sessions using '$Username'"
}

Write-Host "Target: $BaseUrl"
Write-Host "Load: $Users clients, $DurationSeconds seconds, heartbeat every $HeartbeatIntervalSeconds seconds"
Write-Host "Startup spread: $StartupSpreadSeconds seconds"
Write-Host "Credentials: $credentialMode"

$parallelResults = $credentials[0..($Users - 1)] | ForEach-Object -Parallel {
    $client = $_
    $baseUrl = $using:BaseUrl
    $duration = $using:DurationSeconds
    $interval = $using:HeartbeatIntervalSeconds
    $startupSpread = $using:StartupSpreadSeconds
    $skipCertificateCheck = $using:SkipCertificateCheck
    $ProgressPreference = 'SilentlyContinue'

    function Invoke-ClientRequest {
        param([hashtable]$Parameters)
        if ($skipCertificateCheck) {
            $Parameters.SkipCertificateCheck = $true
        }
        Invoke-WebRequest @Parameters
    }

    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    try {
        $loginPage = Invoke-ClientRequest @{
            Uri = "$baseUrl/Account/Login"
            Method = 'GET'
            WebSession = $session
        }
        $tokenMatch = [regex]::Match(
            $loginPage.Content,
            'name="__RequestVerificationToken"[^>]*value="([^"]+)"',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
        )
        if (-not $tokenMatch.Success) {
            throw 'Missing anti-forgery token.'
        }

        $loginResponse = Invoke-ClientRequest @{
            Uri = "$baseUrl/Account/Login"
            Method = 'POST'
            WebSession = $session
            ContentType = 'application/x-www-form-urlencoded'
            Body = @{
                Username = $client.Username
                Password = $client.Password
                __RequestVerificationToken = $tokenMatch.Groups[1].Value
            }
        }
        if ($loginResponse.BaseResponse.RequestMessage.RequestUri.AbsolutePath -match '/Account/Login$') {
            throw 'Invalid credentials.'
        }
    }
    catch {
        [pscustomobject]@{
            Phase = 'login'
            User = $client.Username
            Status = 0
            Milliseconds = 0
            Error = $_.Exception.Message
        }
        return
    }

    if ($startupSpread -gt 0) {
        Start-Sleep -Milliseconds (Get-Random -Minimum 0 -Maximum ($startupSpread * 1000))
    }

    $testClock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($testClock.Elapsed.TotalSeconds -lt $duration) {
        $requestClock = [System.Diagnostics.Stopwatch]::StartNew()
        $statusCode = 0
        $errorMessage = $null

        try {
            $response = Invoke-ClientRequest @{
                Uri = "$baseUrl/api/heartbeat"
                Method = 'POST'
                WebSession = $session
                ContentType = 'application/json'
                Headers = @{ 'X-Requested-With' = 'fetch' }
                Body = '{"state":"online"}'
                MaximumRedirection = 0
                SkipHttpErrorCheck = $true
            }
            $statusCode = [int]$response.StatusCode
        }
        catch {
            if ($_.Exception.PSObject.Properties['Response'] -and
                $null -ne $_.Exception.Response) {
                $statusCode = [int]$_.Exception.Response.StatusCode
            }
            $errorMessage = $_.Exception.Message
        }
        finally {
            $requestClock.Stop()
        }

        [pscustomobject]@{
            Phase = 'heartbeat'
            User = $client.Username
            Status = $statusCode
            Milliseconds = [math]::Round($requestClock.Elapsed.TotalMilliseconds, 2)
            Error = $errorMessage
        }

        $remainingDelay = $interval - $requestClock.Elapsed.TotalSeconds
        if ($remainingDelay -gt 0) {
            Start-Sleep -Milliseconds ([int]($remainingDelay * 1000))
        }
    }
} -ThrottleLimit $Users

$loginFailures = @($parallelResults | Where-Object Phase -eq 'login')
$heartbeats = @($parallelResults | Where-Object Phase -eq 'heartbeat')
$failedHeartbeats = @($heartbeats | Where-Object { $_.Status -lt 200 -or $_.Status -ge 300 })
$latencies = @($heartbeats | Where-Object { $_.Status -ge 200 -and $_.Status -lt 300 } |
    Select-Object -ExpandProperty Milliseconds | Sort-Object)

function Get-Percentile {
    param(
        [double[]]$Values,
        [ValidateRange(0, 100)]
        [double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return 0
    }

    $index = [math]::Ceiling(($Percentile / 100) * $Values.Count) - 1
    return [math]::Round($Values[[math]::Max(0, $index)], 2)
}

$errorPercent = if ($heartbeats.Count) {
    [math]::Round(($failedHeartbeats.Count / $heartbeats.Count) * 100, 2)
}
else {
    100
}

$summary = [pscustomobject]@{
    Clients = $Users
    LoginFailures = $loginFailures.Count
    Heartbeats = $heartbeats.Count
    FailedHeartbeats = $failedHeartbeats.Count
    ErrorPercent = $errorPercent
    P50Milliseconds = Get-Percentile $latencies 50
    P95Milliseconds = Get-Percentile $latencies 95
    P99Milliseconds = Get-Percentile $latencies 99
}

Write-Host "`nLoad-test result"
$summary | Format-List

if ($failedHeartbeats.Count) {
    Write-Host 'Failure status codes'
    $failedHeartbeats | Group-Object Status | Select-Object Name, Count | Format-Table -AutoSize
}

Write-Host 'Security probes'
$unauthenticatedParameters = @{
    Uri = "$BaseUrl/api/heartbeat"
    Method = 'POST'
    ContentType = 'application/json'
    Body = '{"state":"online"}'
    MaximumRedirection = 0
    SkipHttpErrorCheck = $true
}
if ($SkipCertificateCheck) {
    $unauthenticatedParameters.SkipCertificateCheck = $true
}

$securityResults = @()
$securityResults += Invoke-SecurityProbe `
    -Name 'Unauthenticated heartbeat' `
    -SafeStatusCodes @(302, 401, 403) `
    -Risk 'Anonymous callers can write presence data.' `
    -Request { Invoke-WebRequest @unauthenticatedParameters }

if (-not $loginFailures.Count) {
    $probeSession = New-AuthenticatedSession -LoginUsername $credentials[0].Username `
        -LoginPassword $credentials[0].Password

    $securityResults += Invoke-SecurityProbe `
        -Name 'Invalid presence state' `
        -SafeStatusCodes @(400, 422) `
        -Risk 'Unvalidated state values can corrupt presence data or reach unsafe rendering paths.' `
        -Request {
            Invoke-TestRequest @{
                Uri = "$BaseUrl/api/heartbeat"
                Method = 'POST'
                WebSession = $probeSession
                ContentType = 'application/json'
                Body = '{"state":"<script>alert(1)</script>"}'
                MaximumRedirection = 0
                SkipHttpErrorCheck = $true
            }
        }

    $oversizedState = 'a' * 65536
    $oversizedBody = @{ state = $oversizedState } | ConvertTo-Json -Compress
    $securityResults += Invoke-SecurityProbe `
        -Name 'Oversized heartbeat body' `
        -SafeStatusCodes @(400, 413, 422) `
        -Risk 'Unbounded request bodies increase memory and denial-of-service exposure.' `
        -Request {
            Invoke-TestRequest @{
                Uri = "$BaseUrl/api/heartbeat"
                Method = 'POST'
                WebSession = $probeSession
                ContentType = 'application/json'
                Body = $oversizedBody
                MaximumRedirection = 0
                SkipHttpErrorCheck = $true
            }
        }

    $burstStatuses = 1..50 | ForEach-Object {
        $parameters = @{
            Uri = "$BaseUrl/api/heartbeat"
            Method = 'POST'
            WebSession = $probeSession
            ContentType = 'application/json'
            Body = '{"state":"online"}'
            MaximumRedirection = 0
            SkipHttpErrorCheck = $true
        }
        if ($SkipCertificateCheck) {
            $parameters.SkipCertificateCheck = $true
        }
        try {
            [int](Invoke-WebRequest @parameters).StatusCode
        }
        catch {
            if ($_.Exception.PSObject.Properties['Response'] -and
                $null -ne $_.Exception.Response) {
                [int]$_.Exception.Response.StatusCode
            }
            else {
                0
            }
        }
    }

    $securityResults += [pscustomobject]@{
        Probe = 'Per-session burst limiting'
        Status = ($burstStatuses -join ',')
        Result = if (429 -in $burstStatuses) { 'PASS' } else { 'REVIEW' }
        Risk = 'No 429 response was observed during a 50-request burst; endpoint abuse can amplify SQLite write contention.'
    }
}

$securityResults | Format-Table -Wrap -AutoSize

$loadPassed = $loginFailures.Count -eq 0 `
    -and $heartbeats.Count -gt 0 `
    -and $errorPercent -le $MaximumErrorPercent `
    -and $summary.P95Milliseconds -le $MaximumP95Milliseconds
$securityPassed = @($securityResults | Where-Object Result -eq 'REVIEW').Count -eq 0

if (-not $loadPassed -or -not $securityPassed) {
    Write-Error 'Heartbeat test requires review. See load thresholds and security probe results above.'
    exit 1
}

Write-Host 'Heartbeat load and security checks passed.'
