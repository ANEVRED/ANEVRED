$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectFile = Join-Path $projectRoot "ZestResourceOptimizer.csproj"
$distRoot = Join-Path $projectRoot "dist"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dist = Join-Path $distRoot "ANEVRED-allinone-$stamp"
$sourceModel = Join-Path $projectRoot "Models\Translation\en-ru"
$targetModel = Join-Path $dist "Models\Translation\en-ru"

if (-not (Test-Path $projectFile)) {
    throw "Project file not found: $projectFile"
}

if (-not (Test-Path (Join-Path $sourceModel "translator.cmd"))) {
    throw "Translation model files are missing: $sourceModel"
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Write-Host "Building ANEVRED..."
& dotnet build $projectFile -c Release -o $dist --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

Write-Host "Copying local translation runtime..."
New-Item -ItemType Directory -Force -Path $targetModel | Out-Null

$files = @(
    "translator.cmd",
    "translator.mjs",
    "download-model.mjs",
    "package.json",
    "package-lock.json",
    "README.md"
)

foreach ($file in $files) {
    $source = Join-Path $sourceModel $file
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination $targetModel -Force
    }
}

foreach ($directory in @("node", "cache", "node_modules")) {
    $source = Join-Path $sourceModel $directory
    $target = Join-Path $targetModel $directory
    if (-not (Test-Path $source)) {
        throw "Required translation directory is missing: $source"
    }

    robocopy $source $target /E /NFL /NDL /NJH /NJS /NP /XD ".git" ".cache" "test" "tests" "docs" "examples" "benchmark" "benchmarks" "darwin" "linux" "arm64" /XF "*.md" "*.map" | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed while copying $directory with exit code $LASTEXITCODE"
    }
}

$translator = Join-Path $targetModel "translator.cmd"
$node = Join-Path $targetModel "node\node.exe"
if (-not (Test-Path $translator) -or -not (Test-Path $node)) {
    throw "All-in-one translator was not copied correctly."
}

Write-Host "Done. Start this file:"
Write-Host (Join-Path $dist "ANEVRED.exe")
