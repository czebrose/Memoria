@echo off
rem Installs the Memoria online co-op build into FINAL FANTASY IX. Extra arguments go to Install.ps1 (e.g. -GamePath "D:\Games\FINAL FANTASY IX" -OnlineDebug).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
