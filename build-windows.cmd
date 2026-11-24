@echo off
setlocal
set "PROJECT=%~dp0src\IphoneArchive\IphoneArchive.csproj"
set "OUTPUT=%~dp0publish\win-x64"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK was not found.
  echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this file again.
  exit /b 1
)

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%OUTPUT%" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 exit /b %errorlevel%

echo.
echo Built: %OUTPUT%\IphoneArchive.exe
endlocal

