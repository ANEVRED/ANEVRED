# Feature Overview

## Dashboard

- system status and performance score
- CPU, RAM, GPU, VRAM, frametime, and temperature cards
- history graphs for load and frametime
- RAM, VRAM, GPU, and pagefile/commit overview
- top process table
- per-core CPU load view

## AI Recommendations

- local recommendation queue
- severity levels for suggestions
- details panel for the selected recommendation
- local learning status and sample count
- privacy-oriented local storage
- conservative action model for optimization suggestions

Current recommendation examples:

- background load detected
- RAM pressure
- CPU load pressure
- VRAM pressure
- stable system state

## Process Management

- live process list
- CPU/GPU/RAM/commit/VRAM columns
- process priority and status visibility
- protected process checkbox
- selected process actions
- context menu actions
- protected system, launcher, and anti-cheat aware behavior

## Star Citizen Hub

- Star Citizen status awareness
- session timer
- launcher/process awareness
- profile status
- log and hotkey support
- translation hotkey configuration

## Hardware Monitoring

- CPU load
- RAM usage
- GPU load
- VRAM usage
- frametime estimate
- GPU temperature when available
- CPU temperature status when available

## Logs

- diagnostic application logs
- screen translation status logs
- OCR and translation pipeline messages
- wrapped log messages for long diagnostics

## Settings

- auto mode
- local learning toggle
- privacy mode
- autostart toggle
- RAM and CPU automation intervals
- RAM and CPU thresholds
- process list refresh interval
- max process row count
- Star Citizen client folder selection
- hotkey capture
- translation target language and region selection
- protection rule editor

## Live Translation Overlay

- selectable screen region
- manual capture hotkey
- auto refresh mode
- Windows OCR based text detection
- Chrome Translator API integration when available
- fallback to original OCR text when Chrome translation is unavailable
- structured overlay blocks aligned to detected UI text
- special handling for short Star Citizen UI labels, names, and action buttons

## Theming And UI

- dark and light theme
- translucent panel style
- app logo background treatment
- custom dark scrollbars and controls
- tray icon integration
- maximized startup behavior

## Safety

- protected processes are excluded from destructive actions
- system and anti-cheat related processes are treated conservatively
- recommendations can be ignored
- local learning can be disabled
- no forced optimization without user-facing control

