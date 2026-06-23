@echo off
rem Thin launcher for bump.cs - .cmd has no PowerShell execution-policy gate.
setlocal
dotnet run "%~dp0bump.cs" -- %*
set "rc=%errorlevel%"
pause
exit /b %rc%
