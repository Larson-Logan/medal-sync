"""Exercise the promotion check against disposable Git repositories."""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/check-promotion.py'
spec = importlib.util.spec_from_file_location('promotion', SCRIPT)
promotion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(promotion)

PROPS = '<Project>\n  <PropertyGroup>\n    <VersionPrefix>{}</VersionPrefix>\n  </PropertyGroup>\n</Project>\n'
SHIPPED = '## 1.3.1.05 — 2026-11-01\n\n- Shipped change.\n'
RELEASED = '# Changelog\n\n## Unreleased\n\n' + SHIPPED
# Without the runner's own Actions variables, so tests choose the output format
# and never write to the real job summary.
ENV = {**{k: v for k, v in os.environ.items() if k not in ('GITHUB_ACTIONS', 'GITHUB_STEP_SUMMARY')},
       'GIT_AUTHOR_NAME': 'Test', 'GIT_AUTHOR_EMAIL': 'test@example.invalid',
       'GIT_COMMITTER_NAME': 'Test', 'GIT_COMMITTER_EMAIL': 'test@example.invalid',
       'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_NOSYSTEM': '1'}


class PromotionCheckTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='promotion-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.git('init', '-q', '-b', 'main')
        self.commit('release: 1.3.1.05', prefix='1.3.1', changelog=RELEASED)
        for tag in ('v1.3.0', 'v1.3.0.01', 'v1.3.1.00', 'v1.3.1.04', 'v1.3.1.05'):
            self.git('tag', tag)
        self.git('checkout', '-q', '-b', 'dev')

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.root, env=ENV, check=True,
                              capture_output=True, text=True).stdout

    def commit(self, subject, prefix=None, changelog=None):
        if prefix is not None:
            (self.root / 'Directory.Build.props').write_text(PROPS.format(prefix), encoding='utf-8')
        if changelog is not None:
            (self.root / 'CHANGELOG.md').write_text(changelog, encoding='utf-8')
        self.git('add', '-A')
        self.git('commit', '-q', '--allow-empty', '-m', subject)

    def check(self):
        return promotion.check(self.root, 'main', 'dev')

    def heading(self, name):
        return f'# Changelog\n\n## Unreleased\n\n## {name} — 2026-11-06\n\n- New change.\n\n' + SHIPPED

    def assert_ready(self, version):
        self.assertEqual((version, []), self.check())

    def assert_refused(self, *phrases):
        version, errors = self.check()
        self.assertIsNone(version)
        message = '\n'.join(errors)
        for phrase in phrases:
            self.assertIn(phrase, message)
        return errors

    def test_fixes_publish_the_next_hotfix(self):
        self.commit('fix(storage): retry failed uploads (#9)', changelog=self.heading('1.3.1.06'))
        self.assert_ready('1.3.1.06')

    def test_features_need_a_minor_version(self):
        self.commit('fix: a fix (#8)')
        self.commit('feat(setup): add a field (#9)', changelog=self.heading('1.3.1.06'))
        errors = self.assert_refused('raise MINOR', 'set VersionPrefix to 1.4.0',
                                     '  - feat(setup): add a field (#9)')
        self.assertNotIn('fix: a fix', errors[0])

    def test_features_with_a_minor_version_and_heading_are_ready(self):
        self.commit('feat(setup): add a field (#9)', prefix='1.4.0', changelog=self.heading('1.4.0'))
        self.assert_ready('1.4.0.00')

    def test_a_new_version_heading_may_name_its_00(self):
        self.commit('feat: add a field (#9)', prefix='1.4.0', changelog=self.heading('1.4.0.00'))
        self.assert_ready('1.4.0.00')

    def test_a_major_version_also_covers_features(self):
        self.commit('feat: add a field (#9)', prefix='2.0.0', changelog=self.heading('2.0.0'))
        self.assert_ready('2.0.0.00')

    def test_breaking_changes_need_a_major_version(self):
        for subject in ('feat!: drop format 1 (#9)', 'fix(storage)!: rename a field (#9)'):
            with self.subTest(subject=subject):
                self.git('reset', '-q', '--hard', 'main')
                self.commit(subject, prefix='1.4.0', changelog=self.heading('1.4.0'))
                self.assert_refused('raise MAJOR', 'set VersionPrefix to 2.0.0', subject,
                                    '"## 2.0.0 — YYYY-MM-DD"')

    def test_untyped_and_merge_subjects_need_nothing(self):
        self.commit('Merge pull request #7 from example/feature', changelog=self.heading('1.3.1.06'))
        self.assert_ready('1.3.1.06')

    def test_entries_left_under_unreleased_are_refused(self):
        changelog = '# Changelog\n\n## Unreleased\n\n- New change.\n\n' + SHIPPED
        self.commit('fix: a fix (#9)', changelog=changelog)
        self.assert_refused('still has entries under ## Unreleased', '"## 1.3.1.06 — YYYY-MM-DD"')

    def test_the_heading_must_name_the_published_release(self):
        self.commit('fix: a fix (#9)', changelog=self.heading('1.3.1.05.1'))
        self.assert_refused('"## 1.3.1.05.1 — 2026-11-06"', 'publishes 1.3.1.06')

    def test_an_unchanged_changelog_is_refused(self):
        self.commit('ci: tidy a workflow (#9)')
        self.assert_refused('"## 1.3.1.05 — 2026-11-01"', 'publishes 1.3.1.06')

    def test_a_prepared_release_that_never_shipped_is_refused(self):
        # Prepared as hotfix 1.3.1.06, then a feature made the promotion 1.4.0.
        stale = '## 1.3.1.06 — 2026-11-05\n\n- Prepared fix.\n\n'
        changelog = '# Changelog\n\n## Unreleased\n\n## 1.4.0 — 2026-11-06\n\n- New change.\n\n' + stale + SHIPPED
        self.commit('feat(setup): add a field (#9)', prefix='1.4.0', changelog=changelog)
        self.assert_refused('"## 1.3.1.06 — 2026-11-05", which was never released', 'prepare-release.py')

    def test_the_heading_needs_a_date(self):
        changelog = '# Changelog\n\n## 1.3.1.06 — Unreleased\n\n- New change.\n\n' + SHIPPED
        self.commit('fix: a fix (#9)', changelog=changelog)
        self.assert_refused('"## 1.3.1.06 — Unreleased"')

    def test_the_heading_date_must_exist(self):
        changelog = '# Changelog\n\n## Unreleased\n\n## 1.3.1.06 — 2026-13-45\n\n- New change.\n\n' + SHIPPED
        self.commit('fix: a fix (#9)', changelog=changelog)
        self.assert_refused('"## 1.3.1.06 — 2026-13-45"', 'date that does not exist')

    def test_a_lower_version_is_refused(self):
        self.commit('fix: a fix (#9)', prefix='1.3.0', changelog=self.heading('1.3.0.02'))
        self.assert_refused('VersionPrefix 1.3.0 is lower than 1.3.1 on main.')

    def test_an_already_released_version_is_refused(self):
        self.git('tag', 'v1.4.0.00', 'main')
        self.commit('feat: add a field (#9)', prefix='1.4.0', changelog=self.heading('1.4.0'))
        self.assert_refused('VersionPrefix 1.4.0 is already released.')

    def test_bridge_tags_count_as_00_and_betas_never_count(self):
        self.assertEqual([0, 1], list(promotion.released_counters(
            (1, 3, 0), ['v1.3.0', 'v1.3.0.01', 'v1.3.0.02-beta.1', 'v1.3.01', 'v1.3.0.1', 'v1.3.00'])))
        self.assertEqual(('1.3.0.02', ('1.3.0.02',)), promotion.next_release(
            (1, 3, 0), (1, 3, 0), ['v1.3.0', 'v1.3.0.01', 'v1.3.0.02-beta.4']))
        self.assertEqual(('1.3.1.100', ('1.3.1.100',)),
                         promotion.next_release((1, 3, 1), (1, 3, 1), ['v1.3.1.99']))

    def test_the_command_reports_annotations_and_a_summary(self):
        self.commit('feat: add a field (#9)')
        summary = self.root / 'summary.md'
        result = subprocess.run(
            [sys.executable, str(SCRIPT), '--root', str(self.root), '--main', 'main', '--dev', 'dev'],
            env={**ENV, 'GITHUB_ACTIONS': 'true', 'GITHUB_STEP_SUMMARY': str(summary)},
            capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn('::error::This promotion includes new features', result.stdout)
        self.assertIn('Features:%0A  - feat: add a field (#9)', result.stdout)
        self.assertIn('- This promotion includes new features', summary.read_text(encoding='utf-8'))

        self.commit('release: prepare 1.4.0', prefix='1.4.0', changelog=self.heading('1.4.0'))
        result = subprocess.run(
            [sys.executable, str(SCRIPT), '--root', str(self.root), '--main', 'main', '--dev', 'dev'],
            env={**ENV, 'GITHUB_STEP_SUMMARY': str(summary)}, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual('This promotion publishes Medal Sync 1.4.0.00.\n', result.stdout)

    def test_a_missing_revision_is_reported(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), '--root', str(self.root), '--main', 'main', '--dev', 'nothing'],
            env=ENV, capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn('error: git show nothing:Directory.Build.props failed', result.stdout)


if __name__ == '__main__':
    unittest.main()
