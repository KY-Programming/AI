@echo off
rem Thin launcher for publish.cs - .cmd has no PowerShell execution-policy gate.
setlocal
dotnet run "%~dp0publish.cs" -- %*
set "rc=%errorlevel%"
pause
exit /b %rc%
