# Release Text

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
