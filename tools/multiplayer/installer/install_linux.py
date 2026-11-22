#!/usr/bin/env python3
# Online co-op build of Memoria for FINAL FANTASY IX: installer and uninstaller for Linux (Steam + Proton).
# The Linux twin of Install.ps1; same package layout next to this script:
#   manifest.txt      build info and hashes (written by tools\multiplayer\package.ps1)
#   files.sha256      "<sha256>  <path>" for every file under game/
#   base-files.txt    every file the base Memoria patcher writes (paths relative to the game folder)
#   base/Memoria.Patcher.exe, base/Memoria.default.ini
#   game/...          our files, laid out as in the game folder
# The base patcher is a Windows program, so this script installs base Memoria itself: it reads the patcher's payload
# (format: Memoria.Patcher/Program.cs, Run/ExtractFiles) and writes the same files the patcher would.
# It never touches HadesWorkshopMod/ (the mod's own files) and never replaces an existing Memoria.ini: it only adds keys
# that are missing from it. Needs only python3 (3.6+), which Fedora and its spins ship.
import argparse
import datetime
import hashlib
import os
import re
import shutil
import struct
import subprocess
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
GAME_FOLDER_NAME = 'FINAL FANTASY IX'
MARKER_NAME = 'MemoriaOnline.installed.txt'
ENC = 'cp1252'
MAGIC = 0x004149524F4D454D  # "MEMORIA\0"
NO_PAUSE = False

GREEN, YELLOW, CYAN, RED, RESET = ('\033[32m', '\033[33m', '\033[36m', '\033[31m', '\033[0m') if sys.stdout.isatty() else ('',) * 5


def step(text): print(f'{CYAN}-> {text}{RESET}')
def ok(text): print(f'{GREEN}   OK  {text}{RESET}')
def warn(text): print(f'{YELLOW}   !!  {text}{RESET}')


class Failure(Exception):
    pass


def finish(code):
    if not NO_PAUSE and sys.stdin.isatty():
        print()
        try:
            input('Press Enter to close')
        except EOFError:
            pass
    sys.exit(code)


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest().upper()


def read_key_values(path):
    result = {}
    with open(path, encoding='utf-8-sig') as f:
        for line in f:
            m = re.match(r'^\s*([^#=][^=]*?)\s*=\s*(.*)$', line.rstrip('\r\n'))
            if m:
                result[m.group(1)] = m.group(2).strip()
    return result


def write_lines(path, lines, encoding='utf-8', newline='\n'):
    with open(path, 'w', encoding=encoding, newline='') as f:
        f.write(newline.join(lines) + (newline if lines else ''))


def parts_of(rel):
    return [p for p in re.split(r'[\\/]+', rel) if p]


def assert_not_mod_path(rel):
    parts = parts_of(rel)
    if parts and parts[0].lower() == 'hadesworkshopmod':
        raise Failure(f"Refusing to touch mod file '{rel}' (the mod's files are installed separately).")


def resolve(root, rel):
    """Game path for a Windows-style relative path. Linux is case-sensitive and the game's files keep Windows casing,
    so each existing component is matched without regard to case; missing ones keep the given name."""
    path = root
    for part in parts_of(rel):
        candidate = os.path.join(path, part)
        if not os.path.exists(candidate) and os.path.isdir(path):
            lower = part.lower()
            for name in os.listdir(path):
                if name.lower() == lower:
                    candidate = os.path.join(path, name)
                    break
        path = candidate
    return path


def is_game_folder(path):
    return bool(path) and os.path.isfile(resolve(path, 'x64/FF9_Data/Managed/Assembly-CSharp.dll'))


# ---- finding the game ----
def steam_roots():
    home = os.path.expanduser('~')
    roots = [
        os.path.join(home, '.local/share/Steam'),
        os.path.join(home, '.steam/steam'),
        os.path.join(home, '.steam/root'),
        os.path.join(home, '.var/app/com.valvesoftware.Steam/.local/share/Steam'),  # Flatpak Steam
        os.path.join(home, '.var/app/com.valvesoftware.Steam/data/Steam'),
    ]
    seen, result = set(), []
    for r in roots:
        if os.path.isdir(r):
            real = os.path.realpath(r)
            if real not in seen:
                seen.add(real)
                result.append(real)
    return result


