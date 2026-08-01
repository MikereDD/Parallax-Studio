@echo off
setlocal
set "ROOT=%~dp0"
where pwsh.exe >nul 2>nul
if %errorlevel%==0 (
  pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build.ps1" %*
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build.ps1" %*
)
exit /b %errorlevel%
