param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$modelDir = Join-Path $ProjectRoot "Models\Translation\en-ru"
$directMlVersion = "1.14.1"
$directMlCoreVersion = "1.15.4"
if (-not (Test-Path $modelDir)) {
    throw "Model folder not found: $modelDir"
}

$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $node) {
    throw "Node.js is required once to install the local translator. Install Node.js, then run this script again."
}

$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if ($null -eq $npm) {
    $npm = Get-Command npm -ErrorAction SilentlyContinue
}

if ($null -eq $npm) {
    throw "npm is required once to install the local translator runtime. Install Node.js LTS from https://nodejs.org/ so npm is available, then run this script again."
}

$portableNodeDir = Join-Path $modelDir "node"
New-Item -ItemType Directory -Force -Path $portableNodeDir | Out-Null
Copy-Item -LiteralPath $node.Source -Destination (Join-Path $portableNodeDir "node.exe") -Force

Push-Location $modelDir
try {
    if (-not (Test-Path "node_modules")) {
        Write-Host "Installing local translator runtime..."
        & $npm.Source install --omit=dev
    }
    else {
        Write-Host "Updating local translator runtime..."
        & $npm.Source install --omit=dev
    }

    Write-Host "Downloading EN-RU model into local cache..."
    & $node.Source .\download-model.mjs

    Write-Host "Installing DirectML runtime..."
    $downloadDir = Join-Path $ProjectRoot ".downloads\directml"
    $extractDir = Join-Path $downloadDir "onnxruntime-directml-$directMlVersion"
    $packagePath = Join-Path $downloadDir "microsoft.ml.onnxruntime.directml.$directMlVersion.nupkg"
    $directMlCorePackagePath = Join-Path $downloadDir "microsoft.ai.directml.$directMlCoreVersion.nupkg"
    $directMlCoreExtractDir = Join-Path $downloadDir "microsoft-ai-directml-$directMlCoreVersion"
    New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
    if (-not (Test-Path $packagePath)) {
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.DirectML/$directMlVersion" -OutFile $packagePath
    }

    if (-not (Test-Path $directMlCorePackagePath)) {
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.AI.DirectML/$directMlCoreVersion" -OutFile $directMlCorePackagePath
    }

    if (Test-Path $extractDir) {
        Remove-Item -Recurse -Force -LiteralPath $extractDir
    }

    if (Test-Path $directMlCoreExtractDir) {
        Remove-Item -Recurse -Force -LiteralPath $directMlCoreExtractDir
    }

    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    $zipPath = Join-Path $downloadDir "microsoft.ml.onnxruntime.directml.$directMlVersion.zip"
    Copy-Item -LiteralPath $packagePath -Destination $zipPath -Force
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

    New-Item -ItemType Directory -Force -Path $directMlCoreExtractDir | Out-Null
    $directMlCoreZipPath = Join-Path $downloadDir "microsoft.ai.directml.$directMlCoreVersion.zip"
    Copy-Item -LiteralPath $directMlCorePackagePath -Destination $directMlCoreZipPath -Force
    Expand-Archive -LiteralPath $directMlCoreZipPath -DestinationPath $directMlCoreExtractDir -Force

    $nativeSource = Join-Path $extractDir "runtimes\win-x64\native"
    $directMlSource = Join-Path $directMlCoreExtractDir "bin\x64-win\DirectML.dll"
    $nativeTargets = @()
    foreach ($onnxRoot in @(
        (Join-Path $modelDir "node_modules\onnxruntime-node\bin"),
        (Join-Path $modelDir "node_modules\@xenova\transformers\node_modules\onnxruntime-node\bin")
    )) {
        if (Test-Path $onnxRoot) {
            $nativeTargets += Get-ChildItem -LiteralPath $onnxRoot -Directory -Recurse |
                Where-Object { $_.FullName -match "\\win32\\x64$" -and (Test-Path (Join-Path $_.FullName "onnxruntime_binding.node")) } |
                Select-Object -ExpandProperty FullName
        }
    }
    if (-not (Test-Path $nativeSource)) {
        throw "DirectML native files not found in package: $nativeSource"
    }

    if (-not (Test-Path $directMlSource)) {
        throw "DirectML core runtime was not found in package: $directMlSource"
    }

    if ($nativeTargets.Count -eq 0) {
        throw "onnxruntime-node native folder not found."
    }

    foreach ($nativeTarget in $nativeTargets) {
        foreach ($fileName in @("onnxruntime.dll", "onnxruntime_providers_shared.dll", "onnxruntime_providers_dml.dll", "DirectML.dll")) {
            $sourceFile = Join-Path $nativeSource $fileName
            if (Test-Path $sourceFile) {
                Copy-Item -LiteralPath $sourceFile -Destination $nativeTarget -Force
            }
        }

        Copy-Item -LiteralPath $directMlSource -Destination $nativeTarget -Force

        $hasProviderDll = Test-Path (Join-Path $nativeTarget "onnxruntime_providers_dml.dll")
        $hasBundledDirectMl = (Test-Path (Join-Path $nativeTarget "DirectML.dll")) -and (Test-Path (Join-Path $nativeTarget "onnxruntime.dll"))
        if (-not $hasProviderDll -and -not $hasBundledDirectMl) {
            throw "DirectML runtime was not found after installation: $nativeTarget"
        }
    }

    $onnxBackend = Join-Path $modelDir "node_modules\@xenova\transformers\src\backends\onnx.js"
    if (Test-Path $onnxBackend) {
        $content = Get-Content -LiteralPath $onnxBackend -Raw
        if ($content -notmatch "ANE-VRED-DIRECTML-PATCH") {
            $content = $content -replace "executionProviders\.unshift\('cpu'\);", @"
if (process.env.ANEVRED_TRANSLATION_ENGINE === 'directml') {
    // ANE-VRED-DIRECTML-PATCH: prefer DirectML when the native provider DLL is present.
    executionProviders.unshift('dml');
} else {
    executionProviders.unshift('cpu');
}
"@
            Set-Content -LiteralPath $onnxBackend -Value $content -Encoding UTF8
        }
    }

    Write-Host "Local EN-RU translation model installed."
}
finally {
    Pop-Location
}
