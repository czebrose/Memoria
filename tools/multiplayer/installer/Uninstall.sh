#!/usr/bin/env bash
# Undoes the newest install (Linux): puts back the files it saved and removes the ones it added.
exec bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/Install.sh" --uninstall "$@"
