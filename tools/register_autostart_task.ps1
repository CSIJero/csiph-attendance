param(
    [string]$TaskName = "AttendanceMonitoring-Autostart",
    [string]$ScriptPath = "C:\Code\csiph-attendance-main\tools\start_local_stack.ps1"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ScriptPath)) {
    throw "Missing startup script: $ScriptPath"
}

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"$ScriptPath\""

$trigger = New-ScheduledTaskTrigger -AtLogOn

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 15)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description "Start AttendanceMonitoring app and cpolar tunnel at user logon" `
    -Force | Out-Null

Write-Output "Scheduled task registered: $TaskName"
