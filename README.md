# ANEVRED

**Adaptive Neural Enhanced Visual Recognition Engine Daemon**

ANEVRED is a Windows performance companion for Star Citizen and other demanding games. It combines local system monitoring, safe optimization helpers, process protection, Star Citizen utilities, and an experimental screen translation overlay in one desktop tool.

The project is designed to stay local-first. It monitors the current machine, keeps learning data on the device, and avoids destructive system actions.

## Highlights

- Real-time CPU, RAM, GPU, VRAM, frametime, pagefile, and process monitoring
- Safe optimization actions for RAM, CPU load, VRAM pressure, and background tasks
- Protected process list for system, launcher, anti-cheat, and user-selected processes
- Star Citizen oriented hub with session state, log support, profile awareness, and hotkeys
- Live screen translation overlay using Windows OCR and Chrome Translator API when available
- Local learning history for recommendation tuning
- Dark and light themes with responsive dashboard panels
- Multi-language UI support
- Tray integration and startup-friendly window handling

## What ANEVRED Does

ANEVRED watches system pressure and foreground/background activity, then presents conservative recommendations. It does not randomly kill processes or force risky system changes. Protected processes remain excluded from optimization actions.

The AI recommendation layer is local and practical: it looks at observed load, process behavior, resource pressure, and previous samples to decide whether a suggestion is useful. It is not a cloud AI assistant and it does not upload local learning data.

## Screen Translation

The translation overlay captures a selected screen region, runs OCR locally through Windows OCR, and then asks Chrome's Translator API to translate the detected text when that API is available in the installed Chrome build.

If Chrome Translator API is not available, ANEVRED falls back to OCR/original text instead of pretending that translation succeeded.

Star Citizen UI text, button labels, proper names, and alien/lore terms are treated carefully so the overlay does not cover the screen with low-value translations.

## Safety Model

ANEVRED is built around conservative actions:

- protected processes are not terminated or modified
- anti-cheat and system processes are excluded from unsafe actions
- recommendations can be ignored
- local learning can be disabled
- privacy mode keeps learned data on the local machine

## Build

Requirements:

- Windows
- .NET 10 SDK with Windows desktop support

Build:

```powershell
dotnet build ANEVRED.csproj -c Release
```

If the normal `obj` folder is locked on your machine, use custom build folders:

```powershell
New-Item -ItemType Directory -Force -Path buildtmp, buildobj-local, buildbin-local | Out-Null
$env:TEMP=(Resolve-Path buildtmp).Path
$env:TMP=$env:TEMP
dotnet build ANEVRED.csproj -c Release -p:BaseIntermediateOutputPath=buildobj-local\ -p:BaseOutputPath=buildbin-local\
```

## Documentation

- [Product Description](docs/PRODUCT.md)
- [Feature Overview](docs/FEATURES.md)
- [Release Text](docs/RELEASE_NOTES.md)

## Status

ANEVRED is an active desktop project. Some features, especially live translation and Star Citizen specific helpers, are experimental and depend on Windows OCR, Chrome capabilities, and the current Star Citizen UI.

