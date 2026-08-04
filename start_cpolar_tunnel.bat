@echo off
REM Cpolar Tunnel Starter for CSI Attendance

set APP_PORT=54169
set CPOLAR_SUBDOMAIN=csi-attendance
set CPOLAR_REGION=cn

set CPOLAR_EXE=C:\Program Files\cpolar\cpolar.exe
if not exist "%CPOLAR_EXE%" set CPOLAR_EXE=C:\cpolar\cpolar.exe

if not exist "%CPOLAR_EXE%" (
	echo.
	echo ERROR: cpolar.exe not found.
	echo Checked:
	echo   C:\Program Files\cpolar\cpolar.exe
	echo   C:\cpolar\cpolar.exe
	echo.
	pause
	exit /b 1
)

echo.
echo ========================================
echo Cpolar Tunnel - CSI Attendance
echo ========================================
echo.
echo This will start your Cpolar tunnel on:
echo https://%CPOLAR_SUBDOMAIN%.cpolar.cn
echo.
echo Make sure your app is already running on localhost:%APP_PORT%
echo (Start it in another window with: dotnet run)
echo.
echo Press Enter to start the tunnel...
pause

"%CPOLAR_EXE%" http %APP_PORT% -region %CPOLAR_REGION% -subdomain %CPOLAR_SUBDOMAIN% -daemon on

echo.
echo Tunnel started. Test URL:
echo https://%CPOLAR_SUBDOMAIN%.cpolar.cn

pause
