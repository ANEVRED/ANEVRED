# ANEVRED v0.3.5 Public Beta

Free Windows x64 download. Donations are optional.

## Download and start

Download `ANEVRED-0.3.5-win-x64.zip`, extract the complete folder, and run `ANEVRED.exe`. The .NET Desktop runtime is included. `SHA256SUMS.txt` contains the package checksum.

This is the full Main edition. Optional translation still depends on compatible Chrome/language packs; it is not the reduced Workstation edition.

## Changes since v0.3.3

- Protection rules, including removed defaults, persist across restarts.
- Atomic settings saves, last-good backups and visible recovery/write errors.
- Fixed macro-file recovery so malformed files are preserved and can recover from backup instead of being silently overwritten by an empty list.
- Prevented simultaneous instances from overwriting shared settings.
- Reused dashboard cards/history data and reduced hidden-window polling.
- Cached NVIDIA queries and explicitly released WMI temperature resources.
- Added a bounded resource-usage panel and manual diagnostic CSV export.

## Testing and limits

Release build plus isolated settings, macro recovery and runtime/efficiency regression suites. The diagnostics panel was rendered with actual bindings and theme. These checks do not prove that the separately reported long-running Workstation memory issue is solved; long-term testing on affected hardware remains necessary.

Automatic optimization is off by default for new users. Review protection rules before enabling it. Experimental operations are optional and performance gains are not guaranteed.

Feedback: https://github.com/ANEVRED/ANEVRED/issues
