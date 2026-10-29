"""Check public source; optionally audit all reachable Git trees and blob contents."""
from pathlib import Path, PurePosixPath
import argparse
import json
import re
import subprocess

root = Path(__file__).resolve().parents[1]

AGENT_DIRECTORIES = {'.agents', '.claude', '.codex', '.cursor', '.continue', '.agent-sync'}
AGENT_NAMES = {
    'agents.md', 'claude.md', 'claude.local.md', 'gemini.md', '.mcp.json',
    '.cursorrules', '.windsurfrules', '.clinerules', '.aider.conf.yml',
}
OWNER_PATHS = {
    '.graphifyignore', '.node-version', 'extraction-manifest.json', 'requirements-dev.txt',
    'docs/README.md', 'docs/agent-tools.md', 'docs/extraction.md',
    'docs/larson-drive-sync.md', 'docs/medal-sync.md', 'docs/public-readiness.md',
    'docs/topology.md', 'docs/usb-library-improvements.md', 'docs/usb-server-storage.md',
    'docs/usb-shared-storage.md', 'scripts/agent-context.mjs',
    'scripts/check-authored-doc-links.mjs', 'scripts/export-public.py',
    'scripts/graphify-server.py', 'scripts/setup-dev.ps1',
    'scripts/storage/medal-sync/README.md', 'scripts/storage/medal-sync/test-medal-production.ps1',
    'scripts/sync-agent-skills.py', 'scripts/test-graphify.py', 'scripts/update-graph.ps1',
    'tests/test-public-export.py', 'tools/medal-sync/README.md',
}
OWNER_PATHS = {name.casefold() for name in OWNER_PATHS}
PRIVATE_DIRECTORIES = {'wiki', '.validation', '.public-export', 'graphify-out',
                       '.agent-state', '.stversions', '.venv', 'node_modules', 'bin', 'obj',
                       'backups', 'medalcache', '__pycache__', '.medal-sync-dist'}
PRIVATE_EXTENSIONS = {'.exe', '.dll', '.msi', '.db', '.sqlite', '.sqlite3',
                      '.mp4', '.mkv', '.webm', '.log', '.key', '.pem', '.pfx', '.p12'}
PRIVATE_NAMES = {'.env', 'device.json', 'config.local.json', 'startup-originals.json',
                 'rclone.conf', '.stignore', '.stfolder'}
CREDENTIAL_PATTERNS = [
    r'-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----',
    r'gh[pousr]_[A-Za-z0-9]{30,}', r'github_pat_[A-Za-z0-9_]{30,}',
    r'xox[baprs]-[A-Za-z0-9-]{20,}', r'\bAKIA[A-Z0-9]{16}\b',
    r'\bAIza[A-Za-z0-9_-]{35}\b',
]
# Use documentation address ranges and placeholders in public examples.
# These generic checks contain no owner-specific addresses or account names.
DEPLOYMENT_PATTERNS = [
    r'\b192[.]168[.]\d{1,3}[.]\d{1,3}\b',
    r'\b10[.](?:\d{1,3}[.]){2}\d{1,3}\b',
    r'\b172[.](?:1[6-9]|2\d|3[01])[.]\d{1,3}[.]\d{1,3}\b',
]
PROFILE_PATTERN = r'[A-Za-z]:[\\/]+Users[\\/]+([A-Za-z0-9_.-]+)'
SYNTHETIC_PROFILES = {'public', 'default', 'someone', 'u', 'your-user',
                      'user', 'username', 'testuser', 'test', 'example'}


def private_agent_path(name):
    lowered = [part.casefold() for part in PurePosixPath(name.replace('\\', '/')).parts]
    normalized = '/'.join(lowered)
    return (bool(set(lowered) & AGENT_DIRECTORIES)
            or (lowered and lowered[-1] in AGENT_NAMES)
            or normalized.startswith('.github/instructions/')
            or normalized == '.github/copilot-instructions.md')


def path_failure(name):
    path = PurePosixPath(name.replace('\\', '/'))
    lowered = [part.casefold() for part in path.parts]
    normalized = '/'.join(lowered)
    if path.is_absolute() or '..' in path.parts or ':' in name:
        return 'Path outside repository'
    if private_agent_path(name):
        return 'Personal agent instructions/settings must remain untracked'
    if normalized in OWNER_PATHS or set(lowered) & PRIVATE_DIRECTORIES:
        return 'Private owner knowledge/tooling or generated data must remain untracked'
    filename = path.name.casefold()
    if (path.suffix.casefold() in PRIVATE_EXTENSIONS or filename in PRIVATE_NAMES
            or filename.endswith('.rclone.conf') or '.sync-conflict-' in filename
            or re.search(r'\.(?:db|sqlite\d?)-(?:wal|shm|journal)$', filename)
            or (filename.startswith('.env.') and filename != '.env.example')):
        return 'Runtime/private file'
    return None


def content_failures(data):
    text = data.decode('utf-8-sig', errors='replace')
    failures = []
    if any(re.search(pattern, text) for pattern in CREDENTIAL_PATTERNS):
        failures.append('Possible credential')
    personal_profile = any(match.group(1).casefold() not in SYNTHETIC_PROFILES
                           for match in re.finditer(PROFILE_PATTERN, text, re.IGNORECASE))
    if personal_profile or any(re.search(pattern, text, re.IGNORECASE) for pattern in DEPLOYMENT_PATTERNS):
        failures.append('Private deployment reference')
    return failures


