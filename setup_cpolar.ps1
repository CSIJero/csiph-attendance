#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Setup script for Cpolar tunneling with CSI Attendance Monitoring app

.DESCRIPTION
    This script will:
    1. Download Cpolar
    2. Configure it with your auth token
    3. Create a tunnel with custom domain
    4. Start your ASP.NET app locally

.EXAMPLE
    .\setup_cpolar.ps1
#>

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Cpolar Setup for CSI Attendance" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$cpolarFolder = "C:\cpolar"
$authToken = "NjhmMjQyYTMtOTMyMy00Y2NjLTgzYzktMmQ4NDk4ZTgyYmVl"
$domain = "csi-attendance"
$appPort = 54169

# Step 1: Create folder
Write-Host "[Step 1] Creating Cpolar folder..." -ForegroundColor Yellow
if (-not (Test-Path $cpolarFolder)) {
    New-Item -ItemType Directory -Path $cpolarFolder -Force | Out-Null
}
Write-Host "  ✓ Folder ready: $cpolarFolder" -ForegroundColor Green
Write-Host ""

# Step 2: Manual download instructions
Write-Host "[Step 2] Download Cpolar" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Since automatic download isn't available, please download manually:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  1. Go to: https://dashboard.cpolar.com/download" -ForegroundColor White
Write-Host "  2. Download: 'cpolar-v3-windows-amd64.zip'" -ForegroundColor White
Write-Host "  3. Extract to: $cpolarFolder" -ForegroundColor White
Write-Host ""

# Wait for user to download
Write-Host "  Press Enter once you've downloaded and extracted cpolar..." -ForegroundColor Yellow
$null = Read-Host

# Check if cpolar.exe exists
if (-not (Test-Path "$cpolarFolder\cpolar.exe")) {
    Write-Host "  ERROR: cpolar.exe not found at $cpolarFolder" -ForegroundColor Red
    Write-Host "  Please download and extract cpolar, then run this script again." -ForegroundColor Red
    exit 1
}
Write-Host "  DONE: cpolar.exe found!" -ForegroundColor Green

Write-Host ""

# Step 3: Authenticate
Write-Host "[Step 3] Authenticating with Cpolar..." -ForegroundColor Yellow
Write-Host "  Running: cpolar authtoken" -ForegroundColor Cyan
Write-Host ""

cd $cpolarFolder
& ".\cpolar.exe" authtoken $authToken

Write-Host ""
Write-Host "  ✓ Authentication complete!" -ForegroundColor Green
Write-Host ""

# Step 4: Show next steps
Write-Host "[Step 4] Next Steps - Run these in separate PowerShell windows:" -ForegroundColor Yellow
Write-Host ""

Write-Host "  WINDOW 1 - Start your ASP.NET app:" -ForegroundColor Cyan
Write-Host "    cd C:\Code\csiph-attendance-main" -ForegroundColor White
Write-Host "    dotnet run" -ForegroundColor White
Write-Host ""

Write-Host "  WINDOW 2 - Start Cpolar tunnel:" -ForegroundColor Cyan
Write-Host "    cd $cpolarFolder" -ForegroundColor White
Write-Host "    .\cpolar.exe http -subdomain=$domain $appPort" -ForegroundColor White
Write-Host ""

Write-Host "  Your app will be available at: https://$domain.cpolar.io" -ForegroundColor Green
Write-Host ""

# Step 5: Option to start app now
Write-Host "[Step 5] Start your application?" -ForegroundColor Yellow
$start = Read-Host "Start 'dotnet run' now? (y/n)"

if ($start -eq "y" -or $start -eq "Y") {
    Write-Host ""
    Write-Host "Starting your ASP.NET application..." -ForegroundColor Cyan
    Write-Host ""
    cd C:\Code\csiph-attendance-main
    
    Write-Host "Building project..." -ForegroundColor Yellow
    dotnet build
    
    Write-Host ""
    Write-Host "Starting app on http://localhost:$appPort" -ForegroundColor Green
    Write-Host ""
    Write-Host "⚠  IMPORTANT:" -ForegroundColor Yellow
    Write-Host "   1. Keep this window open while the app is running" -ForegroundColor White
    Write-Host "   2. Open another PowerShell window to start the Cpolar tunnel" -ForegroundColor White
    Write-Host "   3. In the new window, run:" -ForegroundColor White
    Write-Host "      cd $cpolarFolder" -ForegroundColor Cyan
    Write-Host "      .\cpolar.exe http -subdomain=$domain $appPort" -ForegroundColor Cyan
    Write-Host ""
    
    dotnet run
}

Write-Host ""
Write-Host "Setup complete!" -ForegroundColor Green
Write-Host "Your app is ready for tunneling with Cpolar" -ForegroundColor Green
