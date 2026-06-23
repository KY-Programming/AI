@echo off
rem Thin launcher for publish.cs, NuGet only - .cmd has no PowerShell execution-policy gate.
setlocal
dotnet run "%~dp0publish.cs" -- --skip-npm %*
set "rc=%errorlevel%"
pause
exit /b %rc%