def ruleset_failures():
    """Committed rulesets must name their branches and require checks CI reports."""
    folder = root / '.github/rulesets'
    workflow = root / '.github/workflows/validate.yml'
    if not folder.is_dir():
        return []
    text = workflow.read_text(encoding='utf-8') if workflow.is_file() else ''
    jobs = text.split('\njobs:\n', 1)[-1]
    checks = set(re.findall(r'^  ([A-Za-z0-9_-]+):\s*$', jobs, re.MULTILINE))
    checks |= {name.strip().strip('\'"') for name in re.findall(r'^    name: (.+)$', jobs, re.MULTILINE)}
    failures, names = [], set()
    for path in sorted(folder.glob('*.json')):
        where = f'.github/rulesets/{path.name}'
        ruleset = json.loads(path.read_text(encoding='utf-8'))
        if ruleset.get('name') in names:
            failures.append(f'Duplicate ruleset name in {where}')
        names.add(ruleset.get('name'))
        include = ruleset.get('conditions', {}).get('ref_name', {}).get('include', [])
        prefix = 'refs/tags/' if ruleset.get('target') == 'tag' else 'refs/heads/'
        if not include or any(not ref.startswith(prefix) for ref in include):
            # ~DEFAULT_BRANCH is dev, so a main ruleset written that way would protect the wrong branch.
            failures.append(f'Ruleset must include explicit {prefix} names: {where}')
        if ruleset.get('enforcement') != 'active':
            failures.append(f'Ruleset is not active: {where}')
        for rule in ruleset.get('rules', []):
            if rule.get('type') != 'required_status_checks':
                continue
            for check in rule['parameters']['required_status_checks']:
                if check['context'] not in checks:
                    failures.append(f'Required check {check["context"]!r} is not a validate.yml job: {where}')
    return failures


def git(*args, repository=root):
    return subprocess.check_output(['git', '-C', str(repository), *args])


def source_files():
    if (root / 'public-source.json').is_file() and not (root / '.git').exists():
        manifest = json.loads((root / 'public-source.json').read_text(encoding='utf-8'))
        if manifest['format'] != 2:
            raise ValueError('Unsupported public export manifest')
        return sorted([*manifest['files'], 'public-source.json'])
    return git('ls-files', '--cached', '--others', '--exclude-standard', '-z').decode().split('\0')


def history_failures(repository):
    if git('rev-parse', '--is-shallow-repository', repository=repository).strip() != b'false':
        raise SystemExit('History audit requires a full clone; fetch the complete history first.')
    commits = git('rev-list', '--all', repository=repository).decode().splitlines()
    failures, seen_entries, blobs = [], set(), {}
    for commit in commits:
        for reason in content_failures(git('cat-file', 'commit', commit, repository=repository)):
            failures.append(f'{reason} in commit metadata {commit[:12]}')
        entries = git('ls-tree', '-r', '-z', commit, repository=repository).split(b'\0')
        for entry in filter(None, entries):
            metadata, raw_name = entry.split(b'\t', 1)
            mode, kind, oid = metadata.decode().split()
            name = raw_name.decode('utf-8')
            key = (mode, oid, name)
            if key in seen_entries:
                continue
            seen_entries.add(key)
            reason = path_failure(name)
            if reason:
                failures.append(f'{reason}; remains in history {commit[:12]}: {name}')
            if mode == '120000' or kind != 'blob':
                failures.append(f'Link or submodule in history {commit[:12]}: {name}')
            elif oid not in blobs:
                blobs[oid] = f'{commit[:12]}:{name}'
    # Scan deleted/replaced content once per blob. Never echo credential matches.
    process = subprocess.run(['git', '-C', str(repository), 'cat-file', '--batch'],
                             input=''.join(oid + '\n' for oid in blobs).encode(),
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    offset = 0
    for oid, location in blobs.items():
        end = process.stdout.index(b'\n', offset)
        header = process.stdout[offset:end].split()
        if len(header) != 3 or header[0].decode() != oid or header[1] != b'blob':
            raise ValueError('Unexpected Git blob response')
        size = int(header[2])
        start = end + 1
        for reason in content_failures(process.stdout[start:start + size]):
            failures.append(f'{reason} in history {location}')
        offset = start + size + 1
    tags = git('for-each-ref', '--format=%(refname)', 'refs/tags', repository=repository).decode().splitlines()
    for tag in tags:
        for reason in content_failures(git('for-each-ref', '--format=%(contents)', tag, repository=repository)):
            failures.append(f'{reason} in tag metadata {tag}')
    return failures, len(commits), len(blobs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--list-docs', action='store_true')
    parser.add_argument('--history', action='store_true', help='Audit every reachable commit, path and blob')
    parser.add_argument('--history-repo', type=Path, help='Full clone or bare mirror to audit with --history')
    args = parser.parse_args()
    if args.history_repo and not args.history:
        parser.error('--history-repo requires --history')
    files = list(filter(None, source_files()))
    if args.list_docs:
        print('\n'.join(name for name in files if name.endswith('.md')))
        return
    failures = []
    for name in files:
        reason = path_failure(name)
        if reason:
            failures.append(f'{reason}: {name}')
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
        failures.extend(f'{reason} in {name}' for reason in content_failures(path.read_bytes()))
    json.loads((root / 'global.json').read_text(encoding='utf-8-sig'))
    failures.extend(ruleset_failures())
    if args.history:
        history, commits, blobs = history_failures(args.history_repo or root)
        failures.extend(history)
    if failures:
        raise SystemExit('\n'.join(failures))
    print('PASS: source hygiene and repository configuration (working tree)')
    if args.history:
        print(f'PASS: reachable history ({commits} commits, {blobs} unique blobs; paths, contents and metadata)')


if __name__ == '__main__':
    main()
