@echo off
setlocal
cd /d "%~dp0"
dotnet publish Timeline.csproj -c Release -r win-x64 --self-contained false -o AppSafe
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Timeline is ready. Double-click Start Timeline.cmd.
pause
endlocal
