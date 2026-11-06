"""Prepare dev for its next promotion to main.

Run it from a clone with current branches and tags, on a branch from dev; it
changes the checked-out files and compares the checked-out commit with main:

    git fetch origin main dev --tags
    git switch -c release/prepare origin/dev
    python scripts/prepare-release.py --dry-run   # show the plan only
    python scripts/prepare-release.py             # update the two files

It chooses the release the same way as the promotion check
(scripts/check-promotion.py), from the typed commit subjects that dev has and
main does not:

- a breaking change (a `type!:` title) raises MAJOR, and a feature (a `feat:`
  title) raises MINOR, unless VersionPrefix in Directory.Build.props is
  already raised far enough;
- otherwise the promotion is the next hotfix of the current version.

It then renames `## Unreleased` in CHANGELOG.md to the release heading, such
as `## 1.5.0 — 2026-11-20` or `## 1.4.0.01 — 2026-11-20`, and starts a new
empty `## Unreleased` above it. A section prepared earlier for a release that
never shipped, such as a hotfix that a later feature turned into a new minor
version, folds into that heading. Commit the result, open a pull request to dev
titled with the printed title, and promote dev to main once it merges. The
printed summary lists the promoted pull requests for the promotion's
description. Only the standard library is used.
"""
import argparse
import datetime
import importlib.util
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
_spec = importlib.util.spec_from_file_location('check_promotion', Path(__file__).with_name('check-promotion.py'))
promotion = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(promotion)
PromotionError = promotion.PromotionError

PLACEHOLDER = '- Internal changes only; see the release notes.'


def plan(root, main, date, dev='HEAD'):
    """The release dev's next promotion publishes, and the file changes it needs."""
    previous = promotion.version_prefix(root, main)
    props_path = Path(root) / 'Directory.Build.props'
    props = original_props = props_path.read_text(encoding='utf-8')
    match = promotion.PREFIX.search(props)
    current = match.group(1) if match else ''
    if not promotion.SEMVER.fullmatch(current):
        raise PromotionError(f"VersionPrefix '{current}' in Directory.Build.props is not MAJOR.MINOR.PATCH.")
    current = tuple(int(part) for part in current.split('.'))
    if not promotion.git(root, 'rev-list', '--max-count=1', f'{main}..{dev}').strip():
        raise PromotionError(f'{dev} has nothing that {main} does not have, so there is nothing to release.')
    titles = list(promotion.promoted_titles(root, main, dev))
    errors, prefix = promotion.version_errors(current, previous, titles)
    if current < previous:
        raise PromotionError(errors[0])
    tags = promotion.git(root, 'tag', '--list', 'v*').split()
    version, names = promotion.next_release(prefix, previous, tags)

    changelog_path = Path(root) / 'CHANGELOG.md'
    changelog = changelog_path.read_text(encoding='utf-8')
    updated = dated_changelog(changelog, names, date, promotion.newest_released(tags))
    if prefix != current:
        props = promotion.PREFIX.sub(f'<VersionPrefix>{promotion.text(prefix)}</VersionPrefix>', props, count=1)
    return {
        'version': version, 'name': names[0], 'previous': promotion.text(previous),
        'current': promotion.text(current), 'prefix': promotion.text(prefix), 'titles': titles,
        'changes': {path: content for path, content, original in (
            (props_path, props, original_props), (changelog_path, updated, changelog)) if content != original},
    }


def dated_changelog(changelog, names, date, released):
    """CHANGELOG.md with its Unreleased entries under the release heading.

    Sections right below Unreleased that were prepared for a release that
    never shipped, such as a hotfix overtaken by a feature, fold into the new
    heading too, so nothing is left under a version nobody can install.
    """
    lines = changelog.splitlines(keepends=True)
    headings = [index for index, line in enumerate(lines) if line.startswith('## ')]
    unreleased = next((index for index in headings if lines[index].strip() == '## Unreleased'), None)
    if unreleased is None:
        raise PromotionError('CHANGELOG.md has no ## Unreleased heading.')
    bounds = [index for index in headings if index > unreleased] + [len(lines)]
    sections = [(unreleased, bounds[0], None)]
    for start, end in zip(bounds, bounds[1:]):
        heading = promotion.RELEASE_HEADING.fullmatch(lines[start][3:].strip())
        key = heading and promotion.release_key(heading.group(1))
        if not key or not (heading.group(1) in names or (released is not None and key > released)):
            break
        sections.append((start, end, heading.group(1)))
    entries = [line for start, end, _ in sections for line in lines[start + 1:end]]
    if len(sections) == 2 and sections[1][2] in names and not any(
            line.strip() for line in lines[unreleased + 1:bounds[0]]):
        return changelog  # Already prepared.
    body = ''.join(line for line in entries if line.strip() != PLACEHOLDER).strip('\n')
    body = re.sub(r'\n{3,}', '\n\n', body) or PLACEHOLDER
    release = f'## Unreleased\n\n## {names[0]} — {date}\n\n{body}\n'
    rest = sections[-1][1]
    return ''.join(lines[:unreleased]) + release + ('\n' + ''.join(lines[rest:]) if rest < len(lines) else '')


def grouped(titles):
    """Promoted subjects by kind, oldest first, as release notes group them."""
    groups = {'Breaking changes': [], 'Features': [], 'Fixes and performance': [], 'Other changes': []}
    for kind, bang, subject in reversed(titles):
        group = ('Breaking changes' if bang else 'Features' if kind == 'feat'
                 else 'Fixes and performance' if kind in ('fix', 'perf') else 'Other changes')
        groups[group].append(subject)
    return {group: subjects for group, subjects in groups.items() if subjects}


def summary(result):
    if result['prefix'] != result['current']:
        reason = f'VersionPrefix {result["current"]} -> {result["prefix"]}'
    elif result['prefix'] != result['previous']:
        reason = f'VersionPrefix already raised from {result["previous"]} to {result["prefix"]}'
    else:
        reason = f'hotfix of {result["prefix"]}'
    lines = [f'Next release: Medal Sync {result["version"]} ({reason}).']
    if result['changes']:
        lines.append('Changes: ' + ', '.join(path.name for path in result['changes']) + '.')
        lines.append(f'Pull request title: release: prepare {result["name"]}')
    else:
        lines.append('Directory.Build.props and CHANGELOG.md are already prepared.')
    groups = grouped(result['titles'])
    if groups:
        lines += ['', 'Promoted pull requests:']
        for group, subjects in groups.items():
            lines.append(f'{group}:')
            lines += [f'- {subject}' for subject in subjects]
    return '\n'.join(lines) + '\n'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n', 1)[0])
    parser.add_argument('--main', default='origin/main', help='current main commit (default: origin/main)')
    parser.add_argument('--date', default=datetime.datetime.now(datetime.timezone.utc).date().isoformat(),
                        help='release date for the changelog heading (default: today, UTC)')
    parser.add_argument('--dry-run', action='store_true', help='print the plan without changing files')
    parser.add_argument('--root', default=str(ROOT), help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if not promotion.is_date(args.date):
        parser.error('--date must be a real date written as YYYY-MM-DD.')
    try:
        result = plan(args.root, args.main, args.date)
    except PromotionError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1
    if not args.dry_run:
        for path, content in result['changes'].items():
            path.write_text(content, encoding='utf-8', newline='\n')
    print(summary(result), end='')
    return 0


if __name__ == '__main__':
    sys.exit(main())
