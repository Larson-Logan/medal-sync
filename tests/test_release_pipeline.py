"""Exercise the actual publish-step asset checks with disposable files."""
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ReleaseAssetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        workflow = (ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8')
        # Read the literal PowerShell step without adding a YAML dependency to CI.
        step = workflow.split('- name: Verify planned asset names and checksums\n', 1)[1]
        step = step.split('      - name:', 1)[0]
        cls.script = textwrap.dedent(step.split('        run: |\n', 1)[1])

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='release-assets-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.assets = self.root / '.medal-sync-dist'
        self.assets.mkdir()
        self.version = '1.3.0.02'
        self.setup = f'medal-sync-{self.version}-setup-win-x64.exe'
        self.archive = f'medal-sync-{self.version}-win-x64.zip'
        for name in (self.setup, self.archive):
            data = f'synthetic payload: {name}'.encode()
            (self.assets / name).write_bytes(data)
            (self.assets / f'{name}.sha256').write_text(
                f'{hashlib.sha256(data).hexdigest()}  {name}\n', encoding='ascii')
        self.path = self.root / 'verify.ps1'
        self.path.write_text(self.script, encoding='utf-8')

    def verify(self, *, succeeds):
        result = subprocess.run(
            ['pwsh', '-NoProfile', '-NonInteractive', '-File', str(self.path)],
            cwd=self.root, env={**os.environ, 'VERSION': self.version},
            capture_output=True, text=True,
        )
        if succeeds:
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        else:
            self.assertNotEqual(0, result.returncode, 'Invalid assets passed publication checks')

    def test_accepts_exact_assets_and_matching_checksums(self):
        self.verify(succeeds=True)

    def test_rejects_missing_payload_or_sidecar(self):
        for name in (self.archive, f'{self.setup}.sha256'):
            with self.subTest(name=name):
                path = self.assets / name
                data = path.read_bytes()
                path.unlink()
                self.verify(succeeds=False)
                path.write_bytes(data)

    def test_rejects_unexpected_file_or_directory(self):
        extra = self.assets / 'staging'
        extra.write_text('build staging', encoding='ascii')
        self.verify(succeeds=False)
        extra.unlink()
        extra.mkdir()
        self.verify(succeeds=False)

    def test_rejects_corrupted_payload(self):
        for name in (self.setup, self.archive):
            with self.subTest(name=name):
                path = self.assets / name
                data = path.read_bytes()
                path.write_bytes(data + b'changed')
                self.verify(succeeds=False)
                path.write_bytes(data)

    def test_rejects_a_different_planned_version(self):
        self.version = '1.3.0.03'
        self.verify(succeeds=False)

    def test_rejects_a_sidecar_naming_another_file(self):
        path = self.assets / f'{self.setup}.sha256'
        path.write_text(path.read_text(encoding='ascii').replace(self.setup, self.archive), encoding='ascii')
        self.verify(succeeds=False)


if __name__ == '__main__':
    unittest.main()
