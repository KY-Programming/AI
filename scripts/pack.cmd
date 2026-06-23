@echo off
rem Thin launcher for pack.cs - .cmd has no PowerShell execution-policy gate.
setlocal
dotnet run "%~dp0pack.cs" -- %*
set "rc=%errorlevel%"
pause
exit /b %rc%
