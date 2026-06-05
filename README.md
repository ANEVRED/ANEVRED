<p align="center">
  <img src="Assets/ANEVRED_Banner.png" alt="ANEVRED" width="720">
</p>

# ANEVRED

**Windows Gaming Optimization, Hardware Monitoring and Star Citizen Assistant**

ANEVRED is a local-first desktop application focused on performance monitoring, safe optimization, hardware visibility, Star Citizen session tracking, and real-time OCR translation overlays.

> Monitor your system, optimize background activity, track Star Citizen sessions, and translate on-screen text without game modifications.

## What You See In The Application

- Real-time CPU, RAM, GPU and VRAM monitoring
- AI-style recommendation engine
- Safe process protection and optimization tools
- Star Citizen session analytics and event tracking
- OCR translation overlay using Windows OCR and Chrome Translator API
- Hardware monitoring and sensor dashboards
- UI dimming and visual comfort tools

> Tip: Add dashboard, Star Citizen Hub, Hardware Monitor and Settings screenshots directly below this section. They will explain ANEVRED faster than any paragraph.

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

## Documentation

- [Product Description](docs/PRODUCT.md)
- [Feature Overview](docs/FEATURES.md)
- [Release Text](docs/RELEASE_NOTES.md)
- [Support ANEVRED](docs/SUPPORT.md)
