@echo off
setlocal EnableExtensions

if "%~1"=="" (
  echo Usage: %~nx0 VERSION
  echo Example: %~nx0 0.1.1
  exit /b 1
)

set "VERSION=%~1"
set "TAG=v%VERSION%"

pushd "%~dp0" >nul

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
  echo This script must be run from inside the Jesnote git repository.
  popd >nul
  exit /b 1
)

for /f "delims=" %%B in ('git branch --show-current') do set "BRANCH=%%B"
if "%BRANCH%"=="" (
  echo Cannot release from a detached HEAD.
  popd >nul
  exit /b 1
)

git rev-parse -q --verify "refs/tags/%TAG%" >nul 2>nul
if not errorlevel 1 (
  echo Tag %TAG% already exists locally.
  popd >nul
  exit /b 1
)

echo Updating src\Jesnote.csproj to %VERSION%...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$version=$env:VERSION;" ^
  "if ($version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') { throw 'Version must look like 0.1.1' }" ^
  "$path='src\Jesnote.csproj';" ^
  "$content=[IO.File]::ReadAllText($path,[Text.Encoding]::UTF8);" ^
  "if ($content -notmatch '<Version>[^<]+</Version>') { throw 'Could not find <Version> in src\Jesnote.csproj' }" ^
  "$content=[regex]::Replace($content,'<Version>[^<]+</Version>','<Version>'+$version+'</Version>',1);" ^
  "[IO.File]::WriteAllText($path,$content,(New-Object Text.UTF8Encoding($false)))"
if errorlevel 1 goto fail

echo Building release configuration...
dotnet build ".\src\Jesnote.csproj" -c Release
if errorlevel 1 goto fail

echo Committing release changes...
git add -A
git diff --cached --quiet
if not errorlevel 1 (
  echo No changes to commit after updating the version.
  popd >nul
  exit /b 1
)

git commit -m "Release %TAG%"
if errorlevel 1 goto fail

git tag "%TAG%"
if errorlevel 1 goto fail

echo Release %TAG% is ready locally.
echo Push it with: git push origin %BRANCH% %TAG%
echo GitHub Actions will build and publish the release assets after the tag is pushed.
popd >nul
exit /b 0

:fail
echo Release failed.
popd >nul
exit /b 1
