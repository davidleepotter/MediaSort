<#
.SYNOPSIS
  Publishes MediaSort to D:\Temp\MediaSorter (clean build, self-contained, single-file, win-x64).

.DESCRIPTION
  Defaults to self-contained + single-file: one ~150 MB MediaSort.exe that
  runs on any Windows 10/11 machine without requiring .NET to be installed.
  Pass -FrameworkDependent for a tiny ~5 MB build that requires .NET 9
  Desktop Runtime on the target machine.

.EXAMPLE
  PS> .\publish.ps1                         # self-contained, single .exe (default)
  PS> .\publish.ps1 -FrameworkDependent     # tiny .exe, requires .NET 9 installed
  PS> .\publish.ps1 -MultiFile              # folder of DLLs instead of single .exe
#>
param(
    [string]$OutputDir = 'D:\Temp\MediaSorter',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$MultiFile,
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

$selfContainedFlag = if ($FrameworkDependent) { 'false' } else { 'true' }
$singleFileFlag    = if ($MultiFile)          { 'false' } else { 'true' }

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
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
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
