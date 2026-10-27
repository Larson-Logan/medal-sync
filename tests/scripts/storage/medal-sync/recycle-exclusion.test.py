"""The Medal recycle exclusion must change one line, fail closed and roll back."""
import contextlib
import hashlib
import importlib.util
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[4]
spec = importlib.util.spec_from_file_location(
    'medal_recycle', ROOT / 'scripts/storage/medal-sync/exclude-medal-temp-from-recycle.py')
recycle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recycle)
DEPLOYED = (ROOT / 'tests/fixtures/smb.conf').read_bytes().decode('utf-8')
# Exercise the migration from the pre-deployment configuration even after the
# reviewed snapshot has been updated to match the live server.
TEMPLATE = DEPLOYED.replace(' Media/Videos/Clips/Temp Media/Videos/Clips/Temp/*', '').replace(' RecordingBuffer', '')


class AddExclusions(unittest.TestCase):
    def test_changes_only_the_data_share_line(self):
        changed = recycle.add_exclusions(TEMPLATE)
        before, after = TEMPLATE.splitlines(), changed.splitlines()
        self.assertEqual(len(before), len(after))
        different = [(old, new) for old, new in zip(before, after) if old != new]
        self.assertEqual(len(different), 1)
        old, new = different[0]
        self.assertTrue(new.startswith(old.split('=')[0].rstrip() + ' = '))
        self.assertTrue(new.endswith(' RecordingBuffer'))
        for pattern in old.split('=', 1)[1].split():
            self.assertIn(pattern, new.split())
        self.assertIn('[Data]', TEMPLATE[:TEMPLATE.index(old)])
        self.assertNotIn('[ServerData]', TEMPLATE[:TEMPLATE.index(old)])

    def test_is_idempotent(self):
        once = recycle.add_exclusions(TEMPLATE)
        self.assertEqual(recycle.add_exclusions(once), once)

    def test_replaces_ineffective_full_path_patterns(self):
        legacy = TEMPLATE.replace('recycle:exclude_dir = Recycle ',
                                  'recycle:exclude_dir = Media/Videos/Clips/Temp Media/Videos/Clips/Temp/* Recycle ', 1)
        self.assertEqual(recycle.add_exclusions(legacy), recycle.add_exclusions(TEMPLATE))

    def test_refuses_a_missing_or_repeated_parameter(self):
        with self.assertRaises(ValueError):
            recycle.add_exclusions('[Data]\n    path = /srv/data\n[ServerData]\n    recycle:exclude_dir = X\n')
        with self.assertRaises(ValueError):
            recycle.add_exclusions('[Data]\n    recycle:exclude_dir = A\n    recycle:exclude_dir = B\n')


class Main(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.config = root / 'smb.conf'
        self.config.write_bytes(TEMPLATE.encode('utf-8'))
        self.backups = root / 'backups'
        self.calls = []
        self.fail_on = None

    def fake_run(self, *args):
        self.calls.append(args)
        if args[:2] == self.fail_on:
            raise subprocess.CalledProcessError(1, args)
        if '--parameter-name' in args:
            text = self.config.read_bytes().decode('utf-8')
            line = next(l for l in text.splitlines() if 'RecordingBuffer' in l)
            return subprocess.CompletedProcess(args, 0, line.split('=', 1)[1].strip() + '\n', '')
        return subprocess.CompletedProcess(args, 0, '', '')

    def invoke(self, *, expected=None, apply=False, hostname='file-storage'):
        args = ['exclude-medal-temp-from-recycle.py', '--expected-config-sha256',
                expected or hashlib.sha256(TEMPLATE.encode('utf-8')).hexdigest()]
        if apply:
            args.append('--apply')
        output = io.StringIO()
        with patch.object(recycle, 'CONFIG', self.config), patch.object(recycle, 'BACKUPS', self.backups), \
             patch.object(recycle.os, 'geteuid', return_value=0, create=True), \
             patch.object(recycle.socket, 'gethostname', return_value=hostname), \
             patch.object(recycle, 'run', side_effect=self.fake_run), patch.object(sys, 'argv', args), \
             contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
            recycle.main()
        return output.getvalue()

    def test_inspection_validates_and_changes_nothing(self):
        output = self.invoke()
        self.assertEqual(self.config.read_bytes().decode('utf-8'), TEMPLATE)
        self.assertFalse(self.backups.exists())
        self.assertEqual(self.calls[0][:2], ('testparm', '-s'))
        self.assertIn('Inspection only', output)

    def test_refuses_drifted_config_and_other_hosts(self):
        with self.assertRaises(SystemExit):
            self.invoke(expected='0' * 64, apply=True)
        with self.assertRaises(SystemExit):
            self.invoke(apply=True, hostname='pve')
        self.assertEqual(self.config.read_bytes().decode('utf-8'), TEMPLATE)
        self.assertEqual(self.calls, [])

    def test_apply_backs_up_installs_and_reloads(self):
        self.invoke(apply=True)
        self.assertEqual(self.config.read_bytes().decode('utf-8'), recycle.add_exclusions(TEMPLATE))
        saved = list(self.backups.glob('*/medal-recycle/smb.conf'))
        self.assertEqual(len(saved), 1)
        self.assertEqual(saved[0].read_bytes().decode('utf-8'), TEMPLATE)
        self.assertIn(('smbcontrol', 'all', 'reload-config'), self.calls)
        applied_hash = hashlib.sha256(self.config.read_bytes()).hexdigest()
        self.assertIn('Already applied', self.invoke(apply=True, expected=applied_hash))

    def test_refuses_drift_even_when_exclusions_are_present(self):
        self.config.write_bytes((recycle.add_exclusions(TEMPLATE) + '\n# unrelated drift\n').encode('utf-8'))
        with self.assertRaises(SystemExit):
            self.invoke(apply=True)
        self.assertEqual(self.calls, [])

    def test_a_failed_reload_restores_the_backup(self):
        self.fail_on = ('systemctl', 'is-active')
        with self.assertRaises(subprocess.CalledProcessError):
            self.invoke(apply=True)
        self.assertEqual(self.config.read_bytes().decode('utf-8'), TEMPLATE)
        self.assertEqual(self.calls[-1], ('smbcontrol', 'all', 'reload-config'))


if __name__ == '__main__':
    unittest.main()
