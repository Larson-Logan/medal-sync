"""Check that promoting dev to main releases the right version with a changelog.

Validation runs this on every pull request into main, which only dev opens:

    python scripts/check-promotion.py --main HEAD^1 --dev HEAD^2

Preview a promotion from a clone with current branches and release tags:

    git fetch origin main dev --tags
    python scripts/check-promotion.py --main origin/main --dev origin/dev

It fails when the promotion:

- includes a breaking change (a `type!:` title) without raising MAJOR, or a
  feature (a `feat:` title) without raising MINOR, compared with
  VersionPrefix on main;
- lowers VersionPrefix, or raises it to a version that is already released;
- still has entries under `## Unreleased` in CHANGELOG.md, or the changelog's
  newest release heading is not the release that this promotion publishes.

The version is chosen as the release workflow chooses it: `.00` when the
promotion changes VersionPrefix, otherwise one more than the highest hotfix
counter tagged for it. Only the standard library is used.
"""
import argparse
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
# The pull request title types; see .github/workflows/pull-request-title.yml.
TYPES = 'feat|fix|perf|docs|ci|build|deps|refactor|test|chore|release|revert'
TITLE = re.compile(rf'({TYPES})(\([a-z0-9._/-]+\))?(!)?: ')
PREFIX = re.compile(r'<VersionPrefix>([^<]*)</VersionPrefix>')
SEMVER = re.compile(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)')
RELEASE_HEADING = re.compile(r'(\S+) — ([0-9]{4}-[0-9]{2}-[0-9]{2})')


class PromotionError(Exception):
    pass


def git(root, *args):
    result = subprocess.run(['git', *args], cwd=root, capture_output=True, text=True)
    if result.returncode:
        raise PromotionError(f'git {" ".join(args)} failed: {result.stderr.strip()}')
    return result.stdout


def version_prefix(root, rev):
    match = PREFIX.search(git(root, 'show', f'{rev}:Directory.Build.props'))
    value = match.group(1) if match else ''
    if not SEMVER.fullmatch(value):
        raise PromotionError(f"VersionPrefix '{value}' at {rev} is not MAJOR.MINOR.PATCH.")
    return tuple(int(part) for part in value.split('.'))


def text(version):
    return '.'.join(str(part) for part in version)


def released_counters(prefix, tags):
    """Hotfix counters tagged for prefix. A three-part bridge tag counts as 00."""
    stable = f'v{text(prefix)}'
    for tag in tags:
        if tag == stable:
            yield 0
        elif tag.startswith(stable + '.') and re.fullmatch(r'[0-9]{2,}', tag[len(stable) + 1:]):
            yield int(tag[len(stable) + 1:])


def next_release(prefix, previous, tags):
    """The release this promotion publishes, and the headings that name it."""
    used = list(released_counters(prefix, tags))
    if prefix != previous:
        if used:
            raise PromotionError(
                f'VersionPrefix {text(prefix)} is already released. Raise it to a version '
                'that has no release yet.')
        counter = 0
    else:
        counter = max(used) + 1 if used else 0
    version = f'{text(prefix)}.{counter:02d}'
    # A new version's heading may leave out its .00, as 1.3.1 did.
    names = (text(prefix), version) if counter == 0 else (version,)
    return version, names


def promoted_titles(root, main, dev):
    """Typed commit subjects on dev that main does not have yet."""
    log = git(root, 'log', '--no-merges', '--format=%s', f'{main}..{dev}')
    for subject in log.splitlines():
        match = TITLE.match(subject)
        if match:
            yield match.group(1), bool(match.group(3)), subject


def version_errors(prefix, previous, titles):
    """Reasons VersionPrefix is wrong, and the lowest version that would do."""
    breaking = [subject for _, bang, subject in titles if bang]
    features = [subject for kind, bang, subject in titles if kind == 'feat' and not bang]
    if prefix < previous:
        return [f'VersionPrefix {text(prefix)} is lower than {text(previous)} on main.'], previous
    if breaking and prefix[0] <= previous[0]:
        required = (previous[0] + 1, 0, 0)
        return [f'This promotion includes breaking changes, so raise MAJOR: set VersionPrefix '
                f'to {text(required)} on dev. Breaking:' + listed(breaking)], required
    if features and prefix[:2] <= previous[:2]:
        required = (previous[0], previous[1] + 1, 0)
        return [f'This promotion includes new features, so raise MINOR: set VersionPrefix '
                f'to {text(required)} on dev. Features:' + listed(features)], required
    return [], prefix


def listed(subjects):
    return ''.join(f'\n  - {subject}' for subject in subjects)


def changelog_errors(changelog, version, names):
    lines = changelog.splitlines()
    headings = [(index, line[3:].strip()) for index, line in enumerate(lines) if line.startswith('## ')]
    expected = f'## {names[0]} — YYYY-MM-DD'
    for position, (index, name) in enumerate(headings):
        if name == 'Unreleased':
            end = headings[position + 1][0] if position + 1 < len(headings) else len(lines)
            if any(line.strip() for line in lines[index + 1:end]):
                return [f'CHANGELOG.md still has entries under ## Unreleased. Rename that heading '
                        f'on dev to "{expected}" and start a new empty ## Unreleased above it.']
            continue
        match = RELEASE_HEADING.fullmatch(name)
        if not match or match.group(1) not in names:
            return [f'The newest release heading in CHANGELOG.md is "## {name}", but this '
                    f'promotion publishes {version}. Add "{expected}" on dev above it with '
                    'what this release changes.']
        return []
    return [f'CHANGELOG.md has no release heading. Add "{expected}" on dev.']


def check(root, main, dev):
    """Return the version this promotion publishes and any reasons it cannot."""
    prefix = version_prefix(root, dev)
    previous = version_prefix(root, main)
    tags = git(root, 'tag', '--list', 'v*').split()
    titles = list(promoted_titles(root, main, dev))
    # With a wrong VersionPrefix, name the changelog heading of the version it needs.
    errors, prefix = version_errors(prefix, previous, titles)
    try:
        version, names = next_release(prefix, previous, tags)
    except PromotionError as error:
        return None, errors + [str(error)]
    errors += changelog_errors(git(root, 'show', f'{dev}:CHANGELOG.md'), version, names)
    return (None if errors else version), errors


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    parser.add_argument('--main', default='HEAD^1', help='current main commit (default: HEAD^1)')
    parser.add_argument('--dev', default='HEAD^2', help='dev commit being promoted (default: HEAD^2)')
    parser.add_argument('--root', default=str(ROOT), help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    try:
        version, errors = check(args.root, args.main, args.dev)
    except PromotionError as error:
        version, errors = None, [str(error)]
    actions = os.environ.get('GITHUB_ACTIONS') == 'true'
    for error in errors:
        # An annotation is one line; %0A is its escaped line break.
        print(f'::error::{error.replace(chr(10), "%0A")}' if actions else f'error: {error}')
    if errors:
        summary = 'This promotion is not ready to release:\n\n' + ''.join(f'- {e}\n' for e in errors)
    else:
        summary = f'This promotion publishes Medal Sync {version}.\n'
        print(summary, end='')
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as handle:
            handle.write(summary)
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
