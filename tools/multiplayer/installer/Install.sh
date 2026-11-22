#!/usr/bin/env bash
# Installs the Memoria online co-op build into FINAL FANTASY IX (Linux, Steam + Proton).
# Extra arguments go to install_linux.py (e.g. --game-path "/path/to/FINAL FANTASY IX" --online-debug).
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$(basename "${BASH_SOURCE[0]}")"

# Started from a file manager (no terminal): open one so the output and questions can be seen
if [ ! -t 0 ] && [ -z "$MEMORIA_ONLINE_IN_TERMINAL" ]; then
    export MEMORIA_ONLINE_IN_TERMINAL=1
    for term in konsole ptyxis gnome-terminal kgx xfce4-terminal xterm; do
        if command -v "$term" >/dev/null 2>&1; then
            case "$term" in
                ptyxis|gnome-terminal|kgx) exec "$term" -- bash "$here/$script" "$@" ;;
                *) exec "$term" -e bash "$here/$script" "$@" ;;
            esac
        fi
    done
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is needed (Fedora: sudo dnf install python3)."
    [ -t 0 ] && read -r -p "Press Enter to close"
    exit 1
fi
exec python3 "$here/install_linux.py" "$@"