def find_game_folder():
    candidates = []
    for root in steam_roots():
        candidates.append(os.path.join(root, 'steamapps/common', GAME_FOLDER_NAME))
        vdf = os.path.join(root, 'steamapps/libraryfolders.vdf')
        if os.path.isfile(vdf):
            with open(vdf, encoding='utf-8', errors='replace') as f:
                for m in re.finditer(r'"path"\s+"([^"]+)"', f.read()):
                    lib = m.group(1).replace('\\\\', '\\')
                    candidates.append(os.path.join(lib, 'steamapps/common', GAME_FOLDER_NAME))
    for c in candidates:
        if is_game_folder(c):
            return os.path.realpath(c)
    warn('Could not find FINAL FANTASY IX automatically.')
    if not sys.stdin.isatty():
        return None
    while True:
        try:
            answer = input('Paste the game folder (the one with FF9_Launcher.exe), or leave empty to cancel: ')
        except EOFError:
            return None
        answer = os.path.expanduser(answer.strip().strip('"\''))
        if not answer:
            return None
        if is_game_folder(answer):
            return os.path.realpath(answer)
        warn(f"No x64/FF9_Data/Managed/Assembly-CSharp.dll under '{answer}'.")


def assert_game_not_running():
    names = ('ff9.exe', 'ff9_launcher.exe', 'memoria.launcher.exe', 'memoria.patcher.exe')
    running = set()
    try:
        pids = [p for p in os.listdir('/proc') if p.isdigit()]
    except OSError:
        return
    for pid in pids:
        try:
            with open(f'/proc/{pid}/cmdline', 'rb') as f:
                args = f.read().split(b'\0')
        except OSError:
            continue
        for a in args[:2]:
            base = re.split(r'[\\/]', a.decode('utf-8', 'replace'))[-1].lower()
            if base in names:
                running.add(base)
    if running:
        raise Failure(f"Close the game and its launcher first (running: {', '.join(sorted(running))}).")


def documents_dir():
    try:
        out = subprocess.run(['xdg-user-dir', 'DOCUMENTS'], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=5)
        d = out.stdout.decode().strip()
        if d and os.path.isdir(d) and os.path.realpath(d) != os.path.realpath(os.path.expanduser('~')):
            return d
    except Exception:
        pass
    return os.path.join(os.path.expanduser('~'), 'Documents')


BACKUP_ROOT = os.path.join(documents_dir(), 'FF9-OnlineCoop-Backups')


# ---- Memoria.ini: keep the user's file, add only the keys it lacks (same rules as Install.ps1) ----
def read_ini_sections(lines):
    sections = [{'Name': '', 'Keys': set(), 'Start': -1, 'LastLine': -1}]
    current = sections[0]
    for i, line in enumerate(lines):
        m = re.match(r'^\s*\[([^\]]+)\]', line)
        if m:
            current = {'Name': m.group(1).strip(), 'Keys': set(), 'Start': i, 'LastLine': i}
            sections.append(current)
            continue
        m = re.match(r'^\s*([A-Za-z0-9_.]+)\s*=', line)
        if m:
            current['Keys'].add(m.group(1).lower())
        if line.strip() != '' and not re.match(r'^\s*;', line):
            current['LastLine'] = i
    return sections


def read_ini_lines(path):
    with open(path, 'rb') as f:
        return f.read().decode(ENC, 'replace').splitlines()


