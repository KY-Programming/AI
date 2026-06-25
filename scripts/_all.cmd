@echo off
rem _all.cmd — run the whole KY.AI release pipeline in order, stopping on the first failure.
rem
rem Stages (each is the same .cs the matching .cmd launches; dist.cs is the local-testing
rem flow and is deliberately left out):
rem
rem   1. bump     bump each project's <Version> and sync README
rem   2. pack     build the NuGet + npm artifacts from those versions
rem   3. publish  push the packed artifacts to NuGet and npm
rem   4. tag      push the branch, then create + push a git tag per project (--push)
rem   5. release  create a GitHub release per project from its pushed tag
rem
rem Any extra args are forwarded to every stage, so e.g. `_all.cmd --dry-run` previews the
rem whole pipeline without changing anything. Unlike the per-stage .cmd files there is no
rem pause between steps — just one pause at the very end.
setlocal

echo.
echo === [bump] dotnet run bump.cs ===
dotnet run "%~dp0bump.cs" -- %*
if errorlevel 1 goto :fail

echo.
echo === [pack] dotnet run pack.cs ===
dotnet run "%~dp0pack.cs" -- %*
if errorlevel 1 goto :fail

echo.
echo === [publish] dotnet run publish.cs ===
dotnet run "%~dp0publish.cs" -- %*
if errorlevel 1 goto :fail

echo.
echo === [tag] dotnet run tag.cs --push ===
dotnet run "%~dp0tag.cs" -- --push %*
if errorlevel 1 goto :fail

echo.
echo === [release] dotnet run release.cs ===
dotnet run "%~dp0release.cs" -- %*
if errorlevel 1 goto :fail

echo.
echo === All stages completed successfully. ===
set "rc=0"
goto :done

:fail
set "rc=%errorlevel%"
echo.
echo === Pipeline ABORTED (exit %rc%). ===

:done
pause
exit /b %rc%
