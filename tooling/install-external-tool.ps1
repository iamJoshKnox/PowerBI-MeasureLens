<#
.SYNOPSIS
    Registers PBI Measure Lens in Power BI Desktop's External Tools ribbon.

.DESCRIPTION
    Writes a .pbitool.json registration file (pointing at the published .exe) into
    the shared External Tools folder. Must be run elevated (admin), because that
    folder lives under Program Files.

.PARAMETER ExePath
    Full path to the published PbiMeasureLens.exe. Defaults to the repo's
    publish\PbiMeasureLens.exe (produced by `dotnet publish`).

.EXAMPLE
    # From an elevated PowerShell:
    .\install-external-tool.ps1
    .\install-external-tool.ps1 -ExePath "C:\Tools\PbiMeasureLens\PbiMeasureLens.exe"
#>
[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot "..\publish\PbiMeasureLens.exe")
)

$ErrorActionPreference = "Stop"

# Require admin
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(`
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be run as Administrator (the External Tools folder is under Program Files)."
    exit 1
}

$ExePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path $ExePath)) {
    Write-Error "Executable not found: $ExePath`nBuild it first:  dotnet publish src\PbiMeasureLens -c Release -o publish"
    exit 1
}

$toolsDir = "C:\Program Files (x86)\Common Files\Microsoft Shared\Power BI Desktop\External Tools"
if (-not (Test-Path $toolsDir)) {
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
}

$json = [ordered]@{
    version     = "0.1.0"
    name        = "PBI Measure Lens"
    description = "Show original-vs-display field names for a report's visuals, and a measure's recursive DAX dependency tree."
    path        = $ExePath
    arguments   = ""
} | ConvertTo-Json

$target = Join-Path $toolsDir "PbiMeasureLens.pbitool.json"
# Write UTF-8 without BOM (Power BI's external-tool loader is picky about a leading BOM).
[System.IO.File]::WriteAllText($target, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Registered PBI Measure Lens ->" $target
Write-Host "Restart Power BI Desktop; the button appears under the External Tools ribbon."
