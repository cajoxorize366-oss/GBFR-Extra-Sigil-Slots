[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$nativeOutput = Join-Path $root "GBFR.ExtraSigilSlots20.Native\bin\$Configuration"
$managedOutput = Join-Path $root "GBFR.ExtraSigilSlots20.Reloaded\bin\$Configuration"
$nativeDll = Join-Path $nativeOutput 'GBFR.ExtraSigilSlots20.Native.dll'
$managedDll = Join-Path $managedOutput 'GBFR.ExtraSigilSlots20.Reloaded.dll'

if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
    throw "Build the native $Configuration configuration first: $nativeDll"
}
if (-not (Test-Path -LiteralPath $managedDll -PathType Leaf)) {
    throw "Build the managed $Configuration configuration first: $managedDll"
}

function Invoke-Harness {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$OutputDirectory
    )
    & dotnet run --project $Project --configuration Release -- $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke harness failed with exit code $LASTEXITCODE`: $Project"
    }
}

Invoke-Harness `
    -Project (Join-Path $PSScriptRoot 'SlotConfigHarness\SlotConfigHarness.csproj') `
    -OutputDirectory $nativeOutput
Invoke-Harness `
    -Project (Join-Path $PSScriptRoot 'PresetStoreHarness\PresetStoreHarness.csproj') `
    -OutputDirectory $managedOutput
Invoke-Harness `
    -Project (Join-Path $PSScriptRoot 'InputPassThroughHarness\InputPassThroughHarness.csproj') `
    -OutputDirectory $managedOutput

Write-Output 'ALL_SMOKE_TESTS=PASS'
