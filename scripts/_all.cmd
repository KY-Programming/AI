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
rem pause between steps — just one pause at the very end, which --no-pause skips (for runs
rem from an existing console or another script). --no-pause is consumed here, not forwarded:
rem bump.cs switches to its interactive mode only when it gets no args at all.
setlocal

set "nopause="
set "fwd="
:parse
if "%~1"=="" goto :parsed
if /i "%~1"=="--no-pause" (set "nopause=1") else set fwd=%fwd% %1
shift
goto :parse
:parsed

echo.
echo === [bump] dotnet run bump.cs ===
dotnet run "%~dp0bump.cs" -- %fwd%
if errorlevel 1 goto :fail

echo.
echo === [pack] dotnet run pack.cs ===
dotnet run "%~dp0pack.cs" -- %fwd%
if errorlevel 1 goto :fail

echo.
echo === [publish] dotnet run publish.cs ===
dotnet run "%~dp0publish.cs" -- %fwd%
if errorlevel 1 goto :fail

echo.
echo === [tag] dotnet run tag.cs --push ===
dotnet run "%~dp0tag.cs" -- --push %fwd%
if errorlevel 1 goto :fail

echo.
echo === [release] dotnet run release.cs ===
dotnet run "%~dp0release.cs" -- %fwd%
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
if not defined nopause pause
exit /b %rc%
