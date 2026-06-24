@echo off
rem Thin launcher for release.cs - creates GitHub releases from the version tags (needs the gh CLI).
setlocal
dotnet run "%~dp0release.cs" -- %*
set "rc=%errorlevel%"
pause
exit /b %rc%
