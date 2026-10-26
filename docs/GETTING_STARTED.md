# Start ANEVRED

ANEVRED is free to download and use. Donations are optional.

1. Download `ANEVRED-0.3.5-win-x64.zip` from the GitHub release.
2. Extract the whole ZIP to a writable folder.
3. Open that folder and start `ANEVRED.exe`. Keep the other files beside it.

This package includes the .NET Desktop runtime; no separate .NET installation is required. It targets Windows x64. The application is currently a public beta.

## First use

Start with monitoring. Review the process protection list in Settings before enabling automatic optimization. Automatic optimization is disabled by default for new installations; existing settings are retained.

RAM/CPU optimization changes the working sets or priorities of eligible processes. It does not guarantee higher FPS or permanently free physical memory. Experimental actions are optional.

The translation overlay is optional and requires compatible Google Chrome and supported Translator API language packs. Initial language-model download may require an Internet connection. The main optimizer does not require translation to be enabled. This release is the full Main edition, not the reduced Workstation edition.

## Settings and diagnostics

Settings and macros are saved under `%LOCALAPPDATA%\ANEVRED`. The application keeps backup files and reports recovery/write problems. Keep your backups when updating; extraction alone does not migrate or delete your existing configuration.

Settings > ANEVRED resource usage shows the application's own memory, CPU, uptime, handles and threads. Its in-memory history is limited to 120 samples. Export writes a CSV only when you choose a destination. Please review any diagnostic file before sharing it.

## Current limits

The reported long-running memory growth of a separate Workstation executable has not been reproduced or conclusively fixed. Short component tests and a successful build do not replace long-term testing across different PCs.

CPU temperature availability depends on Windows/hardware support. Process CPU figures are normalized estimates and may differ from Windows Task Manager. A signed Windows installer and automatic updating are not included in this release.

## Feedback

Please report reproducible problems at https://github.com/ANEVRED/ANEVRED/issues with Windows version, ANEVRED version, steps, and expected/actual behavior. Do not include credentials, personal screen content, or unreviewed logs.
