<#
.SYNOPSIS
  Publishes MediaSort to D:\Temp\MediaSorter (clean build, framework-dependent, win-x64).

.DESCRIPTION
  - Runs `dotnet publish` using the FolderProfile publish profile.
  - Cleans the destination directory first so old files don't linger.
  - Run from anywhere; the script locates the .csproj relative to itself.

.EXAMPLE
  PS> .\publish.ps1
  PS> .\publish.ps1 -SelfContained        # bundle .NET runtime
  PS> .\publish.ps1 -SingleFile           # produce a single .exe
#>
param(
    [string]$OutputDir = 'D:\Temp\MediaSorter',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [switch]$SingleFile,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $scriptDir 'MediaSort\MediaSort.csproj'

if (-not (Test-Path $proj)) {
    throw "Project not found: $proj"
}

# Ensure / clean output directory
if (Test-Path $OutputDir) {
    if (-not $NoClean) {
        Write-Host "Cleaning $OutputDir ..." -ForegroundColor Yellow
        Get-ChildItem -Path $OutputDir -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$selfContainedFlag = if ($SelfContained) { 'true' } else { 'false' }
$singleFileFlag    = if ($SingleFile)    { 'true' } else { 'false' }

Write-Host "Publishing MediaSort -> $OutputDir" -ForegroundColor Cyan
Write-Host "  Configuration : $Configuration"
Write-Host "  Runtime       : $Runtime"
Write-Host "  SelfContained : $selfContainedFlag"
Write-Host "  SingleFile    : $singleFileFlag"
Write-Host ""

& dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedFlag `
    -p:PublishSingleFile=$singleFileFlag `
    -p:PublishDir="$OutputDir\" `
    -p:PublishProtocol=FileSystem

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Published successfully to $OutputDir" -ForegroundColor Green
$exe = Join-Path $OutputDir 'MediaSort.exe'
if (Test-Path $exe) {
    Write-Host "Run it: $exe" -ForegroundColor Green
}