def merge_missing_ini_keys(ini_path, default_ini_path, set_online_debug):
    user = read_ini_lines(ini_path)
    default = read_ini_lines(default_ini_path)
    user_sections = read_ini_sections(user)
    def_sections = read_ini_sections(default)
    wanted = []
    for ds in def_sections:
        if not ds['Name']:
            continue
        later = [s['Start'] for s in def_sections if s['Start'] > ds['Start']]
        end = min(later) if later else len(default)
        key_lines = [default[i].strip() for i in range(ds['Start'] + 1, end) if re.match(r'^\s*([A-Za-z0-9_.]+)\s*=', default[i])]
        wanted.append({'Name': ds['Name'], 'Lines': key_lines})
    if set_online_debug:
        dbg = next((w for w in wanted if w['Name'] == 'Debug'), None)
        if dbg is None:
            dbg = {'Name': 'Debug', 'Lines': []}
            wanted.append(dbg)
        if not any(re.match(r'^OnlineDebug\s*=', l) for l in dbg['Lines']):
            dbg['Lines'].append('OnlineDebug = 1')
    inserts, append_at_end, added = {}, [], []
    for w in wanted:
        us = next((s for s in user_sections if s['Name'] == w['Name']), None)
        missing = []
        for kl in w['Lines']:
            k = kl.split('=', 1)[0].strip()
            if us is None or k.lower() not in us['Keys']:
                missing.append(kl)
                added.append(f"[{w['Name']}] {k}")
        if not missing:
            continue
        if us is not None:
            inserts.setdefault(us['LastLine'], []).extend(missing)
        else:
            append_at_end.extend([''] + [f"[{w['Name']}]"] + missing)
    out = []
    for i, line in enumerate(user):
        out.append(line)
        out.extend(inserts.get(i, []))
    out.extend(append_at_end)
    changed = []
    if set_online_debug:
        in_debug = False
        for i, line in enumerate(out):
            if re.match(r'^\s*\[', line):
                in_debug = bool(re.match(r'^\s*\[Debug\]', line))
                continue
            m = re.match(r'^\s*(Enabled|OnlineDebug)\s*=\s*(.*)$', line)
            if in_debug and m and m.group(2).strip() != '1':
                changed.append(f'[Debug] {m.group(1)} = 1')
                out[i] = f'{m.group(1)} = 1'
    if added or changed:
        write_lines(ini_path, out, ENC, '\r\n')
    return added, changed


# ---- base Memoria ----
def get_base_status(game, manifest):
    if not os.path.isfile(resolve(game, 'x64/FF9_Data/Managed/Memoria.Prime.dll')) or not os.path.isfile(resolve(game, 'Memoria.ini')):
        return 'missing'
    marker = resolve(game, MARKER_NAME)
    if os.path.isfile(marker) and read_key_values(marker).get('BasePatcherSha256') == manifest.get('BasePatcherSha256'):
        return 'same'
    for name in ('FF9_Launcher.exe', 'FF9_Launcher.fix'):
        p = resolve(game, name)
        if os.path.isfile(p) and sha256(p) == manifest.get('BaseLauncherSha256'):
            return 'same'
    return 'other'


class GzipReader:
    """Reads one gzip stream from a file position (like .NET's GZipStream); trailing bytes are ignored."""

    def __init__(self, f):
        self.f = f
        self.z = zlib.decompressobj(16 + zlib.MAX_WBITS)
        self.buf = bytearray()

    def read(self, n):
        while len(self.buf) < n:
            if self.z.eof:
                break
            chunk = self.f.read(1 << 16)
            if not chunk:
                self.buf += self.z.flush()
                break
            self.buf += self.z.decompress(chunk)
        data = bytes(self.buf[:n])
        del self.buf[:n]
        if len(data) != n:
            raise Failure('Truncated base patcher payload: the package is damaged.')
        return data


def install_base(patcher_path, game):
    """Writes base Memoria's files the way Memoria.Patcher.exe does (minus the Windows-only Steam overlay fix)."""
    with open(patcher_path, 'rb') as f:
        f.seek(-0x18, os.SEEK_END)
        pos = -1
        if struct.unpack('<q', f.read(8))[0] == MAGIC:
            pos = f.tell() - 8
        else:
            for off in range(0x2920, 0x2700, -1):  # signed patcher: the payload header sits before the signature
                f.seek(-off, os.SEEK_END)
                if struct.unpack('<q', f.read(8))[0] == MAGIC:
                    pos = f.tell() - 8
                    break
        if pos < 0:
            raise Failure('No Memoria payload in base/Memoria.Patcher.exe: the package is damaged.')
        f.seek(pos + 8)
        left, data_pos = struct.unpack('<qq', f.read(16))

        # The patcher deletes the old field scripts first
        common = resolve(game, 'StreamingAssets/Assets/Resources/CommonAsset')
        if os.path.isdir(common):
            shutil.rmtree(common, ignore_errors=True)

        has_x64 = os.path.isdir(resolve(game, 'x64/FF9_Data/Managed'))
        f.seek(data_pos)
        z = GzipReader(f)
        names = {}
        written = 0
        while left > 0:
            size, ticks, count = struct.unpack('<IqB', z.read(13))
            parts = []
            for _ in range(count):
                (pid,) = struct.unpack('<H', z.read(2))
                if pid & 0x8000:
                    (length,) = struct.unpack('<B', z.read(1))
                    name = z.read(length).decode('utf-8')
                    names[pid & 0x7FFF] = name
                else:
                    name = names[pid]
                parts.append(name)
            left -= size
            rel = '/'.join(parts)
            if '{PLATFORM}' in parts and not has_x64:
                skip(z, size)
                continue
            rel = rel.replace('{PLATFORM}', 'x64')
            assert_not_mod_path(rel)
            out = resolve(game, rel)
            base_name = os.path.basename(out)
            if os.path.exists(out):
                if base_name.lower() in ('memoria.ini', 'settings.ini'):
                    # The patcher merges these with the user's; the user's file is kept byte for byte anyway
                    skip(z, size)
                    continue
                if os.path.splitext(out)[1].lower() in ('.exe', '.dll'):
                    bak = os.path.splitext(out)[0] + '.bak'
                    if not os.path.exists(bak):
                        os.rename(out, bak)
            else:
                os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, 'wb') as o:
                remaining = size
                while remaining > 0:
                    n = min(remaining, 1 << 16)
                    o.write(z.read(n))
                    remaining -= n
            mtime = (ticks - 621355968000000000) / 10000000.0
            try:
                os.utime(out, (mtime, mtime))
            except (OSError, OverflowError):
                pass
            written += 1
    return written


