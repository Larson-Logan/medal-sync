"""Remove published beta releases superseded by a published stable release; retain all tags."""
import argparse
import json
import re
import subprocess
from urllib.parse import quote


NUMBER = r'(?:0|[1-9][0-9]*)'
VERSION = rf'{NUMBER}\.{NUMBER}\.{NUMBER}\.(?:0[0-9]|[1-9][0-9]+)'
STABLE = re.compile(rf'v({VERSION})\Z')
BETA = re.compile(rf'v({VERSION})-beta\.[1-9][0-9]*\Z')


def api(path, method='GET'):
    result = subprocess.run(['gh', 'api', '--method', method, path],
                            text=True, capture_output=True, check=True)
    return json.loads(result.stdout) if result.stdout.strip() else None


def version_numbers(version):
    return tuple(int(part) for part in version.split('.'))


def superseded(release, stable):
    match = BETA.fullmatch(release.get('tag_name', ''))
    return (match is not None and release.get('draft') is False
            and release.get('prerelease') is True and bool(release.get('published_at'))
            and version_numbers(match[1]) <= stable)


def cleanup(repository, released_tag, apply=False):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise ValueError('Repository must be owner/name.')
    match = STABLE.fullmatch(released_tag)
    if match is None:
        raise ValueError('The published stable tag must be vMAJOR.MINOR.PATCH.NN.')
    stable = version_numbers(match[1])
    prefix = f'repos/{repository}/releases'

    def require_published_stable():
        release = api(f'{prefix}/tags/{quote(released_tag, safe="")}')
        if (release.get('tag_name') != released_tag or release.get('draft') is not False
                or release.get('prerelease') is not False or not release.get('published_at')):
            raise ValueError('Cleanup requires the exact stable release to be fully published.')

    require_published_stable()
    # Snapshot every page before deleting; deletion during pagination would skip shifted entries.
    candidates = []
    page = 1
    while True:
        releases = api(f'{prefix}?per_page=100&page={page}')
        candidates.extend(release for release in releases if superseded(release, stable))
        if len(releases) < 100:
            break
        page += 1
    messages = []
    for release in candidates:
        tag, identity = release['tag_name'], release['id']
        if type(identity) is not int or identity <= 0:
            raise ValueError('Release has an invalid ID; nothing was deleted for it.')
        if not apply:
            messages.append(f'Would delete beta release {tag}; keep its tag.')
            continue
        # Recheck mutable release metadata immediately before deleting by ID.
        require_published_stable()
        current = api(f'{prefix}/{identity}')
        if current.get('tag_name') != tag or current.get('id') != identity or not superseded(current, stable):
            messages.append(f'Keep {tag}: release changed during cleanup.')
            continue
        api(f'{prefix}/{identity}', method='DELETE')
        messages.append(f'Deleted beta release {tag}; kept its tag.')
    return messages or ['No superseded beta releases. All tags were kept.']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', required=True, help='owner/name')
    parser.add_argument('--released-tag', required=True, help='Published vMAJOR.MINOR.PATCH.NN')
    parser.add_argument('--apply', action='store_true', help='Delete matching releases; default is dry run')
    args = parser.parse_args()
    for message in cleanup(args.repository, args.released_tag, args.apply):
        print(message)
