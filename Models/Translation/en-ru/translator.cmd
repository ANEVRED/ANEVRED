@echo off
setlocal
set "MODEL_DIR=%~dp0"
set "NODE_EXE=node"

if exist "%MODEL_DIR%node\node.exe" set "NODE_EXE=%MODEL_DIR%node\node.exe"

"%NODE_EXE%" "%MODEL_DIR%translator.mjs" %*