def skip(z, size):
    while size > 0:
        n = min(size, 1 << 16)
        z.read(n)
        size -= n


def read_our_files():
    result = []
    with open(os.path.join(HERE, 'files.sha256'), encoding='utf-8-sig') as f:
        for line in f:
            m = re.match(r'^([0-9A-Fa-f]{64})\s+(.+)$', line.strip())
            if m:
                rel = m.group(2).strip().replace('\\', '/')
                assert_not_mod_path(rel)
                result.append({'Hash': m.group(1).upper(), 'Rel': rel})
    return result


def read_base_files():
    result = []
    with open(os.path.join(HERE, 'base-files.txt'), encoding='utf-8-sig') as f:
        for line in f:
            rel = line.strip().replace('\\', '/')
            if rel:
                assert_not_mod_path(rel)
                result.append(rel)
    return result


# ---- uninstall ----
def restore(game, backup_path):
    backup = backup_path
    if not backup:
        found = []
        if os.path.isdir(BACKUP_ROOT):
            for name in sorted(os.listdir(BACKUP_ROOT), reverse=True):
                d = os.path.join(BACKUP_ROOT, name)
                info = os.path.join(d, 'backup.txt')
                if os.path.isfile(info) and not os.path.exists(os.path.join(d, 'restored.txt')) \
                        and os.path.realpath(read_key_values(info).get('GamePath', '')) == game:
                    found.append(d)
        backup = found[0] if found else None
    if not backup or not os.path.isfile(os.path.join(backup, 'backup.txt')):
        raise Failure(f'No backup found for {game} in {BACKUP_ROOT}.')
    step(f'Restoring from {backup}')
    files = os.path.join(backup, 'files')
    restored = 0
    if os.path.isdir(files):
        for dirpath, _, filenames in os.walk(files):
            for fn in filenames:
                src = os.path.join(dirpath, fn)
                rel = os.path.relpath(src, files)
                assert_not_mod_path(rel)
                dest = resolve(game, rel)
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                shutil.copy2(src, dest)
                restored += 1
    removed = 0
    created = os.path.join(backup, 'created.txt')
    if os.path.isfile(created):
        with open(created, encoding='utf-8-sig') as f:
            for rel in (l.strip() for l in f):
                if not rel:
                    continue
                assert_not_mod_path(rel)
                p = resolve(game, rel)
                if os.path.isfile(p):
                    os.remove(p)
                    removed += 1
    marker = resolve(game, MARKER_NAME)
    if os.path.isfile(marker) and not os.path.exists(os.path.join(files, MARKER_NAME)):
        os.remove(marker)
    ok(f'Put back {restored} file(s), removed {removed} file(s) the install had added.')
    write_lines(os.path.join(backup, 'restored.txt'), [f'Restored = {datetime.datetime.now().isoformat(timespec="seconds")}'])
    print()
    print(f'{GREEN}Restore finished.{RESET}')
    print(f'  Game folder:          {game}')
    print(f"  Assembly-CSharp.dll:  {sha256(resolve(game, 'x64/FF9_Data/Managed/Assembly-CSharp.dll'))[:12]}")
    print(f'  The backup folder is kept: {backup}')


