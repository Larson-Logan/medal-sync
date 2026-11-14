"""Exercise the release preparation script against disposable Git repositories."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


prepare = load('prepare_release', 'scripts/prepare-release.py')
promotion = load('check_promotion', 'scripts/check-promotion.py')

PROPS = '<Project>\n  <PropertyGroup>\n    <VersionPrefix>{}</VersionPrefix>\n  </PropertyGroup>\n</Project>\n'
SHIPPED = '## 1.3.1.05 — 2026-11-01\n\n- Shipped change.\n'
RELEASED = '# Changelog\n\n## Unreleased\n\n' + SHIPPED
ENV = {**{k: v for k, v in os.environ.items() if k not in ('GITHUB_ACTIONS', 'GITHUB_STEP_SUMMARY')},
       'GIT_AUTHOR_NAME': 'Test', 'GIT_AUTHOR_EMAIL': 'test@example.invalid',
       'GIT_COMMITTER_NAME': 'Test', 'GIT_COMMITTER_EMAIL': 'test@example.invalid',
       'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_NOSYSTEM': '1'}


class PrepareReleaseTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='prepare-release-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.git('init', '-q', '-b', 'main')
        self.commit('release: 1.3.1.05', prefix='1.3.1', changelog=RELEASED)
        for tag in ('v1.3.0', 'v1.3.1.00', 'v1.3.1.05', 'v1.3.1.06-beta.2'):
            self.git('tag', tag)
        self.git('checkout', '-q', '-b', 'dev')

    def git(self, *args):
        return subprocess.run(['git', *args], cwd=self.root, env=ENV, check=True,
                              capture_output=True, text=True).stdout

    def commit(self, subject, prefix=None, changelog=None, entry=None):
        if prefix is not None:
            (self.root / 'Directory.Build.props').write_text(PROPS.format(prefix), encoding='utf-8')
        if changelog is not None:
            (self.root / 'CHANGELOG.md').write_text(changelog, encoding='utf-8')
        if entry is not None:
            path = self.root / 'CHANGELOG.md'
            path.write_text(path.read_text(encoding='utf-8').replace(
                '## Unreleased\n\n', f'## Unreleased\n\n- {entry}\n', 1), encoding='utf-8')
        self.git('add', '-A')
        self.git('commit', '-q', '--allow-empty', '-m', subject)

    def run_script(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = prepare.main(['--root', str(self.root), '--main', 'main', '--date', '2026-11-20', *args])
        return code, out.getvalue(), err.getvalue()

    def prepared(self, *args):
        code, out, err = self.run_script(*args)
        self.assertEqual(0, code, out + err)
        return out

    def files(self):
        return ((self.root / 'Directory.Build.props').read_text(encoding='utf-8'),
                (self.root / 'CHANGELOG.md').read_text(encoding='utf-8'))

    def promote(self):
        """Commit the prepared files and run the real promotion check against them."""
        self.commit('release: prepare')
        return promotion.check(str(self.root), 'main', 'dev')

    def test_a_feature_raises_minor_and_dates_the_changelog(self):
        self.commit('feat(sync): add a thing (#1)', entry='Add a thing.')
        self.commit('fix: repair a thing (#2)', entry='Repair a thing.')
        out = self.prepared()
        self.assertIn('Next release: Medal Sync 1.4.0.00 (VersionPrefix 1.3.1 -> 1.4.0).', out)
        self.assertIn('Pull request title: release: prepare 1.4.0', out)
        self.assertIn('Features:\n- feat(sync): add a thing (#1)\nFixes and performance:\n- fix: repair a thing (#2)', out)
        props, changelog = self.files()
        self.assertIn('<VersionPrefix>1.4.0</VersionPrefix>', props)
        self.assertEqual('# Changelog\n\n## Unreleased\n\n## 1.4.0 — 2026-11-20\n\n'
                         '- Repair a thing.\n- Add a thing.\n\n' + SHIPPED, changelog)
        self.assertEqual(('1.4.0.00', []), self.promote())

    def test_a_breaking_change_raises_major(self):
        self.commit('feat(config)!: drop an old option (#1)', entry='Drop an old option.')
        self.assertIn('Medal Sync 2.0.0.00', self.prepared())
        self.assertIn('<VersionPrefix>2.0.0</VersionPrefix>', self.files()[0])
        self.assertIn('## 2.0.0 — 2026-11-20', self.files()[1])
        self.assertEqual(('2.0.0.00', []), self.promote())

    def test_fixes_only_are_the_next_hotfix(self):
        self.commit('fix: repair a thing (#1)', entry='Repair a thing.')
        out = self.prepared()
        self.assertIn('Medal Sync 1.3.1.06 (hotfix of 1.3.1)', out)
        self.assertIn('Changes: CHANGELOG.md.', out)
        props, changelog = self.files()
        self.assertIn('<VersionPrefix>1.3.1</VersionPrefix>', props)
        self.assertIn('## Unreleased\n\n## 1.3.1.06 — 2026-11-20\n\n- Repair a thing.\n\n## 1.3.1.05', changelog)
        self.assertEqual(('1.3.1.06', []), self.promote())

    def test_keeps_a_prefix_that_is_already_raised_far_enough(self):
        self.commit('release: start 1.5.0', prefix='1.5.0')
        self.commit('feat: add a thing (#1)', entry='Add a thing.')
        out = self.prepared()
        self.assertIn('Medal Sync 1.5.0.00 (VersionPrefix already raised from 1.3.1 to 1.5.0).', out)
        self.assertIn('<VersionPrefix>1.5.0</VersionPrefix>', self.files()[0])
        self.assertIn('## 1.5.0 — 2026-11-20', self.files()[1])
        self.assertEqual(('1.5.0.00', []), self.promote())

    def test_internal_changes_get_a_heading_that_says_so(self):
        self.commit('ci: tidy a workflow (#1)')
        self.prepared()
        self.assertIn('## 1.3.1.06 — 2026-11-20\n\n' + prepare.PLACEHOLDER + '\n\n## 1.3.1.05', self.files()[1])
        self.assertEqual(('1.3.1.06', []), self.promote())

    def test_a_second_run_changes_nothing(self):
        self.commit('feat: add a thing (#1)', entry='Add a thing.')
        self.prepared()
        before = self.files()
        out = self.prepared('--date', '2026-12-01')
        self.assertIn('already prepared', out)
        self.assertEqual(before, self.files())

    def test_a_dry_run_changes_nothing(self):
        self.commit('feat: add a thing (#1)', entry='Add a thing.')
        before = self.files()
        self.assertIn('Medal Sync 1.4.0.00', self.prepared('--dry-run'))
        self.assertEqual(before, self.files())

    def test_refuses_when_there_is_nothing_to_promote(self):
        code, _, err = self.run_script()
        self.assertEqual(1, code)
        self.assertIn('nothing to release', err)

    def test_refuses_a_lowered_prefix(self):
        self.commit('release: lower', prefix='1.3.0')
        code, _, err = self.run_script()
        self.assertEqual(1, code)
        self.assertIn('lower than 1.3.1', err)

    def test_refuses_a_raised_prefix_that_is_already_released(self):
        self.git('tag', 'v1.4.0.00', 'main')
        self.commit('feat: add a thing (#1)', entry='Add a thing.')
        code, _, err = self.run_script()
        self.assertEqual(1, code)
        self.assertIn('already released', err)

    def test_later_entries_fold_into_the_prepared_section(self):
        self.commit('feat: add a thing (#1)', entry='Add a thing.')
        self.prepared()
        self.commit('release: prepare 1.4.0')
        self.commit('fix: repair a thing (#2)', entry='Repair a thing.')
        self.assertIn('Medal Sync 1.4.0.00', self.prepared('--date', '2026-11-21'))
        self.assertEqual('# Changelog\n\n## Unreleased\n\n## 1.4.0 — 2026-11-21\n\n'
                         '- Repair a thing.\n\n- Add a thing.\n\n' + SHIPPED, self.files()[1])
        self.assertEqual(('1.4.0.00', []), self.promote())

    def test_a_prepared_hotfix_folds_into_the_new_minor_version(self):
        self.commit('fix: repair a thing (#1)', entry='Repair a thing.')
        self.prepared()
        self.commit('release: prepare 1.3.1.06')
        self.commit('feat: add a thing (#2)', entry='Add a thing.')
        self.assertIn('Medal Sync 1.4.0.00 (VersionPrefix 1.3.1 -> 1.4.0)', self.prepared())
        changelog = self.files()[1]
        self.assertEqual('# Changelog\n\n## Unreleased\n\n## 1.4.0 — 2026-11-20\n\n'
                         '- Add a thing.\n\n- Repair a thing.\n\n' + SHIPPED, changelog)
        self.assertNotIn('1.3.1.06', changelog)
        self.assertEqual(('1.4.0.00', []), self.promote())

    def test_the_placeholder_gives_way_to_real_entries(self):
        self.commit('ci: tidy a workflow (#1)')
        self.prepared()
        self.commit('release: prepare 1.3.1.06')
        self.commit('fix: repair a thing (#2)', entry='Repair a thing.')
        self.prepared()
        changelog = self.files()[1]
        self.assertIn('## 1.3.1.06 — 2026-11-20\n\n- Repair a thing.\n\n## 1.3.1.05', changelog)
        self.assertNotIn(prepare.PLACEHOLDER, changelog)

    def test_refuses_a_malformed_date(self):
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            prepare.main(['--root', str(self.root), '--date', '20 Nov'])

    def test_refuses_a_date_that_does_not_exist(self):
        self.commit('fix: repair a thing (#9)', entry='Repair a thing.')
        before = self.files()
        for date in ('2026-13-01', '2026-02-30', '2026-11-00'):
            with self.subTest(date=date), self.assertRaises(SystemExit), \
                    contextlib.redirect_stderr(io.StringIO()) as stderr:
                prepare.main(['--root', str(self.root), '--date', date])
            self.assertIn('real date', stderr.getvalue())
            self.assertEqual(before, self.files())


if __name__ == '__main__':
    unittest.main()
