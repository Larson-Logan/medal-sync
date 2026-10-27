"""Check source hygiene without reading ignored runtime data or Git history."""
from pathlib import Path
import argparse
import json
import re
import subprocess

root = Path(__file__).resolve().parents[1]


def private_agent_path(name):
    parts = Path(name.replace('\\', '/')).parts
    lowered = [part.casefold() for part in parts]
    return (bool(set(lowered) & {'.agents', '.claude', '.codex', '.cursor', '.continue'})
            or (lowered and lowered[-1] in {
                'agents.md', 'claude.md', 'claude.local.md', 'gemini.md', '.mcp.json',
                '.cursorrules', '.windsurfrules', '.clinerules', '.aider.conf.yml'})
            or name.replace('\\', '/').casefold().startswith('.github/instructions/')
            or name.replace('\\', '/').casefold() == '.github/copilot-instructions.md')


def source_files():
    if (root / 'public-source.json').is_file() and not (root / '.git').exists():
        manifest = json.loads((root / 'public-source.json').read_text(encoding='utf-8'))
        if manifest['format'] != 1:
            raise ValueError('Unsupported public export manifest')
        return sorted(manifest['files'] + ['public-source.json'])
    return subprocess.check_output(
        ['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'],
        cwd=root,
    ).decode().split('\0')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--list-docs', action='store_true')
    parser.add_argument('--history', action='store_true', help='Also reject private agent paths in reachable Git history')
    args = parser.parse_args()
    files = list(filter(None, source_files()))
    if args.list_docs:
        print('\n'.join(name for name in files if name.endswith('.md')))
        return
    failures = []
    patterns = [r'-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----',
                r'gh[pousr]_[A-Za-z0-9]{30,}', r'github_pat_[A-Za-z0-9_]{30,}']
    private_extensions = {'.exe', '.dll', '.msi', '.db', '.sqlite', '.sqlite3',
                          '.mp4', '.mkv', '.webm', '.log', '.key', '.pem', '.pfx', '.p12'}
    private_names = {'.env', 'device.json', 'config.local.json', 'startup-originals.json'}
    for name in files:
        if private_agent_path(name):
            failures.append(f'Personal agent instructions/settings must remain untracked: {name}')
        path = root / name
        if not path.resolve().is_relative_to(root):
            failures.append(f'Path outside repository: {name}')
            continue
        if path.is_symlink():
            failures.append(f'Symbolic link in source: {name}')
            continue
        if not path.is_file():
            failures.append(f'Missing source file: {name}')
            continue
        if (path.suffix.lower() in private_extensions or path.name in private_names
                or re.search(r'\.(?:db|sqlite\d?)-(?:wal|shm|journal)$', path.name)
                or (path.name.startswith('.env.') and path.name != '.env.example')):
            failures.append(f'Runtime/private file: {name}')
        text = path.read_text(encoding='utf-8-sig', errors='replace')
        if any(re.search(pattern, text) for pattern in patterns):
            failures.append(f'Possible credential in {name}')
    json.loads((root / 'global.json').read_text(encoding='utf-8-sig'))
    if args.history:
        commits = subprocess.check_output(['git', 'rev-list', '--all'], cwd=root, text=True).splitlines()
        for commit in commits:
            paths = subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', '-z', commit], cwd=root).decode().split('\0')
            for name in filter(None, paths):
                if private_agent_path(name):
                    failures.append(f'Personal agent path remains in history {commit[:12]}: {name}')
    if failures:
        raise SystemExit('\n'.join(failures))
    print('PASS: source hygiene and repository configuration (working tree only)')


if __name__ == '__main__':
    main()
