#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Helper script to prepare this ASP.NET Core app for Cpolar deployment.

.DESCRIPTION
    This script performs pre-deployment checks and outputs environment variable
    templates for easy copy-paste into the Cpolar dashboard.

.EXAMPLE
    .\prepare_cpolar.ps1
#>

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Cpolar Deployment Preparation" -ForegroundColor Cyan
Write-Host "Attendance Monitoring System" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Check for required files
Write-Host "[Step 1] Checking for required files..." -ForegroundColor Yellow
$required_files = @(
    "AttendanceMonitoring.csproj",
    "Dockerfile",
    "Program.cs",
    ".env.cpolar"
)

$missing = $false
foreach ($file in $required_files) {
    if (Test-Path $file) {
        Write-Host "  ✓ $file" -ForegroundColor Green
    } else {
        Write-Host "  ✗ MISSING: $file" -ForegroundColor Red
        $missing = $true
    }
}

if ($missing) {
    Write-Host ""
    Write-Host "ERROR: Some required files are missing." -ForegroundColor Red
    Write-Host "Make sure you're in the project root directory." -ForegroundColor Red
    exit 1
}

Write-Host ""

# 2. Check if repository is initialized
Write-Host "[Step 2] Checking Git configuration..." -ForegroundColor Yellow
if (Test-Path ".git") {
    Write-Host "  ✓ Git repository initialized" -ForegroundColor Green
    
    $origin = git config --get remote.origin.url 2>$null
    if ($origin) {
        Write-Host "  ✓ Remote origin: $origin" -ForegroundColor Green
    } else {
        Write-Host "  ✗ No remote origin configured" -ForegroundColor Red
        Write-Host ""
        Write-Host "    To add origin:" -ForegroundColor Yellow
        Write-Host "    git remote add origin https://github.com/YOUR_USERNAME/csiph-attendance.git" -ForegroundColor Cyan
    }
} else {
    Write-Host "  ⚠ Git repository NOT initialized" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "    To initialize:" -ForegroundColor Yellow
    Write-Host "    git init" -ForegroundColor Cyan
    Write-Host "    git add -A" -ForegroundColor Cyan
    Write-Host "    git commit -m 'Initial commit'" -ForegroundColor Cyan
    Write-Host "    git remote add origin https://github.com/YOUR_USERNAME/csiph-attendance.git" -ForegroundColor Cyan
    Write-Host "    git push -u origin main" -ForegroundColor Cyan
}

Write-Host ""

# 3. Check Docker
Write-Host "[Step 3] Checking Docker installation..." -ForegroundColor Yellow
try {
    $dockerVersion = docker --version 2>$null
    if ($dockerVersion) {
        Write-Host "  ✓ Docker installed: $dockerVersion" -ForegroundColor Green
    }
} catch {
    Write-Host "  ⚠ Docker not found (optional for testing, required for Cpolar build)" -ForegroundColor Yellow
}

Write-Host ""

# 4. Check .NET SDK
Write-Host "[Step 4] Checking .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = dotnet --version 2>$null
    if ($dotnetVersion) {
        Write-Host "  ✓ .NET SDK installed: $dotnetVersion" -ForegroundColor Green
    }
} catch {
    Write-Host "  ✗ .NET SDK not found" -ForegroundColor Red
}

Write-Host ""

# 5. Summary of what to do next
Write-Host "[Step 5] Next steps for Cpolar deployment:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  1. If not already done, push your code to GitHub:" -ForegroundColor Cyan
Write-Host "     git push -u origin main" -ForegroundColor White
Write-Host ""
Write-Host "  2. Go to https://cpolar.com" -ForegroundColor Cyan
Write-Host ""
Write-Host "  3. Sign up and connect your GitHub account" -ForegroundColor Cyan
Write-Host ""
Write-Host "  4. Create a new Docker deployment:" -ForegroundColor Cyan
Write-Host "     - Repository: YOUR_USERNAME/csiph-attendance" -ForegroundColor White
Write-Host "     - Branch: main" -ForegroundColor White
Write-Host "     - Dockerfile: ./Dockerfile" -ForegroundColor White
Write-Host "     - Port: 10000" -ForegroundColor White
Write-Host ""
Write-Host "  5. Set environment variables in Cpolar dashboard" -ForegroundColor Cyan
Write-Host "     (see .env.cpolar file for template)" -ForegroundColor White
Write-Host ""
Write-Host "  6. Deploy and wait for build (~5 minutes)" -ForegroundColor Cyan
Write-Host ""

# 6. Display environment variables template
Write-Host "[Step 6] Environment variables template:" -ForegroundColor Yellow
Write-Host ""
Write-Host "Copy and paste these into your Cpolar deployment:" -ForegroundColor Cyan
Write-Host ""

$env_vars = @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "ASPNETCORE_URLS=http://0.0.0.0:10000",
    "ConnectionStrings__DefaultConnection=postgres://user:password@host:5432/database",
    "Email__Provider=Brevo",
    "Email__Enabled=true",
    "Email__FromAddress=your-email@example.com",
    "Email__FromName=Attendance Monitor",
    "Email__Username=xkeysib-your-brevo-api-key",
    "Email__Password=xkeysib-your-brevo-api-key",
    "AttendanceMonitoring__RequireSecureCookie=true",
    "AttendanceMonitoring__OnlineThresholdSeconds=600",
    "AttendanceMonitoring__SessionLifetimeHours=12"
)

foreach ($var in $env_vars) {
    Write-Host "  $var" -ForegroundColor White
}

Write-Host ""

# 7. Important reminders
Write-Host "[Step 7] Important reminders:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  ⚠ Default credentials (CHANGE IMMEDIATELY after first login):" -ForegroundColor Red
Write-Host "    Username: admin" -ForegroundColor White
Write-Host "    Password: Admin123!" -ForegroundColor White
Write-Host ""
Write-Host "  ⚠ For Brevo API key:" -ForegroundColor Yellow
Write-Host "    - Get from https://app.brevo.com/settings/keys/api" -ForegroundColor White
Write-Host "    - Use the long 'xkeysib-...' key (NOT the SMTP password)" -ForegroundColor White
Write-Host ""
Write-Host "  ⚠ Database selection:" -ForegroundColor Yellow
Write-Host "    - SQLite: Simple, for testing only" -ForegroundColor White
Write-Host "    - PostgreSQL: For production (recommended)" -ForegroundColor White
Write-Host ""

# 8. Helpful documentation links
Write-Host "[Step 8] Helpful documentation:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Full deployment guide:    CPOLAR_DEPLOY.md" -ForegroundColor Cyan
Write-Host "  Quick reference:          CPOLAR_QUICK_REF.md" -ForegroundColor Cyan
Write-Host "  Environment template:     .env.cpolar" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Cpolar Docs:              https://cpolar.com/docs" -ForegroundColor Cyan
Write-Host "  Brevo API Docs:           https://developers.brevo.com" -ForegroundColor Cyan
Write-Host "  ASP.NET Core Docs:        https://learn.microsoft.com/en-us/aspnet/core" -ForegroundColor Cyan
Write-Host ""

Write-Host "========================================" -ForegroundColor Green
Write-Host "Ready for Cpolar deployment! 🚀" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
