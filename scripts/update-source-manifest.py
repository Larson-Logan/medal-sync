"""Refresh, verify or query the public source inventory.

The inventory lists every Git-visible safe file and the Windows CI suites that
must run when it changes. Validation reads it to skip suites a pull request
cannot affect, so a change to a document or a unit test does not wait for the
installer and storage suites. It changes only when files are added, removed or
renamed, or when the rules below change. Git already content-addresses every
file, and release assets carry their own SHA-256 sidecars and attestations.

    python scripts/update-source-manifest.py                     # refresh
    python scripts/update-source-manifest.py --check             # verify
    python scripts/update-source-manifest.py --affected origin/dev  # suites a change needs
"""
from pathlib import Path
import argparse
import fnmatch
import json
import os
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = 'public-source.json'
FORMAT = 2

# Each suite is one Windows job in .github/workflows/validate.yml. The Linux
# repository checks always run, so files they alone cover need no suite.
SUITES = {
    'unit': '.NET unit tests (scripts/validate.ps1 -Part Unit)',
    'desktop': 'Launcher build, desktop and script tests, packaging and native installer',
    'storage': 'Real rclone and WinFsp storage integration',
}
ALL = list(SUITES)

# First match wins. A pattern ending in / covers that folder. Any file no rule
# names needs every suite, so a new kind of file is never skipped by accident.
RULES = [
    # Documentation, GitHub settings and repository tooling: checked by the
    # Linux job only. docs/setup.html, LICENSE and THIRD_PARTY.md ship in the
    # installer, so they are deliberately not here.
    (['README.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md',
      'SECURITY.md', 'SUPPORT.md', 'docs/*.md', 'tests/README.md',
      '.github/ISSUE_TEMPLATE/', '.github/PULL_REQUEST_TEMPLATE.md',
      '.github/labels.json', '.github/project.json', '.github/release.yml',
      '.github/dependabot.yml', '.github/rulesets/',
      '.github/workflows/milestones.yml', '.github/workflows/release.yml',
      '.github/workflows/branch-cleanup.yml', 'scripts/cleanup-pr-branch.py',
      'scripts/check-repository.py', 'scripts/check-doc-links.mjs',
      'scripts/update-source-manifest.py', 'scripts/generate-icon.py',
      'scripts/setup-github-project.ps1', 'scripts/setup-github-rulesets.ps1',
      'tests/test_*.py'], []),
    # The shared Medal fixture is compiled into the integration program too.
    (['tests/tools/medal-sync/Fixtures/'], ALL),
    (['tests/tools/medal-sync/'], ['unit']),
    (['tests/integration/', 'scripts/setup-storage-toolchain.ps1'], ['desktop', 'storage']),
    (['scripts/test-storage-integration.ps1'], ['storage']),
    (['tools/medal-sync/MedalSync.Launch/', 'tests/desktop/', 'tests/scripts/',
      'tests/fixtures/', 'scripts/storage/', 'installer/', 'docs/setup.html',
      'scripts/package-release.ps1', 'scripts/package-windows-installer.ps1',
      'scripts/setup-installer-toolchain.ps1', 'scripts/test-windows-installer.ps1',
      'scripts/test-windows-installer-isolated.ps1', 'LICENSE', 'THIRD_PARTY.md'], ['desktop']),
    # Everything else, including the core program, its configuration, the SDK
    # pin, shared build properties and validate.yml itself, needs every suite.
]


def matches(name, pattern):
    if pattern.endswith('/'):
        return name.startswith(pattern)
    # A wildcard never crosses a folder boundary.
    return name.count('/') == pattern.count('/') and fnmatch.fnmatchcase(name, pattern)


def suites_for(name):
    for patterns, suites in RULES:
        if any(matches(name, pattern) for pattern in patterns):
            return list(suites)
    return list(ALL)


def tracked_files():
    names = subprocess.check_output(
        ['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT,
    ).decode('utf-8').split('\0')
    return sorted({name for name in names if name and name != MANIFEST})


def render(files):
    # One file per line keeps diffs readable and the file easy to search.
    lines = [f'    {json.dumps(name, ensure_ascii=False)}: {json.dumps(suites_for(name))}' for name in files]
    suites = [f'    {json.dumps(key)}: {json.dumps(value)}' for key, value in SUITES.items()]
    return ('{\n'
            f'  "format": {FORMAT},\n'
            '  "suites": {\n' + ',\n'.join(suites) + '\n  },\n'
            '  "files": {\n' + ',\n'.join(lines) + '\n  }\n'
            '}\n')


def load_manifest():
    manifest = json.loads((ROOT / MANIFEST).read_text(encoding='utf-8'))
    if manifest.get('format') != FORMAT:
        raise SystemExit(f'{MANIFEST} is not format {FORMAT}. Run python scripts/update-source-manifest.py.')
    return manifest


def affected(base):
    """Suites needed by the change from BASE to HEAD, read from the inventory."""
    manifest = load_manifest()
    changed = subprocess.check_output(
        ['git', 'diff', '--name-only', '--no-renames', '-z', base, 'HEAD'], cwd=ROOT,
    ).decode('utf-8').split('\0')
    reasons = {suite: [] for suite in SUITES}
    for name in filter(None, changed):
        if name == MANIFEST:
            continue
        # A removed file is no longer listed, so classify it by the same rules.
        for suite in manifest['files'].get(name, suites_for(name)):
            if suite not in reasons:
                raise SystemExit(f'{MANIFEST} names unknown suite {suite!r} for {name}.')
            reasons[suite].append(name)
    return reasons


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--check', action='store_true', help='Fail if the inventory is stale')
    mode.add_argument('--affected', metavar='BASE',
                      help='Print suite=true|false for the change from BASE to HEAD')
    args = parser.parse_args()

    if args.affected:
        reasons = affected(args.affected)
        summary = os.environ.get('GITHUB_STEP_SUMMARY')
        report = ['| Suite | Runs | Because of |', '| --- | --- | --- |']
        for suite, names in reasons.items():
            print(f'{suite}={"true" if names else "false"}')
            more = f' and {len(names) - 3} more' if len(names) > 3 else ''
            report.append(f'| {suite} | {"yes" if names else "skipped"} | '
                          f'{", ".join(f"`{name}`" for name in names[:3]) + more or "no affected files"} |')
            print(f'  {suite}: {", ".join(names[:3]) + more or "skipped"}', file=sys.stderr)
        if summary:
            with open(summary, 'a', encoding='utf-8') as handle:
                handle.write('Windows suites chosen from `public-source.json`:\n\n' + '\n'.join(report) + '\n')
        return

    subprocess.run([sys.executable, str(ROOT / 'scripts/check-repository.py')], cwd=ROOT, check=True)
    files = tracked_files()
    expected = render(files)
    json.loads(expected)
    target = ROOT / MANIFEST
    if args.check:
        current = target.read_text(encoding='utf-8') if target.is_file() else ''
        if current != expected:
            raise SystemExit('Public source manifest is stale. Run python scripts/update-source-manifest.py.')
        print(f'PASS: public source inventory ({len(files)} files)')
    else:
        target.write_text(expected, encoding='utf-8', newline='\n')
        print(f'Updated public source inventory ({len(files)} files)')


if __name__ == '__main__':
    main()
