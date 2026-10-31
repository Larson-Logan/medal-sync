"""Delete an unchanged same-repository branch after its pull request closes."""
import argparse
import json
import subprocess
from urllib.parse import quote


def api(path, method='GET', missing_ok=False):
    result = subprocess.run(['gh', 'api', '--method', method, path],
                            text=True, capture_output=True)
    if result.returncode:
        if missing_ok and '(HTTP 404)' in result.stderr:
            return None
        raise RuntimeError(f'GitHub API {method} {path} failed: {result.stderr.strip()}')
    return json.loads(result.stdout) if result.stdout.strip() else None


def open_pulls(repository):
    page = 1
    while True:
        pulls = api(f'repos/{repository}/pulls?state=open&per_page=100&page={page}')
        yield from pulls
        if len(pulls) < 100:
            return
        page += 1


def delete_branch(clone_url, name, sha):
    # The lease makes the final head check atomic with deletion. A concurrent
    # push refuses instead of deleting its new commits. gh supplies credentials
    # through the helper, never through a command-line token or stored config.
    subprocess.run(['git', '-c', 'credential.helper=',
                    '-c', 'credential.helper=!gh auth git-credential',
                    'push', f'--force-with-lease=refs/heads/{name}:{sha}',
                    clone_url, f':refs/heads/{name}'], check=True)


def cleanup(repository, number, apply=False):
    prefix = f'repos/{repository}'
    pull = api(f'{prefix}/pulls/{number}')
    if pull['state'] != 'closed':
        return 'Keep: pull request is open.'
    head = pull['head']
    if (head.get('repo') or {}).get('full_name') != repository:
        return 'Keep: branch belongs to a fork or a deleted repository.'
    name = head['ref']
    settings = api(prefix)
    if name in {'main', 'dev', settings['default_branch']}:
        return 'Keep: long-lived branch.'
    path = f'{prefix}/branches/{quote(name, safe="")}'
    branch = api(path, missing_ok=True)
    if branch is None:
        return 'Already deleted.'
    if branch['protected']:
        return 'Keep: protected branch.'
    if branch['commit']['sha'] != head['sha']:
        return 'Keep: branch changed since this pull request closed.'
    for other in open_pulls(repository):
        same_head = ((other['head'].get('repo') or {}).get('full_name') == repository
                     and other['head']['ref'] == name)
        if same_head or other['base']['ref'] == name:
            return f'Keep: open pull request #{other["number"]} uses this branch.'
    if not apply:
        return f'Would delete {name} at {head["sha"]} (PR #{number}).'
    # Recheck after pagination so a reopened PR or a newly pushed head refuses.
    latest = api(f'{prefix}/pulls/{number}')
    current = api(path, missing_ok=True)
    if current is None:
        return 'Already deleted.'
    if (latest['state'] != 'closed' or latest['head']['sha'] != head['sha']
            or current['commit']['sha'] != head['sha'] or current['protected']):
        return 'Keep: pull request or branch changed during cleanup.'
    delete_branch(settings['clone_url'], name, head['sha'])
    return f'Deleted {name} at {head["sha"]} (PR #{number}).'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', required=True, help='owner/name')
    parser.add_argument('--pr', required=True, type=int)
    parser.add_argument('--apply', action='store_true', help='Delete; otherwise only report')
    args = parser.parse_args()
    print(cleanup(args.repository, args.pr, args.apply))
