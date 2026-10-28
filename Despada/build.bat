@echo off
setlocal
cd /d "%~dp0"

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

echo Building Despada (%CONFIG%)...
dotnet build "Despada.csproj" -c %CONFIG%
if errorlevel 1 (
    echo.
    echo Build FAILED.
    exit /b 1
)

echo.
echo Build succeeded.
endlocal
