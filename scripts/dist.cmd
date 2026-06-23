@echo off
rem Thin launcher for dist.cs - .cmd has no PowerShell execution-policy gate.
setlocal
dotnet run "%~dp0dist.cs" -- %*
set "rc=%errorlevel%"
pause
exit /b %rc%
