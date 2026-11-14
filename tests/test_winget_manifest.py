"""Check the winget manifest generator against the real installer settings."""
import contextlib
import importlib.util
import io
from pathlib import Path
import re
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('new_winget_manifest', ROOT / 'scripts/new-winget-manifest.py')
winget = importlib.util.module_from_spec(spec)
spec.loader.exec_module(winget)

VERSION = '1.4.0.00'
SETUP = f'medal-sync-{VERSION}-setup-win-x64.exe'
HASH = 'ab' * 32


class WingetManifestTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='winget-')
        self.addCleanup(temporary.cleanup)
        self.out = Path(temporary.name)
        self.folder = self.out / 'manifests/l/LarsonLogan/MedalSync' / VERSION

    def run_script(self, *args):
        stderr = io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(stderr):
            try:
                return winget.main([*args, '--out', str(self.out)]), stderr.getvalue()
            except SystemExit as error:
                return error.code, stderr.getvalue()

    def test_matches_the_setup_scripts_identity(self):
        # winget matches installed apps by product code and publisher, so these
        # must follow AppId and AppPublisher if the setup script changes them.
        setup = (ROOT / 'installer/windows/medal-sync.iss').read_text(encoding='utf-8')
        app_id = re.search(r'^AppId=\{(\{[0-9A-F-]{36}\})$', setup, re.MULTILINE).group(1)
        self.assertEqual(f'{app_id}_is1', winget.PRODUCT_CODE)
        self.assertIn(f'AppPublisher={winget.PUBLISHER}\n', setup)
        self.assertIn('OutputBaseFilename=medal-sync-{#AppVersion}-setup-win-x64\n', setup)
        self.assertIn('PrivilegesRequiredOverridesAllowed=dialog\n', setup)

    def test_writes_the_three_manifests_from_a_sidecar(self):
        sidecar = self.out / f'{SETUP}.sha256'
        sidecar.write_text(f'{HASH}  {SETUP}\n', encoding='ascii')
        code, _ = self.run_script('--version', VERSION, '--sidecar', str(sidecar), '--release-date', '2026-11-05')
        self.assertEqual(0, code)
        names = sorted(path.name for path in self.folder.iterdir())
        self.assertEqual(['LarsonLogan.MedalSync.installer.yaml', 'LarsonLogan.MedalSync.locale.en-US.yaml',
                          'LarsonLogan.MedalSync.yaml'], names)
        installer = (self.folder / 'LarsonLogan.MedalSync.installer.yaml').read_text(encoding='utf-8')
        url = f'https://github.com/Larson-Logan/medal-sync/releases/download/v{VERSION}/{SETUP}'
        self.assertEqual(2, installer.count(f'InstallerUrl: {url}\n'))
        self.assertEqual(2, installer.count(f'InstallerSha256: {HASH.upper()}\n'))
        self.assertIn('Scope: machine\n  InstallerUrl', installer)
        self.assertIn('Custom: /ALLUSERS\n', installer)
        self.assertIn('Custom: /CURRENTUSER\n', installer)
        # Only per-user setup needs WinFsp from winget; all-users setup bundles it.
        self.assertEqual(1, installer.count('PackageIdentifier: WinFsp.WinFsp'))
        self.assertTrue(installer.rstrip().endswith('ManifestVersion: 1.10.0'))
        self.assertIn('ReleaseDate: 2026-11-05\n', installer)
        for path in self.folder.iterdir():
            text = path.read_text(encoding='utf-8')
            self.assertIn(f'PackageVersion: {VERSION}\n', text)
            self.assertNotIn('\t', text)

    def test_refuses_betas_bad_hashes_and_mismatched_sidecars(self):
        sidecar = self.out / 'other.sha256'
        sidecar.write_text(f'{HASH}  medal-sync-1.3.0.00-setup-win-x64.exe\n', encoding='ascii')
        for args in (['--version', '1.4.0.00-beta.1', '--sha256', HASH],
                     ['--version', '1.4.0', '--sha256', HASH],
                     ['--version', VERSION, '--sha256', 'ab' * 31],
                     ['--version', VERSION, '--sha256', HASH, '--release-date', '2026-13-01'],
                     ['--version', VERSION, '--sidecar', str(sidecar)]):
            with self.subTest(args=args):
                code, _ = self.run_script(*args)
                self.assertEqual(2, code)
                self.assertFalse(self.folder.exists())

    def test_never_overwrites_an_existing_folder(self):
        self.assertEqual(0, self.run_script('--version', VERSION, '--sha256', HASH)[0])
        code, error = self.run_script('--version', VERSION, '--sha256', HASH)
        self.assertEqual(2, code)
        self.assertIn('already exists', error)


if __name__ == '__main__':
    unittest.main()
