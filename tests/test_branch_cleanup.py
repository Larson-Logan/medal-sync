"""Exercise branch deletion boundaries without contacting GitHub."""
import copy
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('cleanup', ROOT / 'scripts/cleanup-pr-branch.py')
cleanup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cleanup)


class BranchCleanupTests(unittest.TestCase):
    def setUp(self):
        self.repository = 'example/project'
        self.pull = {'state': 'closed', 'head': {
            'ref': 'topic/change', 'sha': 'a' * 40, 'repo': {'full_name': self.repository}}}
        self.branch = {'protected': False, 'commit': {'sha': 'a' * 40}}
        self.settings = {'default_branch': 'dev', 'clone_url': 'https://github.com/example/project.git'}
        self.pages = [[]]
        self.calls = []
        self.recheck = None

    def api(self, path, method='GET', missing_ok=False):
        self.calls.append((method, path))
        if method == 'DELETE':
            return None
        if '/pulls?' in path:
            page = int(path.rsplit('=', 1)[1])
            return self.pages[page - 1]
        if '/pulls/' in path:
            if self.recheck and sum('/pulls/' in p for _, p in self.calls) > 1:
                return self.recheck
            return self.pull
        if '/branches/' in path:
            return self.branch
        return self.settings

    def run_cleanup(self, apply=True):
        with (patch.object(cleanup, 'api', side_effect=self.api),
              patch.object(cleanup, 'delete_branch', side_effect=lambda url, name, sha:
                           self.calls.append(('DELETE', (url, name, sha))))):
            return cleanup.cleanup(self.repository, 7, apply)

    def assert_kept(self):
        self.assertTrue(self.run_cleanup().startswith('Keep:'))
        self.assertFalse(any(method == 'DELETE' for method, _ in self.calls))

    def test_delete_closed_unchanged_branch(self):
        self.assertTrue(self.run_cleanup().startswith('Deleted'))
        self.assertEqual(('DELETE', (self.settings['clone_url'], 'topic/change', 'a' * 40)), self.calls[-1])
        self.assertIn(('GET', 'repos/example/project/branches/topic%2Fchange'), self.calls)

    def test_dry_run_does_not_delete(self):
        self.assertTrue(self.run_cleanup(False).startswith('Would delete'))
        self.assertFalse(any(method == 'DELETE' for method, _ in self.calls))

    def test_open_or_reopened_pull_keeps_branch(self):
        self.pull['state'] = 'open'
        self.assert_kept()

    def test_fork_and_deleted_repository_keep_branch(self):
        for repo in [{'full_name': 'fork/project'}, None]:
            with self.subTest(repo=repo):
                self.pull['head']['repo'] = repo
                self.assert_kept()

    def test_main_dev_and_default_are_kept(self):
        self.settings['default_branch'] = 'integration'
        for name in ['main', 'dev', 'integration']:
            with self.subTest(name=name):
                self.pull['head']['ref'] = name
                self.assert_kept()

    def test_protected_branch_is_kept(self):
        self.branch['protected'] = True
        self.assert_kept()

    def test_new_commits_are_kept(self):
        self.branch['commit']['sha'] = 'b' * 40
        self.assert_kept()

    def test_missing_branch_is_idempotent(self):
        self.branch = None
        self.assertEqual('Already deleted.', self.run_cleanup())
        self.assertFalse(any(method == 'DELETE' for method, _ in self.calls))

    def test_open_head_or_base_pull_keeps_branch_including_later_page(self):
        for field in ['head', 'base']:
            with self.subTest(field=field):
                other = {'number': 8, 'head': {'ref': 'another', 'repo': {'full_name': self.repository}},
                         'base': {'ref': 'dev'}}
                other[field]['ref'] = 'topic/change'
                unrelated = copy.deepcopy(other)
                unrelated[field]['ref'] = 'unrelated'
                self.pages = [[unrelated] * 100, [other]]
                self.assert_kept()

    def test_reopened_during_cleanup_is_kept(self):
        self.recheck = copy.deepcopy(self.pull)
        self.recheck['state'] = 'open'
        self.assert_kept()

    def test_api_error_does_not_delete(self):
        with patch.object(cleanup, 'api', side_effect=RuntimeError('API unavailable')):
            with self.assertRaises(RuntimeError):
                cleanup.cleanup(self.repository, 7, True)

    def test_deletion_uses_atomic_lease_and_credential_helper(self):
        with patch.object(cleanup.subprocess, 'run') as run:
            cleanup.delete_branch(self.settings['clone_url'], 'topic/change', 'a' * 40)
        command = run.call_args.args[0]
        self.assertIn('--force-with-lease=refs/heads/topic/change:' + 'a' * 40, command)
        self.assertIn('credential.helper=!gh auth git-credential', command)
        self.assertEqual(':refs/heads/topic/change', command[-1])
        self.assertTrue(run.call_args.kwargs['check'])

    def test_real_git_refuses_stale_lease_then_deletes_matching_head(self):
        with tempfile.TemporaryDirectory() as folder:
            def git(*args, input=None):
                return subprocess.check_output(
                    ['git', '-C', folder, '-c', 'user.name=Test',
                     '-c', 'user.email=test@example.com', *args],
                    input=input, text=True, stderr=subprocess.PIPE).strip()
            git('init', '--bare')
            tree = git('mktree', input='')
            first = git('commit-tree', tree, '-m', 'First')
            second = git('commit-tree', tree, '-p', first, '-m', 'Concurrent push')
            git('update-ref', 'refs/heads/topic/change', second)
            with self.assertRaises(subprocess.CalledProcessError):
                cleanup.delete_branch(folder, 'topic/change', first)
            self.assertEqual(second, git('rev-parse', 'refs/heads/topic/change'))
            cleanup.delete_branch(folder, 'topic/change', second)
            self.assertEqual('', git('for-each-ref', '--format=%(refname)', 'refs/heads/'))


if __name__ == '__main__':
    unittest.main()
