@echo off
setlocal

pushd "%~dp0" >nul

dotnet run --project ".\src\Jesnote.csproj" -- %*
set "EXIT_CODE=%ERRORLEVEL%"

popd >nul
exit /b %EXIT_CODE%
