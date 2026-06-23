@echo off
rem Thin launcher for publish.cs, npm only - .cmd has no PowerShell execution-policy gate.
setlocal
dotnet run "%~dp0publish.cs" -- --skip-nuget %*
set "rc=%errorlevel%"
pause
exit /b %rc%
