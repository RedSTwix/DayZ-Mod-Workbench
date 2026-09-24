@echo off
setlocal
where dotnet >nul 2>nul
if errorlevel 1 (
  echo SDK do .NET nao encontrado no PATH.
  pause
  exit /b 1
)
dotnet build "%~dp0src\DayZModWorkbench.csproj" --configuration Release --nologo
if errorlevel 1 (
  pause
  exit /b 1
)
copy /y "%~dp0bin\DayZModWorkbench.exe" "%~dp0DayZModWorkbench.exe" >nul
if exist "%~dp0bin\DayZModWorkbench.exe.config" copy /y "%~dp0bin\DayZModWorkbench.exe.config" "%~dp0DayZModWorkbench.exe.config" >nul
echo.
echo Pronto: %~dp0DayZModWorkbench.exe
pause
