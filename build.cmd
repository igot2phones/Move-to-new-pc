@echo off
REM Windows build. Prefers a modern MSBuild (VS 2017+) with the net40 reference
REM assemblies package; falls back to the in-box .NET Framework 4.0 MSBuild, which is
REM present on every machine that can run this app and needs no package at all.
setlocal enabledelayedexpansion
set ROOT=%~dp0
set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

set MSBUILD=
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
if not exist "%VSWHERE%" set VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe
if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSBUILD=%%i
)

if defined MSBUILD (
  echo Using modern MSBuild: !MSBUILD!
  set REFS=%ROOT%build\refs
  if not exist "!REFS!\mscorlib.dll" (
    echo Fetching net40 reference assemblies...
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ErrorActionPreference='Stop';" ^
      "$u='https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net40/1.0.3';" ^
      "$z=Join-Path $env:TEMP 'net40ref.zip';" ^
      "(New-Object Net.WebClient).DownloadFile($u,$z);" ^
      "$t=Join-Path $env:TEMP 'net40ref';" ^
      "if(Test-Path $t){Remove-Item -Recurse -Force $t};" ^
      "Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
      "[IO.Compression.ZipFile]::ExtractToDirectory($z,$t);" ^
      "New-Item -ItemType Directory -Force -Path '%ROOT%build\refs' | Out-Null;" ^
      "Copy-Item (Join-Path $t 'build\.NETFramework\v4.0\*.dll') '%ROOT%build\refs' -Force"
    if errorlevel 1 goto :fail
  )
  "!MSBUILD!" "%ROOT%MoveToNewPC.sln" /p:Configuration=%CONFIG% /p:Platform="Any CPU" ^
      /p:FrameworkPathOverride="%ROOT%build\refs" /nologo /v:minimal
  if errorlevel 1 goto :fail
) else (
  set MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe
  if not exist "!MSBUILD!" goto :nomsbuild
  echo Using in-box .NET 4.0 MSBuild: !MSBUILD!
  "!MSBUILD!" "%ROOT%MoveToNewPC.sln" /p:Configuration=%CONFIG% /nologo /v:minimal
  if errorlevel 1 goto :fail
)

echo.
echo Build OK -^> %ROOT%build
dir /b "%ROOT%build\*.exe"
exit /b 0

:nomsbuild
echo ERROR: No MSBuild found. Install .NET Framework 4.0+ or Visual Studio.
exit /b 1
:fail
echo BUILD FAILED
exit /b 1
