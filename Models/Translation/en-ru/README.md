ANEVRED local EN-RU model
=========================

This folder contains the local EN-RU translator runner for ANEVRED.

Install once from the project root:

`powershell -ExecutionPolicy Bypass -File .\Tools\InstallLocalTranslationModel.ps1`

The install step uses Node.js and downloads `Xenova/opus-mt-en-ru` into this folder's local cache.
After that, `translator.cmd` runs offline.

ANEVRED sends OCR text to the runner via stdin and expects translated UTF-8 text on stdout.

This keeps the game text local. No cloud service is called by ANEVRED.

Expected runtime path after build:

`Models\Translation\en-ru\translator.cmd`
