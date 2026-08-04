@echo off
REM Start CSI Attendance Monitoring App

set APP_PORT=54169

echo.
echo ========================================
echo CSI Attendance Monitoring
echo ========================================
echo.
echo Starting application on localhost:%APP_PORT%
echo.
echo After the app starts, open another terminal and run:
echo   start_cpolar_tunnel.bat
echo.
echo Then visit: https://csi-attendance.cpolar.cn
echo.
echo Press Enter to continue...
pause

cd C:\Code\csiph-attendance-main

echo Building project...
call dotnet build

echo.
echo Starting application...
echo.

call dotnet run

pause
