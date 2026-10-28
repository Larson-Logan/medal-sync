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

    def test_owner_notes_and_sync_files_are_protected_in_a_fresh_clone(self):
        self.init()
        for name in ['wiki/hot.md', 'docs/medal-sync.md', 'scripts/sync-agent-skills.py',
                     '.agent-sync/manifest.json', '.stignore', 'notes.sync-conflict-example.md']:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('synthetic private note', encoding='utf-8')
            self.git('check-ignore', '--quiet', name)
            self.assertEqual(0, self.check().returncode, name)
            self.git('add', '-f', name)
            self.assertNotEqual(0, self.check().returncode, name)
            self.git('rm', '--cached', '--', name)

    def test_deleted_runtime_file_is_still_rejected_in_history(self):
        self.init()
        (self.root / 'rclone.conf').write_text('synthetic', encoding='utf-8')
        self.git('add', '-f', 'rclone.conf')
        self.git('commit', '-m', 'Synthetic runtime file')
        self.git('rm', '--', 'rclone.conf')
        self.git('commit', '-m', 'Remove runtime file')
        self.assertEqual(0, self.check().returncode)
        self.assertIn('Runtime/private file; remains in history', self.check('--history').stderr)

    def test_deleted_credentials_are_detected_without_echoing_the_value(self):
        self.init()
        token = 'gh' + 'p_' + 'a' * 36
        path = self.root / 'notes.txt'
        path.write_text(token, encoding='utf-8')
        self.git('add', 'notes.txt')
        self.git('commit', '-m', 'Synthetic old content')
        path.write_text('clean replacement', encoding='utf-8')
        self.git('add', 'notes.txt')
        self.git('commit', '-m', 'Replace old content')
        self.assertEqual(0, self.check().returncode)
        result = self.check('--history')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Possible credential in history', result.stderr)
        self.assertNotIn(token, result.stdout + result.stderr)

    def test_pull_request_only_ref_is_checked_in_bare_mirror(self):
        self.init()
        self.git('commit', '-m', 'Clean main')
        self.git('switch', '-c', 'private-fixture')
        (self.root / 'AGENTS.md').write_text('synthetic private note', encoding='utf-8')
        self.git('add', '-f', 'AGENTS.md')
        self.git('commit', '-m', 'Private PR fixture')
        self.git('update-ref', 'refs/pull/99/head', 'HEAD')
        self.git('switch', 'main')
        self.git('branch', '-D', 'private-fixture')
        mirror = self.root / 'mirror.git'
        self.git('clone', '--mirror', str(self.root), str(mirror))
        # Keep the mirror outside working-tree source enumeration.
        self.git('config', 'core.excludesFile', str(self.root / 'fixture-ignore'))
        (self.root / 'fixture-ignore').write_text('mirror.git/\nfixture-ignore\n', encoding='utf-8')
        result = self.check('--history', '--history-repo', str(mirror))
        self.assertNotEqual(0, result.returncode)
        self.assertIn('remains in history', result.stderr)
        self.assertIn('AGENTS.md', result.stderr)

    def test_commit_and_tag_metadata_are_checked(self):
        self.init()
        token = 'gh' + 'p_' + 'b' * 36
        self.git('commit', '-m', token)
        self.git('tag', '-a', 'synthetic', '-m', token)
        result = self.check('--history')
        self.assertIn('Possible credential in commit metadata', result.stderr)
        self.assertIn('Possible credential in tag metadata', result.stderr)
        self.assertNotIn(token, result.stdout + result.stderr)

    def test_private_network_addresses_are_rejected(self):
        self.init()
        private = '192.' + '168.' + '86.' + '123'
        (self.root / 'notes.txt').write_text(private, encoding='utf-8')
        result = self.check()
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Private deployment reference', result.stderr)
        self.assertNotIn(private, result.stdout + result.stderr)

    def test_personal_profile_paths_are_rejected_but_placeholders_are_allowed(self):
        self.init()
        path = self.root / 'notes.txt'
        path.write_text('C:' + '/' + 'Users' + '/' + 'synthetic-person' + '/data', encoding='utf-8')
        self.assertIn('Private deployment reference', self.check().stderr)
        path.write_text('C:' + '/' + 'Users' + '/' + '<user>' + '/data', encoding='utf-8')
        self.assertEqual(0, self.check().returncode)

    def test_shallow_clone_cannot_claim_a_complete_history_audit(self):
        self.init()
        self.git('commit', '-m', 'First fixture')
        (self.root / 'public.txt').write_text('synthetic public source', encoding='utf-8')
        self.git('add', 'public.txt')
        self.git('commit', '-m', 'Second fixture')
        clone = self.root / 'shallow'
        self.git('clone', '--depth=1', self.root.as_uri(), str(clone))
        result = subprocess.run(['python', 'scripts/check-repository.py', '--history'],
                                cwd=clone, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('requires a full clone', result.stderr)


if __name__ == '__main__':
    unittest.main()
