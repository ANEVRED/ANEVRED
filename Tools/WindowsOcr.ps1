param(
    [Parameter(Mandatory = $true)]
    [string]$ImagePath,

    [string]$LanguageTag = "en-US",

    [switch]$Json
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Runtime.WindowsRuntime

[Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null

function Wait-WinRtOperation {
    param(
        [Parameter(Mandatory = $true)]
        $Operation,

        [Parameter(Mandatory = $true)]
        [Type]$ResultType
    )

    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq "AsTask" -and
            $_.IsGenericMethodDefinition -and
            $_.GetParameters().Count -eq 1
        } |
        Select-Object -First 1

    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

$file = Wait-WinRtOperation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)) ([Windows.Storage.StorageFile])
$stream = Wait-WinRtOperation ($file.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
$decoder = Wait-WinRtOperation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Wait-WinRtOperation ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

$language = [Windows.Globalization.Language]::new($LanguageTag)
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
if ($null -eq $engine) {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
}

if ($null -eq $engine) {
    throw "Windows OCR engine is not available for $LanguageTag."
}

$result = Wait-WinRtOperation ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
$lineItems = New-Object System.Collections.Generic.List[object]
$textLines = New-Object System.Collections.Generic.List[string]

foreach ($line in $result.Lines) {
    $words = @()
    $left = [double]::PositiveInfinity
    $top = [double]::PositiveInfinity
    $right = 0.0
    $bottom = 0.0

    foreach ($word in $line.Words) {
        if ([string]::IsNullOrWhiteSpace($word.Text)) {
            continue
        }

        $words += $word.Text
        $rect = $word.BoundingRect
        $left = [Math]::Min($left, [double]$rect.X)
        $top = [Math]::Min($top, [double]$rect.Y)
        $right = [Math]::Max($right, [double]($rect.X + $rect.Width))
        $bottom = [Math]::Max($bottom, [double]($rect.Y + $rect.Height))
    }

    if ($words.Count -gt 0) {
        $text = ($words -join " ")
        $textLines.Add($text)
        $lineItems.Add([pscustomobject]@{
            text = $text
            left = $left
            top = $top
            width = [Math]::Max(1.0, $right - $left)
            height = [Math]::Max(1.0, $bottom - $top)
        })
    }
}

if ($Json) {
    [pscustomobject]@{
        text = if ($textLines.Count -gt 0) { $textLines -join [Environment]::NewLine } else { $result.Text }
        lines = $lineItems
    } | ConvertTo-Json -Depth 5 -Compress
} elseif ($textLines.Count -gt 0) {
    $textLines -join [Environment]::NewLine
} else {
    $result.Text
}
