"""Refresh or verify the public source inventory using only Git-visible safe files.

The inventory lists file names only, so it changes only when files are added,
removed or renamed. Git already content-addresses every file, and release
assets carry their own SHA-256 sidecars and attestations.
"""
from pathlib import Path
import argparse
import json
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Fail if the inventory is stale')
    args = parser.parse_args()
    subprocess.run([sys.executable, str(ROOT / 'scripts/check-repository.py')], cwd=ROOT, check=True)
    names = subprocess.check_output(
        ['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT,
    ).decode('utf-8').split('\0')
    files = sorted({name for name in names if name and name != 'public-source.json'})
    manifest = {'format': 1, 'files': files}
    expected = json.dumps(manifest, indent=2, ensure_ascii=False) + '\n'
    target = ROOT / 'public-source.json'
    if args.check:
        if target.read_text(encoding='utf-8') != expected:
            raise SystemExit('Public source manifest is stale. Run python scripts/update-source-manifest.py.')
        print(f'PASS: public source inventory ({len(files)} files)')
    else:
        target.write_text(expected, encoding='utf-8', newline='\n')
        print(f'Updated public source inventory ({len(files)} files)')


if __name__ == '__main__':
    main()
