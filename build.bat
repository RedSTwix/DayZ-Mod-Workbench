@echo off
setlocal
set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
  echo MSBuild nao encontrado.
  pause
  exit /b 1
)
"%MSBUILD%" "%~dp0src\DayZModWorkbench.csproj" /t:Rebuild /p:Configuration=Release /nologo
if errorlevel 1 (
  pause
  exit /b 1
)
copy /y "%~dp0bin\DayZModWorkbench.exe" "%~dp0DayZModWorkbench.exe" >nul
echo.
echo Pronto: %~dp0DayZModWorkbench.exe
pause
