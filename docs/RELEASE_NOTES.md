# Release Text

## ANEVRED v0.3.3

ANEVRED v0.3.3 is a safety and polish release focused on stronger default protection, clearer Star Citizen maintenance tools, UI dimming controls, support links, localization, and release metadata.

### Safety And Process Protection

- expanded the default protection list for Star Citizen, RSI Launcher, anti-cheat, GPU driver, audio, and input-device helper processes
- added protection rule persistence upgrades so older settings receive new default safety rules automatically
- improved protected-process visibility in the dashboard and settings area
- added quick protection actions for selected processes
- kept optimization actions conservative by skipping protected, critical, launcher, driver, and security-sensitive processes

### Optimization And Cleanup

- tightened CPU optimization so priorities are lowered more gradually where appropriate
- tracked ANEVRED-changed process priorities so they can be restored when the app closes
- improved background app closing behavior to request normal window close instead of forcing unsafe termination
- clarified shader cache cleanup warnings and blocked cache clearing while Star Citizen is running
- kept shader cache maintenance focused on detected Star Citizen cache folders

### UI Dimming

- added a full-screen RGB dimming overlay for bright scenes and visual comfort
- added configurable dimming strength and RGB controls
- added a dimming hotkey with default `Ctrl+Alt+D`
- added automatic dimming tune from the current game image
- limited the dimming overlay to the detected game monitor when possible

### Support And Community

- added support links in the application info area
- added Buy Me a Coffee, PayPal, and RSI referral links to the README and documentation
- added a dedicated support document and Buy Me a Coffee about text
- clarified that support is optional and helps fund OCR, Star Citizen, UI dimming, testing, and future releases

### Localization And UI Text

- refreshed English, German, and Russian localization entries
- added localized labels for UI dimming, protection rules, autostart, shader cache safety, and process actions
- improved visible app text around protected processes and safer optimization behavior

### Version

- updated visible app version and project metadata to `0.3.3`

## ANEVRED v0.3.1

ANEVRED v0.3.1 is a refinement release focused on Star Citizen session history, more reliable log matching, safer cleanup behavior, and clearer project positioning.

### Star Citizen Sessions

- added persistent Star Citizen session history
- added total session count and total play time summary
- added bounded, scrollable session history display
- added expandable per-session log details
- added peak RAM, commit, VRAM, Star Citizen RAM, and Star Citizen commit summaries per session
- improved session end handling so short intentional sessions are preserved

### Game.log Analysis

- matched session-end evidence to the actual session time window
- added support for searching `LIVE\logbackups`
- improved backup log candidate selection by session time
- prevented stale shutdown lines from being attached to newer sessions
- refreshed old saved session log details when possible

### Security Hints

- renamed Defender hint to Security hint
- added detection for Microsoft Defender and common third-party security tools
- added multi-security-process summaries
- highlights the highest-load security process during a Star Citizen session

### Cleanup And Safety

- restored ANEVRED-changed process priorities when the app exits
- hid the UI dimming overlay when the app exits
- cleaned safe Chrome Translator cache folders while preserving translation data
- minimized the main window instead of hiding it during translation region selection

### Documentation

- refreshed the README to explain ANEVRED's original optimization and monitoring focus
- positioned live translation as a later experimental feature
- clarified the local-first and conservative safety model

### Version

- updated visible app version and project metadata to `0.3.1`

## ANEVRED v0.3.0

ANEVRED v0.3.0 introduces a stronger identity for the project and expands the application from a resource optimizer into a Star Citizen focused performance and visual recognition companion.

### Product Identity

- renamed and cleaned up the project around the ANEVRED name
- added the full product expansion: Adaptive Neural Enhanced Visual Recognition Engine Daemon
- updated app icon, splash screen, title, and visible branding
- added a dedicated information area for product, privacy, translation, and AI behavior

### Dashboard And UI

- refreshed dashboard layout and monitoring cards
- improved dark and light theme styling
- added translucent panels with app logo background treatment
- fixed focus states and dark mode behavior for buttons, combo boxes, scrollbars, and tables
- improved startup behavior so the application opens maximized

### AI Recommendations

- expanded the AI recommendations page with summary, queue, details, and local learning information
- added local learning status, sample count, and storage path visibility
- improved recommendation wording and localization behavior
- changed ambiguous actions such as "Check" into clearer labels where appropriate

### Process And Protection

- improved process table visibility and transparency
- added protected process handling for system, launcher, and important background processes
- improved process action controls and status display

### Star Citizen Support

- added Star Citizen oriented status and session handling
- added hotkey configuration for Star Citizen related actions
- improved launcher/process awareness
- added live translation region controls

### Live Translation Overlay

- added Windows OCR based screen text recognition
- added Chrome Translator API integration when available
- added fallback behavior when Chrome Translator API is unavailable
- improved structured overlay rendering
- improved OCR encoding and language handling
- added better diagnostics for skipped or partially translated blocks
- improved handling for Star Citizen UI labels, action buttons, proper names, and short game-specific terms

### Logs

- improved diagnostics for OCR and translation
- added wrapped log message display so long entries remain readable

### Build And Repository Hygiene

- cleaned project metadata and output handling
- excluded generated build folders from project compilation
- added documentation for product description, feature overview, and release text

### Known Limitations

- live translation depends on Windows OCR quality and the current game UI contrast
- Chrome Translator API availability depends on the installed Chrome version and feature support
- Star Citizen UI layouts can change, so OCR block grouping may need further tuning
- alien/lore languages are not treated as complete machine translation yet; they need curated local glossaries
