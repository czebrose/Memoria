@echo off
rem Undoes the newest install: puts back the files it saved and removes the ones it added.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall %*
