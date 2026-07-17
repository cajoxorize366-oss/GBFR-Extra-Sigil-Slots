$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$configuration = 'Release'
$nativeProject = Join-Path $root 'GBFR.ExtraSigilSlots20.Native\GBFR.ExtraSigilSlots20.Native.vcxproj'
$managedProject = Join-Path $root 'GBFR.ExtraSigilSlots20.Reloaded\GBFR.ExtraSigilSlots20.Reloaded.csproj'
$managedOutput = Join-Path $root 'GBFR.ExtraSigilSlots20.Reloaded\bin\Release'
$distRoot = Join-Path $root 'dist'
$packageDir = Join-Path $distRoot 'GBFR.ExtraSigilSlots20.Reloaded'
$zipPath = Join-Path $distRoot 'GBFR-Extra-Sigil-Slots-0.3.0-test7-direct-battle-hook.zip'

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
    $fallbacks = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
    )
    $msbuild = $fallbacks | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $msbuild) {
    throw 'MSBuild was not found. Install Visual Studio 2022 Build Tools with the C++ workload.'
}

& $msbuild $nativeProject `
    /t:Rebuild `
    /p:Configuration=$configuration `
    /p:Platform=x64 `
    /m `
    /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Native build failed with exit code $LASTEXITCODE."
}

& dotnet build $managedProject -c $configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Managed build failed with exit code $LASTEXITCODE."
}

$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
$resolvedDist = [IO.Path]::GetFullPath($distRoot).TrimEnd('\') + '\'
if (-not $resolvedDist.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a dist path outside the repository: $distRoot"
}

if (Test-Path -LiteralPath $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir | Out-Null
Copy-Item -Path (Join-Path $managedOutput '*') -Destination $packageDir -Recurse -Force

foreach ($excludedFile in @(
    'GBFR.ExtraSigilSlots20.Reloaded.pdb',
    'GBFR-ExtraSigilSlots20.ini',
    'README-development.md'
)) {
    $excludedPath = Join-Path $packageDir $excludedFile
    if (Test-Path -LiteralPath $excludedPath) {
        Remove-Item -LiteralPath $excludedPath -Force
    }
}

$runtimesPath = Join-Path $packageDir 'runtimes'
if (Test-Path -LiteralPath $runtimesPath) {
    Get-ChildItem -LiteralPath $runtimesPath -Directory |
        Where-Object { $_.Name -ne 'win-x64' } |
        Remove-Item -Recurse -Force
}

Compress-Archive -LiteralPath $packageDir -DestinationPath $zipPath -CompressionLevel Optimal

Write-Output "Reloaded-II package: $packageDir"
Write-Output "ZIP: $zipPath"