# ---- main ----
def main():
    global NO_PAUSE
    ap = argparse.ArgumentParser(description='Installs (or with --uninstall, removes) the Memoria online co-op build.')
    ap.add_argument('--game-path', help='the FINAL FANTASY IX folder (default: found through Steam)')
    ap.add_argument('--uninstall', action='store_true', help='restore the newest backup')
    ap.add_argument('--backup-path', help='uninstall from this backup folder instead of the newest one')
    ap.add_argument('--online-debug', action='store_true', help='test PCs: set [Debug] Enabled = 1 and OnlineDebug = 1 in Memoria.ini')
    ap.add_argument('--force-patcher', action='store_true', help='install base Memoria even if it is already there')
    ap.add_argument('--no-pause', action='store_true', help="don't wait for Enter at the end")
    args = ap.parse_args()
    NO_PAUSE = args.no_pause

    manifest_path = os.path.join(HERE, 'manifest.txt')
    if not os.path.isfile(manifest_path):
        raise Failure('manifest.txt not found next to the script: run Install.sh from the unzipped package.')
    manifest = read_key_values(manifest_path)
    print(f"Memoria online co-op build {manifest.get('Build')}")
    print(f"  branch {manifest.get('Branch')}, commit {manifest.get('Commit')}, built {manifest.get('BuiltUtc')} UTC")
    print()

    if args.game_path:
        if not is_game_folder(os.path.expanduser(args.game_path)):
            raise Failure(f'Not a FINAL FANTASY IX folder: {args.game_path}')
        game = os.path.realpath(os.path.expanduser(args.game_path))
    else:
        step('Looking for FINAL FANTASY IX')
        game = find_game_folder()
        if not game:
            raise Failure('No game folder given.')
    ok(game)
    assert_game_not_running()
    if not os.access(game, os.W_OK):
        raise Failure(f"Can't write to {game}.")

    if args.uninstall:
        restore(game, args.backup_path)
        finish(0)

    our_files = read_our_files()
    base_files = read_base_files()

    step('Checking the installed Memoria')
    base_status = get_base_status(game, manifest)
    run_base = args.force_patcher or base_status != 'same'
    if base_status == 'missing':
        warn('Memoria is not installed: base Memoria will be installed.')
    elif base_status == 'other':
        warn(f"A different (or unknown) Memoria version is installed: base Memoria ({manifest.get('BaseVersion')}) will update it.")
    elif args.force_patcher:
        warn('Base Memoria matches; installing it again anyway (--force-patcher).')
    else:
        ok(f"Base Memoria is {manifest.get('BaseVersion')}.")

    # Back up everything this run may overwrite
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    backup = os.path.join(BACKUP_ROOT, stamp)
    backup_files = os.path.join(backup, 'files')
    step(f'Backing up to {backup}')
    to_backup = {}  # lowercase key -> rel (keeps the first spelling)

    def want(rel):
        to_backup.setdefault(rel.replace('\\', '/').lower(), rel.replace('\\', '/'))

    for f in our_files:
        want(f['Rel'])
    for n in ('Memoria.ini', 'Settings.ini', MARKER_NAME):
        want(n)
    if run_base:
        for rel in base_files:
            want(rel)
            if re.search(r'\.(exe|dll)$', rel, re.I):
                want(os.path.splitext(rel)[0] + '.bak')
        common = resolve(game, 'StreamingAssets/Assets/Resources/CommonAsset')
        if os.path.isdir(common):
            for dirpath, _, filenames in os.walk(common):
                for fn in filenames:
                    want(os.path.relpath(os.path.join(dirpath, fn), game))
    created = []
    saved = 0
    for rel in to_backup.values():
        assert_not_mod_path(rel)
        src = resolve(game, rel)
        if os.path.isfile(src):
            dest = os.path.join(backup_files, os.path.relpath(src, game))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.copy2(src, dest)
            saved += 1
        else:
            created.append(rel)  # not there yet: uninstall removes it again if this run adds it
    os.makedirs(backup, exist_ok=True)
    write_lines(os.path.join(backup, 'created.txt'), created)
    write_lines(os.path.join(backup, 'backup.txt'), [
        f'GamePath = {game}', f'Made = {datetime.datetime.now().isoformat(timespec="seconds")}', f"Build = {manifest.get('Build')}",
        f'PatcherRun = {run_base}', f'FilesSaved = {saved}', f'FilesAdded = {len(created)}'])
    ok(f'Saved {saved} file(s); {len(created)} file(s) will be new.')

    if run_base:
        patcher = os.path.join(HERE, 'base', 'Memoria.Patcher.exe')
        if sha256(patcher) != manifest.get('BasePatcherSha256'):
            raise Failure('base/Memoria.Patcher.exe does not match manifest.txt: the package is damaged.')
        step(f"Installing base Memoria ({manifest.get('BaseVersion')}) from the patcher's files; this takes a minute")
        n = install_base(patcher, game)
        for rel in ('x64/FF9_Data/Managed/Memoria.Prime.dll', 'FF9_Launcher.exe'):
            if not os.path.isfile(resolve(game, rel)):
                raise Failure(f"Base Memoria didn't install {rel}.")
        if sha256(resolve(game, 'FF9_Launcher.exe')) != manifest.get('BaseLauncherSha256'):
            raise Failure("Base Memoria's launcher wasn't installed.")
        ini = resolve(game, 'Memoria.ini')
        if not os.path.isfile(ini):
            shutil.copyfile(os.path.join(HERE, 'base', 'Memoria.default.ini'), ini)
        ok(f'Base Memoria installed ({n} files).')

    step('Copying the online co-op files')
    for f in our_files:
        dest = resolve(game, f['Rel'])
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copyfile(os.path.join(HERE, 'game', *parts_of(f['Rel'])), dest)
        ok(f['Rel'])

    step('Memoria.ini')
    added, changed = merge_missing_ini_keys(resolve(game, 'Memoria.ini'), os.path.join(HERE, 'base', 'Memoria.default.ini'), args.online_debug)
    if not added and not changed:
        ok('Kept as it was (no keys missing).')
    for a in added:
        ok(f'added {a}')
    for c in changed:
        ok(f'set {c} (--online-debug)')

    step('Verifying')
    bad = 0
    for f in our_files:
        h = sha256(resolve(game, f['Rel']))
        if h != f['Hash']:
            bad += 1
            warn(f"{f['Rel']}: hash {h[:12]} but expected {f['Hash'][:12]}")
    acs = sha256(resolve(game, 'x64/FF9_Data/Managed/Assembly-CSharp.dll'))
    if acs != manifest.get('AssemblyCSharpSha256'):
        bad += 1
        warn('Assembly-CSharp.dll does not match manifest.txt.')
    if bad:
        raise Failure(f'{bad} file(s) failed verification. Run Uninstall.sh to undo.')
    ok(f'Assembly-CSharp.dll {acs[:12]} matches.')

    write_lines(resolve(game, MARKER_NAME), [
        f"Build = {manifest.get('Build')}", f"Branch = {manifest.get('Branch')}", f"Commit = {manifest.get('Commit')}",
        f"BaseVersion = {manifest.get('BaseVersion')}", f"BasePatcherSha256 = {manifest.get('BasePatcherSha256')}",
        f'AssemblyCSharpSha256 = {acs}', f'Installed = {datetime.datetime.now().isoformat(timespec="seconds")}', f'Backup = {backup}'], newline='\r\n')

    print()
    print(f'{GREEN}Installed.{RESET}')
    print(f'  Game folder:          {game}')
    print(f"  Build:                {manifest.get('Build')} ({manifest.get('Branch')} {manifest.get('Commit')})")
    print(f"  Base Memoria:         {manifest.get('BaseVersion')}{' (installed now)' if run_base else ' (already installed)'}")
    print(f'  Assembly-CSharp.dll:  {acs[:12]} (verified)')
    print(f'  Backup:               {backup}')
    if not os.path.isdir(resolve(game, 'HadesWorkshopMod')):
        warn('No HadesWorkshopMod folder: install the mod too (it comes separately) before playing online.')
    print('  Undo with Uninstall.sh (restores the newest backup).')
    finish(0)


if __name__ == '__main__':
    try:
        main()
    except Failure as e:
        print()
        print(f'{RED}FAILED: {e}{RESET}')
        finish(1)
    except Exception as e:  # unexpected: show it all
        import traceback
        traceback.print_exc()
        print(f'{RED}FAILED: {e}{RESET}')
        finish(1)
