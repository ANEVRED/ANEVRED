<p align="center">
  <img src="Assets/ANEVRED_Banner.png" alt="ANEVRED" width="720">
</p>

# ANEVRED

**Adaptive Neural Enhanced Visual Recognition Engine Daemon**

ANEVRED started as a personal toolset for Star Citizen. The original goal was simple: gain more control over system resources, background processes, memory pressure, and performance on a mid-range gaming system.

Over time, the project evolved into a local-first collection of utilities focused on monitoring, safe optimization, and quality-of-life improvements, including Star Citizen session tools, UI dimming, and a real-time OCR translation overlay.

<p align="center">
  <a href="https://buymeacoffee.com/anevred"><img src="https://img.shields.io/badge/Buy%20Me%20a%20Coffee-Support-yellow?style=for-the-badge" alt="Buy Me a Coffee"></a>
  <a href="https://paypal.me/Anevred"><img src="https://img.shields.io/badge/PayPal-Donate-blue?style=for-the-badge" alt="PayPal"></a>
</p>

## Why ANEVRED Exists

I do not own a high-end system.

With a moderate CPU and 32 GB of RAM, I wanted a way to:

- monitor resource usage in real time
- detect unnecessary background activity
- free memory when needed
- reduce distractions while playing
- access useful information without alt-tabbing
- keep control over what is running on my system
- react quickly when Star Citizen starts to stutter or behave strangely

ANEVRED was built to solve those problems first. The live translation overlay came later and became one of the most visible features, but optimization, monitoring, and control were the original reason for the project.

## Main Features

### System Monitoring

- CPU monitoring
- RAM and pagefile monitoring
- GPU and VRAM monitoring
- frametime estimate
- process analysis
- performance overview dashboard
- local recommendation hints

### Optimization Tools

- safe background process management
- memory cleanup tools
- CPU priority adjustment for selected background processes
- protected process lists
- automatic optimization profiles
- gaming mode presets
- restore of ANEVRED-changed process priorities on app exit

### Star Citizen Integration

- Star Citizen process awareness
- session tracking
- persistent session history
- total session time summary
- Game.log and logbackup analysis for session-end hints
- server change, respawn, lag, and stutter markers
- hotkey support for quick event logging
- security-process hints during active sessions

### Live Translation Overlay

Originally added as an experimental feature after the optimization tools.

- selectable screen region
- Windows OCR based text detection
- real-time translation when Chrome's Translator API is available
- external overlay rendering
- layout-aware translated text blocks
- no game file modification
- no injection
- no custom language packs required

The translation overlay works not only in Star Citizen, but in virtually any application where Windows OCR can read the selected region.

### UI Dimming

For bright scenes, night play, and improved visibility.

- adjustable RGB filtering
- full-screen dimming overlay
- hotkey controlled
- optional auto tuning from the current image
- overlay is removed when ANEVRED exits

## Safety Model

ANEVRED uses conservative local actions:

- protected processes are not terminated or modified
- system, security, launcher, and anti-cheat processes are excluded from unsafe actions
- process priorities changed by ANEVRED are restored on app exit
- recommendations can be ignored
- local learning can be disabled
- privacy mode keeps learned data on the local machine
- screen translation uses normal desktop capture and OCR, not game injection

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

## Support

If ANEVRED helps you enjoy your gaming sessions, support helps fund new features, testing, and future releases.

- Buy Me a Coffee: https://buymeacoffee.com/anevred
- PayPal: https://paypal.me/Anevred
- Star Citizen referral: https://www.robertsspaceindustries.com/enlist?referral=STAR-4WLN-4RNF

## Documentation

- [Product Description](docs/PRODUCT.md)
- [Feature Overview](docs/FEATURES.md)
- [Release Text](docs/RELEASE_NOTES.md)
- [Support ANEVRED](docs/SUPPORT.md)

## Status

ANEVRED is an active desktop project. Some features, especially live translation, local recommendation tuning, UI dimming, and Star Citizen-specific helpers, are experimental and depend on Windows APIs, Chrome capabilities, and the current Star Citizen client behavior.
