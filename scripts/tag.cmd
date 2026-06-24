@echo off
rem Thin launcher for tag.cs - always pushes (--push). Pass --dry-run to preview, --force to move tags.
setlocal
dotnet run "%~dp0tag.cs" -- --push %*
set "rc=%errorlevel%"
pause
exit /b %rc%
