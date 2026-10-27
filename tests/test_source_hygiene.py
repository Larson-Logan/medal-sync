"""Prove local agent settings stay local, including force-added and historical files."""
from pathlib import Path
import json
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class SourceHygieneTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix='medal-hygiene-')
        self.root = Path(self.scratch.name)
        (self.root / 'scripts').mkdir()
        shutil.copyfile(ROOT / 'scripts/check-repository.py', self.root / 'scripts/check-repository.py')
        shutil.copyfile(ROOT / '.gitignore', self.root / '.gitignore')
        (self.root / 'global.json').write_text('{}', encoding='utf-8')

    def tearDown(self):
        # This directory was created by TemporaryDirectory for this test only.
        self.scratch.cleanup()

    def git(self, *args):
        return subprocess.check_output(['git', *args], cwd=self.root, stderr=subprocess.STDOUT, text=True)

    def init(self):
        self.git('init', '-b', 'main')
        self.git('config', 'user.name', 'Synthetic Fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.git('add', '.')

    def check(self, *args):
        return subprocess.run(['python', 'scripts/check-repository.py', *args], cwd=self.root,
                              capture_output=True, text=True)

    def test_ignored_settings_are_allowed_but_forced_tracking_is_rejected(self):
        self.init()
        for name in ['AGENTS.md', 'CLAUDE.md', '.codex/config.toml', '.claude/settings.json',
                     '.agents/skills/personal/SKILL.md', '.mcp.json', '.github/copilot-instructions.md']:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('synthetic personal preference', encoding='utf-8')
            self.git('check-ignore', '--quiet', name)
            self.assertEqual(0, self.check().returncode, name)
            self.git('add', '-f', name)
            refused = self.check()
            self.assertNotEqual(0, refused.returncode, name)
            self.assertIn('must remain untracked', refused.stderr, name)
            self.git('rm', '--cached', '--', name)

    def test_removing_file_from_tip_does_not_hide_it_from_history_check(self):
        self.init()
        (self.root / 'AGENTS.md').write_text('synthetic', encoding='utf-8')
        self.git('add', '-f', 'AGENTS.md')
        self.git('commit', '-m', 'Synthetic private file')
        self.git('rm', '--', 'AGENTS.md')
        self.git('commit', '-m', 'Remove synthetic private file')
        self.assertEqual(0, self.check().returncode)
        refused = self.check('--history')
        self.assertNotEqual(0, refused.returncode)
        self.assertIn('remains in history', refused.stderr)

    def test_source_archive_manifest_cannot_include_agent_instructions(self):
        (self.root / 'AGENTS.md').write_text('synthetic', encoding='utf-8')
        (self.root / 'public-source.json').write_text(json.dumps({
            'format': 1, 'files': ['global.json', 'AGENTS.md', 'scripts/check-repository.py']
        }), encoding='utf-8')
        refused = self.check()
        self.assertNotEqual(0, refused.returncode)
        self.assertIn('must remain untracked', refused.stderr)

    def test_provider_credentials_stay_ignored_and_cannot_be_force_added(self):
        self.init()
        for name in ['rclone.conf', 'private.rclone.conf']:
            (self.root / name).write_text('[provider]\ntoken = synthetic', encoding='utf-8')
            self.git('check-ignore', '--quiet', name)
            self.git('add', '-f', name)
            self.assertNotEqual(0, self.check().returncode)
            self.git('rm', '--cached', '--', name)


if __name__ == '__main__':
    unittest.main()
