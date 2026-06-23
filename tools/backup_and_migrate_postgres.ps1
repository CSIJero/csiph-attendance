param(
    [string]$SourceDatabaseUrl,

    [string]$TargetDatabaseUrl,

    [switch]$RestoreToTarget,

    [string]$BackupDir = ".\\backups",

    [switch]$CompressSql,

    [switch]$SkipPlainSql,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourceDatabaseUrl)) {
    $SourceDatabaseUrl = [Environment]::GetEnvironmentVariable("SOURCE_DATABASE_URL", "Process")
}

if ([string]::IsNullOrWhiteSpace($TargetDatabaseUrl)) {
    $TargetDatabaseUrl = [Environment]::GetEnvironmentVariable("TARGET_DATABASE_URL", "Process")
}

if ([string]::IsNullOrWhiteSpace($SourceDatabaseUrl)) {
    throw "Source database URL is required. Pass -SourceDatabaseUrl or set SOURCE_DATABASE_URL in this terminal session."
}

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Get-ToolOrThrow {
    param([string]$ToolName)
    $cmd = Get-Command $ToolName -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw "Required tool '$ToolName' was not found on PATH. Install PostgreSQL client tools and retry."
    }
    return $cmd.Source
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [hashtable]$Environment
    )

    $previous = @{}
    if ($Environment) {
        foreach ($key in $Environment.Keys) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
            [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], "Process")
        }
    }

    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw ("Command failed with exit code {0}: {1} {2}" -f $LASTEXITCODE, $FilePath, ($Arguments -join ' '))
        }
    }
    finally {
        if ($Environment) {
            foreach ($key in $Environment.Keys) {
                [Environment]::SetEnvironmentVariable($key, $previous[$key], "Process")
            }
        }
    }
}

if ($RestoreToTarget -and [string]::IsNullOrWhiteSpace($TargetDatabaseUrl)) {
    throw "-TargetDatabaseUrl is required when -RestoreToTarget is specified."
}

if ($RestoreToTarget -and -not $Force) {
    throw "Refusing to restore without explicit confirmation. Re-run with -Force to continue."
}

$pgDump = Get-ToolOrThrow -ToolName "pg_dump"
$psql = Get-ToolOrThrow -ToolName "psql"
$pgRestore = Get-ToolOrThrow -ToolName "pg_restore"

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null

$dumpFile = Join-Path $BackupDir "attendance_backup_$timestamp.dump"
$sqlFile = Join-Path $BackupDir "attendance_backup_$timestamp.sql"
if ($CompressSql) {
    $sqlFile = "$sqlFile.gz"
}

Write-Step "Checking source DB connectivity"
Invoke-External -FilePath $psql -Arguments @("$SourceDatabaseUrl", "-c", "select now() as backup_started_at;")

Write-Step "Creating custom-format backup (.dump)"
Invoke-External -FilePath $pgDump -Arguments @(
    "--format=custom",
    "--no-owner",
    "--no-privileges",
    "--verbose",
    "--file", $dumpFile,
    "$SourceDatabaseUrl"
)

if (-not $SkipPlainSql) {
    Write-Step "Creating plain SQL backup"
    if ($CompressSql) {
        $gzip = Get-Command gzip -ErrorAction SilentlyContinue
        if (-not $gzip) {
            throw "gzip not found on PATH. Remove -CompressSql or install gzip and retry."
        }

        & $pgDump --format=plain --no-owner --no-privileges $SourceDatabaseUrl | & $gzip -9 > $sqlFile
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create compressed SQL backup."
        }
    }
    else {
        Invoke-External -FilePath $pgDump -Arguments @(
            "--format=plain",
            "--no-owner",
            "--no-privileges",
            "--verbose",
            "--file", $sqlFile,
            "$SourceDatabaseUrl"
        )
    }
}

Write-Step "Verifying custom backup integrity"
Invoke-External -FilePath $pgRestore -Arguments @("--list", $dumpFile)

Write-Step "Computing SHA256 for backup files"
$hashes = @()
$hashes += Get-FileHash -Path $dumpFile -Algorithm SHA256
if (-not $SkipPlainSql) {
    $hashes += Get-FileHash -Path $sqlFile -Algorithm SHA256
}
$hashFile = Join-Path $BackupDir "attendance_backup_$timestamp.sha256.txt"
$hashes | ForEach-Object { "{0}  {1}" -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) } | Set-Content -Path $hashFile

if ($RestoreToTarget) {
    Write-Step "Checking target DB connectivity"
    Invoke-External -FilePath $psql -Arguments @("$TargetDatabaseUrl", "-c", "select now() as restore_started_at;")

    Write-Step "Resetting target public schema"
    Invoke-External -FilePath $psql -Arguments @(
        "$TargetDatabaseUrl",
        "-v", "ON_ERROR_STOP=1",
        "-c", "drop schema if exists public cascade; create schema public;"
    )

    Write-Step "Restoring backup into target DB"
    Invoke-External -FilePath $pgRestore -Arguments @(
        "--verbose",
        "--clean",
        "--if-exists",
        "--no-owner",
        "--no-privileges",
        "--dbname", "$TargetDatabaseUrl",
        "$dumpFile"
    )

    Write-Step "Running post-restore row count sanity checks"
    $tables = @("users", "attendances", "leave_requests", "notifications")
    foreach ($table in $tables) {
        Invoke-External -FilePath $psql -Arguments @(
            "$TargetDatabaseUrl",
            "-v", "ON_ERROR_STOP=1",
            "-c", "select '$table' as table_name, count(*) as row_count from $table;"
        )
    }
}

Write-Step "Done"
Write-Host "Custom backup: $dumpFile"
if (-not $SkipPlainSql) {
    Write-Host "Plain SQL backup: $sqlFile"
}
Write-Host "Checksum file: $hashFile"

if (-not $RestoreToTarget) {
    Write-Host "Backup finished. No restore was performed."
}
