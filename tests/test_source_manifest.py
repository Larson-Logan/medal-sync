"""Keep the source inventory's suite rules in step with the validation workflow."""
from pathlib import Path
import importlib.util
import json
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('manifest', ROOT / 'scripts/update-source-manifest.py')
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)
WORKFLOW = (ROOT / '.github/workflows/validate.yml').read_text(encoding='utf-8')


class SourceManifestTests(unittest.TestCase):
    def test_every_suite_gates_a_workflow_job(self):
        for suite in manifest.SUITES:
            self.assertIn(f"needs.checks.outputs.{suite} == 'true'", WORKFLOW, suite)
            self.assertIn(f'{suite}: ${{{{ steps.scope.outputs.{suite} }}}}', WORKFLOW, suite)
            self.assertRegex(WORKFLOW, rf'"[a-z-]+": "{suite}"', suite)
            self.assertIn(f'{suite}=true', WORKFLOW, suite)

    def test_unknown_and_core_files_need_every_suite(self):
        for name in ['new-kind-of-file.txt', 'tools/medal-sync/MedalSync/Program.cs', 'global.json',
                     'Directory.Build.props', 'tools/medal-sync/config/dependencies.json',
                     '.github/workflows/validate.yml', 'scripts/validate.ps1', 'docs/nested/page.md']:
            self.assertEqual(manifest.ALL, manifest.suites_for(name), name)

    def test_shipped_and_shared_files_are_not_documentation(self):
        self.assertEqual(['desktop'], manifest.suites_for('docs/setup.html'))
        self.assertEqual(['desktop'], manifest.suites_for('LICENSE'))
        self.assertEqual(['desktop'], manifest.suites_for('THIRD_PARTY.md'))
        self.assertEqual(manifest.ALL, manifest.suites_for('tests/tools/medal-sync/Fixtures/MedalFixture.cs'))
        self.assertEqual(['unit'], manifest.suites_for('tests/tools/medal-sync/LibraryTests.cs'))
        self.assertEqual([], manifest.suites_for('docs/storage.md'))

    def test_committed_inventory_lists_only_known_suites(self):
        committed = json.loads((ROOT / 'public-source.json').read_text(encoding='utf-8'))
        self.assertEqual(manifest.FORMAT, committed['format'])
        self.assertEqual(manifest.SUITES, committed['suites'])
        for name, suites in committed['files'].items():
            self.assertLessEqual(set(suites), set(manifest.SUITES), name)

    def test_every_rule_matches_a_committed_file(self):
        files = json.loads((ROOT / 'public-source.json').read_text(encoding='utf-8'))['files']
        for patterns, _ in manifest.RULES:
            for pattern in patterns:
                self.assertTrue(any(manifest.matches(name, pattern) for name in files), pattern)

    def test_main_only_accepts_dev(self):
        self.assertRegex(WORKFLOW, re.escape('github.base_ref == \'main\''))
        self.assertIn('"$HEAD_REF" != dev', WORKFLOW)


if __name__ == '__main__':
    unittest.main()
