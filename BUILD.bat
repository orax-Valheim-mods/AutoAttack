@echo off

REM Get current folder name.
for %%I in (.) do set "projectName=%%~nxI"

REM Build project using the current folder name.
echo on
dotnet build "%projectName%.sln" -c Release -v diag
if %errorlevel% neq 0 exit /b %errorlevel%
