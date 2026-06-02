<#
.SYNOPSIS
    Produces both distribution builds of PBI Measure Lens.

.DESCRIPTION
    publish\     - self-contained, compressed single file (~68 MB). Runs anywhere; no
                   prerequisites. Best for sharing / copying to VMs.
    publish-fd\  - framework-dependent single file (~0.3 MB). Requires the .NET 8 Desktop
                   Runtime on the target machine. Best for known-good / standardized machines.
#>
[CmdletBinding()]
param([string]$Configuration = "Release")

$proj = Join-Path $PSScriptRoot "..\src\PbiMeasureLens\PbiMeasureLens.csproj"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

function Publish-Variant {
    param([string[]]$Args, [string]$Label)
    Write-Host "`n=== Publishing $Label ===" -ForegroundColor Cyan
    & dotnet @Args
    if ($LASTEXITCODE -ne 0) { throw "$Label publish failed (exit $LASTEXITCODE)" }
}

Publish-Variant -Label "self-contained (publish\)" -Args @(
    "publish", $proj, "-c", $Configuration, "-r", "win-x64",
    "--self-contained", "true", "-p:PublishSingleFile=true",
    "-o", (Join-Path $root "publish")
)

Publish-Variant -Label "framework-dependent (publish-fd\)" -Args @(
    "publish", $proj, "-c", $Configuration, "-r", "win-x64",
    "--self-contained", "false", "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=false", "-o", (Join-Path $root "publish-fd")
)

Write-Host "`nDone:" -ForegroundColor Green
Get-ChildItem (Join-Path $root "publish\PbiMeasureLens.exe"), (Join-Path $root "publish-fd\PbiMeasureLens.exe") |
    Select-Object FullName, @{N='MB';E={[math]::Round($_.Length/1MB,2)}}
