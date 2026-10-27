#!/usr/bin/env python3
"""Keep Medal's recording buffer out of the Data share's recycle folder.

Medal keeps its recording buffer in <clip folder>/Temp and deletes it after each
game; the [Data] share keeps every deleted file in Recycle/<user> with no purge.
This adds RecordingBuffer to the share's recycle:exclude_dir. Samba matches
individual directory components, not complete paths: every directory named
RecordingBuffer on Data is excluded. Normal clips and other Temp directories
remain recycled. The previously deployed full-path patterns are removed.

Run as root inside CT 211 (file-storage). Inspection is the default: it checks
the live config against the reviewed SHA-256, validates the change with
testparm and prints it. --apply backs up smb.conf under
/root/backups/<YYYY-MM-DD-HHMM>/medal-recycle, installs the change and reloads
Samba, restoring the backup if any step fails. See docs/medal-sync.md.
"""
import argparse
from datetime import datetime
import difflib
import hashlib
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile

CONFIG = Path('/etc/samba/smb.conf')
BACKUPS = Path('/root/backups')
SECTION = 'Data'
PARAMETER = 'recycle:exclude_dir'
PATTERNS = ('RecordingBuffer',)
LEGACY_PATTERNS = ('Media/Videos/Clips/Temp', 'Media/Videos/Clips/Temp/*')


def run(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(args, check=True, text=True, capture_output=True)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def add_exclusions(text: str) -> str:
    """Return the config with PATTERNS added to [Data]'s exclude_dir; unchanged when present."""
    lines = text.splitlines(keepends=True)
    section = None
    target = None
    for index, line in enumerate(lines):
        header = re.match(r'^\s*\[([^\]]+)\]\s*$', line)
        if header:
            section = header.group(1).strip()
            continue
        if section == SECTION and re.match(r'^\s*' + re.escape(PARAMETER) + r'\s*=', line, re.IGNORECASE):
            if target is not None:
                raise ValueError(f'[{SECTION}] sets {PARAMETER} more than once')
            target = index
    if target is None:
        raise ValueError(f'[{SECTION}] has no {PARAMETER} line to extend')
    key, _, value = lines[target].partition('=')
    ending = lines[target][len(lines[target].rstrip('\r\n')):]
    original = value.split()
    existing = [pattern for pattern in original if pattern not in LEGACY_PATTERNS]
    missing = [pattern for pattern in PATTERNS if pattern not in existing]
    if not missing and existing == original:
        return text
    lines[target] = f'{key.rstrip()} = {" ".join(existing + missing)}{ending}'
    return ''.join(lines)


def validate(text: str) -> None:
    with tempfile.TemporaryDirectory() as directory:
        candidate = Path(directory) / 'smb.conf'
        candidate.write_text(text, encoding='utf-8', newline='')
        run('testparm', '-s', str(candidate))


def install(text: str) -> None:
    temporary = CONFIG.with_name(CONFIG.name + '.medal-recycle-new')
    temporary.write_text(text, encoding='utf-8', newline='')
    temporary.chmod(0o644)
    os.replace(temporary, CONFIG)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--expected-config-sha256', required=True, help='SHA-256 of the reviewed live smb.conf')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if os.geteuid() != 0 or socket.gethostname() != 'file-storage':
        parser.error('Run as root inside file-storage CT 211')
    current = CONFIG.read_bytes()
    desired_text = add_exclusions(current.decode('utf-8'))
    desired = desired_text.encode('utf-8')
    current_hash, desired_hash = sha256(current), sha256(desired)
    # Even an already-applied configuration must match the reviewed hash.
    # For a retry, supply the printed desired hash as the expected live hash.
    if current_hash != args.expected_config_sha256:
        parser.error('Live Samba configuration changed; inspect it before applying')
    if desired == current:
        print('Already applied: deletions inside RecordingBuffer directories on Data are not recycled.')
        print('Config SHA256:', current_hash)
        return
    validate(desired_text)
    diff = difflib.unified_diff(current.decode('utf-8').splitlines(keepends=True), desired_text.splitlines(keepends=True),
                                str(CONFIG), str(CONFIG) + ' (new)')
    print(''.join(diff), end='')
    print('Current config SHA256:', current_hash)
    print('Desired config SHA256:', desired_hash)
    if not args.apply:
        print('Inspection only; nothing was changed. Add --apply to install it.')
        return
    backup = BACKUPS / datetime.now().strftime('%Y-%m-%d-%H%M') / 'medal-recycle'
    backup.mkdir(mode=0o700, parents=True, exist_ok=True)
    if (backup / 'smb.conf').exists():
        raise RuntimeError('Backup already exists for this minute; use a later minute')
    shutil.copy2(CONFIG, backup / 'smb.conf')
    try:
        install(desired_text)
        run('testparm', '-s')
        run('smbcontrol', 'all', 'reload-config')
        run('systemctl', 'is-active', 'smbd')
        shown = run('testparm', '-s', '--section-name', SECTION, '--parameter-name', PARAMETER).stdout
        if not all(pattern in shown.split() for pattern in PATTERNS):
            raise RuntimeError(f'testparm does not show the new {PARAMETER}: {shown.strip()}')
    except Exception:
        shutil.copy2(backup / 'smb.conf', CONFIG)
        run('smbcontrol', 'all', 'reload-config')
        raise
    print('Applied. Config backup:', backup)
    print('Connections opened before this change may keep the old setting until they reconnect.')
    print(f'Rollback: copy {backup / "smb.conf"} to {CONFIG}, run testparm -s, then smbcontrol all reload-config.')


if __name__ == '__main__':
    main()
