[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipBundle
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$standalone = Join-Path $root 'GBFR.ExtraSigilSlots.Standalone'
$tauri = Join-Path $standalone 'src-tauri'
$nativeProject = Join-Path $root 'GBFR.ExtraSigilSlots.Native\GBFR.ExtraSigilSlots.Native.vcxproj'
$nativeOutput = Join-Path $root "GBFR.ExtraSigilSlots.Native\bin\$Configuration"
$agentResources = Join-Path $tauri 'resources\agent'

$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    $msbuild = & $vswhere `
        -latest `
        -products '*' `
        -requires Microsoft.Component.MSBuild `
        -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
}
if (-not $msbuild) {
    $msbuild = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $msbuild) {
    throw 'MSBuild was not found. Install Visual Studio 2022 Build Tools with the C++ workload.'
}

$cargo = Get-Command cargo -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $cargo) {
    $cargoFallback = Join-Path $HOME '.cargo\bin\cargo.exe'
    if (Test-Path -LiteralPath $cargoFallback) {
        $cargo = $cargoFallback
    }
}
if (-not $cargo) {
    throw 'Cargo was not found. Install the stable x86_64-pc-windows-msvc Rust toolchain.'
}
$cargoDirectory = Split-Path -Parent $cargo
if (-not (($env:PATH -split ';') -contains $cargoDirectory)) {
    $env:PATH = "$cargoDirectory;$env:PATH"
}

& $msbuild $nativeProject `
    /t:Rebuild `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /m `
    /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Native build failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $agentResources -Force | Out-Null
$resources = @(
    @{ Source = (Join-Path $nativeOutput 'GBFR.ExtraSigilSlots.Native.dll'); Name = 'GBFR.ExtraSigilSlots.Native.dll' },
    @{ Source = (Join-Path $root 'GBFR.ExtraSigilSlots.Native\GBFR-ExtraSigilSlots.compatibility.tsv'); Name = 'GBFR-ExtraSigilSlots.compatibility.tsv' },
    @{ Source = (Join-Path $root 'GBFR.ExtraSigilSlots.Native\GBFR-ExtraSigilSlots.names.en.tsv'); Name = 'GBFR-ExtraSigilSlots.names.en.tsv' },
    @{ Source = (Join-Path $root 'GBFR.ExtraSigilSlots.Native\GBFR-ExtraSigilSlots.names.zh-CN.tsv'); Name = 'GBFR-ExtraSigilSlots.names.zh-CN.tsv' }
)
foreach ($resource in $resources) {
    if (-not (Test-Path -LiteralPath $resource.Source -PathType Leaf)) {
        throw "Required Standalone resource is missing: $($resource.Source)"
    }
    Copy-Item -LiteralPath $resource.Source -Destination (Join-Path $agentResources $resource.Name) -Force
}

Push-Location $standalone
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed with exit code $LASTEXITCODE."
    }

    if (-not $SkipTests) {
        & npm test -- --run
        if ($LASTEXITCODE -ne 0) {
            throw "Frontend tests failed with exit code $LASTEXITCODE."
        }
    }

    & npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "Frontend build failed with exit code $LASTEXITCODE."
    }

    Push-Location $tauri
    try {
        & $cargo fmt --all -- --check
        if ($LASTEXITCODE -ne 0) {
            throw "cargo fmt failed with exit code $LASTEXITCODE."
        }
        if (-not $SkipTests) {
            & $cargo test --locked
            if ($LASTEXITCODE -ne 0) {
                throw "cargo test failed with exit code $LASTEXITCODE."
            }
        }
        & $cargo clippy --locked --all-targets -- -D warnings
        if ($LASTEXITCODE -ne 0) {
            throw "cargo clippy failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    if (-not $SkipTests) {
        & dotnet run `
            --project (Join-Path $root 'tests\StandaloneAgentHarness\StandaloneAgentHarness.csproj') `
            --configuration Release `
            -- $nativeOutput
        if ($LASTEXITCODE -ne 0) {
            throw "Standalone Agent smoke test failed with exit code $LASTEXITCODE."
        }
    }

    if (-not $SkipBundle) {
        & npm run tauri build
        if ($LASTEXITCODE -ne 0) {
            throw "Tauri bundle failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    Pop-Location
}

Write-Output "Standalone build complete: $standalone"
if (-not $SkipBundle) {
    Write-Output "Installer directory: $(Join-Path $tauri 'target\release\bundle\nsis')"
}
