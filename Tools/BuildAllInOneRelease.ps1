$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectFile = Join-Path $projectRoot "ANEVRED.csproj"
$distRoot = Join-Path $projectRoot "dist"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dist = Join-Path $distRoot "ANEVRED-allinone-$stamp"

if (-not (Test-Path $projectFile)) {
    throw "Project file not found: $projectFile"
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Write-Host "Building ANEVRED..."
& dotnet build $projectFile -c Release -o $dist
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

Get-ChildItem -LiteralPath $dist -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

Write-Host "Skipping bundled translation models. Live translation uses Chrome."

Write-Host "Done. Start this file:"
Write-Host (Join-Path $dist "ANEVRED.exe")
