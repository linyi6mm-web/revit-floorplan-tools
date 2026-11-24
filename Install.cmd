@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Addin.ps1"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Installation failed. Review the message above.
pause
exit /b %RESULT%
