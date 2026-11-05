"""Exercise the actual release and beta workflow steps with disposable files and a stubbed gh."""
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]


def step_script(name, workflow='release.yml'):
    workflow = (ROOT / '.github/workflows' / workflow).read_text(encoding='utf-8')
    # Read the literal PowerShell step without adding a YAML dependency to CI.
    step = workflow.split(f'- name: {name}\n', 1)[1]
    body = step.split('        run: |\n', 1)[1].splitlines(keepends=True)
    # The block ends at the first line indented less than the script itself:
    # the next step, or the next job after a job's last step.
    script = []
    for line in body:
        if line.strip() and not line.startswith(' ' * 10):
            break
        script.append(line)
    return textwrap.dedent(''.join(script))


class ReleaseAssetTests(unittest.TestCase):
    WORKFLOW = 'release.yml'
    VERSION = '1.3.0.02'

    @classmethod
    def setUpClass(cls):
        cls.script = step_script('Verify planned asset names and checksums', cls.WORKFLOW)

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='release-assets-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.assets = self.root / '.medal-sync-dist'
        self.assets.mkdir()
        self.version = self.VERSION
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


class BetaAssetTests(ReleaseAssetTests):
    WORKFLOW = 'beta.yml'
    VERSION = '1.3.1.02-beta.3'

    def test_rejects_the_stable_assets_of_the_same_version(self):
        self.version = '1.3.1.02'
        self.verify(succeeds=False)


class ReleasePublishTests(unittest.TestCase):
    def test_publishes_to_the_explicit_repository_without_a_checkout(self):
        with tempfile.TemporaryDirectory(prefix='release-publish-') as temporary:
            root = Path(temporary)
            # A fake CLI prevents network access and checks the arguments of the
            # actual publish step. No repository or remotes exist in this folder.
            stub = textwrap.dedent('''\
                $ErrorActionPreference = 'Stop'
                $global:releaseCalls = @()
                function gh {
                    $global:LASTEXITCODE = 0
                    if ($args[0] -eq 'api') { return }
                    if ($args[0] -ne 'release') { throw 'Unexpected gh command.' }
                    $repositoryIndex = [Array]::IndexOf($args, '--repo')
                    if ($repositoryIndex -lt 0 -or $args[$repositoryIndex + 1] -cne $env:GITHUB_REPOSITORY) {
                        throw 'Release command requires the explicit target repository.'
                    }
                    $global:releaseCalls += $args[1]
                    if ($args[1] -eq 'create') { 'https://example.com/release' }
                }
            ''')
            assertions = textwrap.dedent('''\
                if (($global:releaseCalls -join ',') -cne 'create,edit') {
                    throw 'Expected draft creation followed by publication.'
                }
            ''')
            script = root / 'publish.ps1'
            script.write_text(stub + step_script('Publish GitHub release') + assertions, encoding='utf-8')
            summary = root / 'summary.md'
            environment = {**os.environ, 'GITHUB_REPOSITORY': 'example/project',
                           'GITHUB_SHA': 'a' * 40, 'GITHUB_STEP_SUMMARY': str(summary),
                           'VERSION': '1.3.0.02'}
            result = subprocess.run(
                ['pwsh', '-NoProfile', '-NonInteractive', '-File', str(script)],
                cwd=root, env=environment, capture_output=True, text=True,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn('https://example.com/release', summary.read_text(encoding='utf-8'))


class BetaPublishTests(unittest.TestCase):
    """Runs the actual beta publish step against a fake gh that records each call."""

    STUB = textwrap.dedent(r"""
        $ErrorActionPreference = 'Stop'
        $global:calls = @()
        function gh {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq 'api') {
                if ($args -contains "repos/$env:GITHUB_REPOSITORY/releases/latest") { return 'v1.3.1.01' }
                if ($env:EXISTING) { return $env:EXISTING }
                return
            }
            $global:calls += ($args -join ' ')
            $repositoryIndex = [Array]::IndexOf($args, '--repo')
            if ($repositoryIndex -lt 0 -or $args[$repositoryIndex + 1] -cne $env:GITHUB_REPOSITORY) {
                throw 'Release command requires the explicit target repository.'
            }
            if ($args[1] -eq 'create') { 'https://example.com/beta' }
        }
    """)
    REPORT = "\n} finally { $global:calls | Set-Content -LiteralPath calls.txt -Encoding utf8 }\n"

    def publish(self, existing=''):
        with tempfile.TemporaryDirectory(prefix='beta-publish-') as temporary:
            root = Path(temporary)
            script = root / 'publish.ps1'
            body = step_script('Publish GitHub prerelease', 'beta.yml')
            script.write_text(self.STUB + 'try {\n' + body + self.REPORT, encoding='utf-8')
            environment = {**os.environ, 'GITHUB_REPOSITORY': 'example/project',
                           'GITHUB_SHA': 'b' * 40, 'GITHUB_STEP_SUMMARY': str(root / 'summary.md'),
                           'VERSION': '1.3.1.02-beta.3', 'EXISTING': existing}
            result = subprocess.run(
                ['pwsh', '-NoProfile', '-NonInteractive', '-File', str(script)],
                cwd=root, env=environment, capture_output=True, text=True,
            )
            calls_file, summary_file = root / 'calls.txt', root / 'summary.md'
            calls = calls_file.read_text(encoding='utf-8-sig').splitlines() if calls_file.exists() else []
            summary = summary_file.read_text(encoding='utf-8') if summary_file.exists() else ''
            return result, [call.split(' ') for call in calls if call], summary

    def test_publishes_a_prerelease_that_is_never_latest(self):
        result, calls, summary = self.publish()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(2, len(calls), calls)
        create, edit = calls
        self.assertEqual(['release', 'create', 'v1.3.1.02-beta.3'], create[:3])
        for flag in ('--draft', '--prerelease', '--latest=false', '--generate-notes'):
            self.assertIn(flag, create)
        self.assertEqual('b' * 40, create[create.index('--target') + 1])
        # Notes start at the latest stable release, so a beta lists all of dev since then.
        self.assertEqual('v1.3.1.01', create[create.index('--notes-start-tag') + 1])
        self.assertIn('.medal-sync-dist/medal-sync-1.3.1.02-beta.3-setup-win-x64.exe', create)
        self.assertEqual(['release', 'edit', 'v1.3.1.02-beta.3'], edit[:3])
        for flag in ('--draft=false', '--prerelease', '--latest=false'):
            self.assertIn(flag, edit)
        self.assertIn('https://example.com/beta', summary)

    def test_refuses_when_the_beta_or_its_release_already_exists(self):
        result, calls, _ = self.publish(existing='https://example.com/releases/v1.3.1.02')
        self.assertNotEqual(0, result.returncode, 'A beta was published over an existing release')
        self.assertEqual([], calls)


class BetaPlanTests(unittest.TestCase):
    """Runs the actual beta planning step against a fake gh with a synthetic release history."""

    STUB = textwrap.dedent(r"""
        gh() {
          local args="$*" prefix
          case "$args" in
            *contents/Directory.Build.props*)
              printf '<Project>\n  <PropertyGroup>\n    <VersionPrefix>%s</VersionPrefix>\n  </PropertyGroup>\n</Project>\n' "$STUB_PREFIX" ;;
            *commits/dev*) echo "$STUB_HEAD" ;;
            *target_commitish*) if [ -n "$STUB_EXISTING" ]; then echo "$STUB_EXISTING"; fi ;;
            *matching-refs/tags/*)
              prefix="${args#*matching-refs/tags/}"
              prefix="${prefix%% *}"
              printf '%s\n' $STUB_TAGS | awk -v p="$prefix" 'index($0, p) == 1' ;;
            */releases*) printf '%s\n' $STUB_RELEASES ;;
            *) echo "unexpected gh call: $args" >&2; return 1 ;;
          esac
        }
    """)

    def plan(self, prefix='1.3.1', releases=(), tags=(), head='c' * 40, existing=''):
        with tempfile.TemporaryDirectory(prefix='beta-plan-') as temporary:
            root = Path(temporary)
            script = root / 'plan.sh'
            # GitHub runs bash steps with -eo pipefail.
            script.write_text('set -eo pipefail\n' + self.STUB + step_script('Plan beta version', 'beta.yml'),
                              encoding='utf-8', newline='\n')
            output, summary = root / 'output', root / 'summary'
            output.touch()
            environment = {**os.environ, 'REPO': 'example/project', 'SHA': 'c' * 40,
                           'GITHUB_OUTPUT': str(output), 'GITHUB_STEP_SUMMARY': str(summary),
                           'STUB_PREFIX': prefix, 'STUB_HEAD': head, 'STUB_EXISTING': existing,
                           'STUB_RELEASES': ' '.join(releases), 'STUB_TAGS': ' '.join(tags)}
            result = subprocess.run(['bash', script.name], cwd=root, env=environment,
                                    capture_output=True, text=True)
            values = dict(line.split('=', 1) for line in output.read_text(encoding='utf-8').splitlines())
            return result, values

    def planned(self, **history):
        result, values = self.plan(**history)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual('true', values.get('release'), values)
        base, prerelease = values['version'].split('-', 1)
        self.assertEqual(values['revision'], base.split('.')[3])
        self.assertEqual(values['prerelease'], prerelease)
        return values['version']

    def test_first_beta_leads_up_to_the_next_hotfix(self):
        stable = ('v1.3.0.05', 'v1.3.1.00', 'v1.3.1.01')
        self.assertEqual('1.3.1.02-beta.1', self.planned(releases=stable, tags=stable))

    def test_later_betas_count_up_from_releases_and_tags(self):
        releases = ('v1.3.1.00', 'v1.3.1.01', 'v1.3.1.01-beta.7', 'v1.3.1.02-beta.1', 'v1.3.1.02-beta.2')
        tags = releases + ('v1.3.1.02-beta.3',)
        self.assertEqual('1.3.1.02-beta.4', self.planned(releases=releases, tags=tags))

    def test_betas_never_raise_the_stable_counter(self):
        releases = ('v1.3.1.00', 'v1.3.1.01-beta.1', 'v1.3.1.01-beta.2')
        self.assertEqual('1.3.1.01-beta.3', self.planned(releases=releases, tags=releases))

    def test_a_raised_version_prefix_starts_at_00(self):
        releases = ('v1.3.1.01', 'v1.3.1.02-beta.4')
        self.assertEqual('1.4.0.00-beta.1', self.planned(prefix='1.4.0', releases=releases, tags=releases))

    def test_the_bridge_release_counts_as_00(self):
        self.assertEqual('1.3.0.01-beta.1',
                         self.planned(prefix='1.3.0', releases=('v1.2.0', 'v1.3.0'), tags=('v1.3.0',)))

    def test_counter_keeps_counting_past_99(self):
        releases = ('v1.3.1.99', 'v1.3.1.100-beta.1')
        self.assertEqual('1.3.1.100-beta.2', self.planned(releases=releases, tags=releases))

    def test_skips_a_commit_that_is_no_longer_the_tip_of_dev(self):
        result, values = self.plan(head='d' * 40)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({'release': 'false'}, values)

    def test_skips_a_commit_that_already_has_a_release(self):
        result, values = self.plan(existing='https://example.com/releases/v1.3.1.02-beta.1')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({'release': 'false'}, values)

    def test_refuses_a_malformed_version_prefix(self):
        result, _ = self.plan(prefix='1.3')
        self.assertNotEqual(0, result.returncode)



class PromotionReuseTests(unittest.TestCase):
    """Runs the actual suite choice for a promotion against a fake gh and a disposable merge."""

    STUB = textwrap.dedent(r"""
        sleep() { echo slept >>"$CALLS"; }
        gh() {
          local args="$*" calls
          echo "gh $args" >>"$CALLS"
          calls="$(grep -c '^gh .*beta.yml/runs' "$CALLS" || true)"
          case "$args" in
            *beta.yml/runs*'branch=dev&event=push&head_sha='*)
              if [ -n "$STUB_FAIL" ]; then echo '{"message":"Server Error"}'; return 1; fi
              if [ "$calls" -le "${STUB_PENDING:-0}" ]; then echo "7 in_progress https://example.com/runs/7"
              else echo "$STUB_RUN"; fi ;;
            *actions/runs/7/jobs*)
              if [ "$calls" -le "${STUB_PENDING:-0}" ]; then echo 'in_progress none'
              else echo "$STUB_JOB"; fi ;;
            *) echo "unexpected gh call: $args" >&2; return 1 ;;
          esac
        }
    """)
    ENV = {**{k: v for k, v in os.environ.items() if k not in ('GITHUB_ACTIONS', 'GITHUB_STEP_SUMMARY')},
           'GIT_AUTHOR_NAME': 'Test', 'GIT_AUTHOR_EMAIL': 'test@example.invalid',
           'GIT_COMMITTER_NAME': 'Test', 'GIT_COMMITTER_EMAIL': 'test@example.invalid',
           'GIT_CONFIG_GLOBAL': os.devnull, 'GIT_CONFIG_NOSYSTEM': '1'}

    def choose(self, run='7 completed https://example.com/runs/7', job='completed success',
               pending=0, fail='', differ=False):
        with tempfile.TemporaryDirectory(prefix='promotion-reuse-') as temporary:
            root = Path(temporary)
            repo = root / 'repo'
            repo.mkdir()
            git = lambda *args: subprocess.run(['git', *args], cwd=repo, env=self.ENV, check=True,
                                               capture_output=True, text=True).stdout.strip()
            git('init', '-q', '-b', 'main')
            (repo / 'file.txt').write_text('main\n', encoding='utf-8')
            git('add', '.')
            git('commit', '-q', '-m', 'main')
            git('checkout', '-q', '-b', 'dev')
            (repo / 'file.txt').write_text('dev\n', encoding='utf-8')
            git('commit', '-q', '-am', 'feat: change')
            git('checkout', '-q', 'main')
            git('merge', '-q', '--no-ff', '-m', 'Promote dev', 'dev')
            if differ:
                (repo / 'other.txt').write_text('main only\n', encoding='utf-8')
                git('add', '.')
                git('commit', '-q', '--amend', '--no-edit')
            script = root / 'choose.sh'
            # GitHub runs bash steps with -eo pipefail.
            script.write_text('set -eo pipefail\n' + self.STUB
                              + step_script('Choose the Windows suites from the source inventory', 'validate.yml'),
                              encoding='utf-8', newline='\n')
            output, summary, calls = root / 'output', root / 'summary', root / 'calls'
            for path in (output, summary, calls):
                path.touch()
            environment = {**self.ENV, 'EVENT_NAME': 'pull_request', 'BASE_REF': 'main', 'REPO': 'example/project',
                           'GITHUB_OUTPUT': str(output), 'GITHUB_STEP_SUMMARY': str(summary), 'CALLS': str(calls),
                           'STUB_RUN': run, 'STUB_JOB': job, 'STUB_PENDING': str(pending), 'STUB_FAIL': fail}
            result = subprocess.run(['bash', str(script)], cwd=repo, env=environment, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            values = dict(line.split('=', 1) for line in output.read_text(encoding='utf-8').splitlines())
            return (values, summary.read_text(encoding='utf-8'), calls.read_text(encoding='utf-8'),
                    git('rev-parse', 'HEAD^2'))

    def reused(self, values):
        return all(values[suite] == 'false' for suite in ('unit', 'desktop', 'storage'))

    def ran_all(self, values):
        return all(values[suite] == 'true' for suite in ('unit', 'desktop', 'storage'))

    def test_reuses_the_passing_beta_validation_of_the_promoted_commit(self):
        values, summary, calls, dev = self.choose()
        self.assertTrue(self.reused(values), values)
        self.assertIn('already passed', summary)
        self.assertIn(f'head_sha={dev}', calls)

    def test_reuses_a_passing_validation_even_when_publishing_failed(self):
        values, _, _, _ = self.choose(run='7 completed https://example.com/runs/7', job='completed success')
        self.assertTrue(self.reused(values), values)

    def test_waits_while_the_beta_validation_is_still_running(self):
        values, _, calls, _ = self.choose(pending=2)
        self.assertTrue(self.reused(values), values)
        self.assertEqual(2, calls.count('slept'))

    def test_stops_waiting_once_validation_finished_while_publishing_continues(self):
        values, _, calls, _ = self.choose(run='7 in_progress https://example.com/runs/7', job='completed success')
        self.assertTrue(self.reused(values), values)
        self.assertNotIn('slept', calls)

    def test_runs_every_suite_when_the_beta_validation_failed(self):
        values, summary, _, _ = self.choose(job='completed failure')
        self.assertTrue(self.ran_all(values), values)
        self.assertIn('no passing beta validation', summary)

    def test_runs_every_suite_when_the_beta_run_skipped_validation(self):
        values, _, _, _ = self.choose(job='none none')
        self.assertTrue(self.ran_all(values), values)

    def test_runs_every_suite_when_the_commit_has_no_beta_run(self):
        values, _, calls, _ = self.choose(run='none none')
        self.assertTrue(self.ran_all(values), values)
        self.assertNotIn('/jobs', calls)

    def test_runs_every_suite_when_the_api_fails(self):
        values, _, _, _ = self.choose(fail='1')
        self.assertTrue(self.ran_all(values), values)

    def test_runs_every_suite_without_asking_when_the_merge_differs_from_dev(self):
        values, summary, calls, _ = self.choose(differ=True)
        self.assertTrue(self.ran_all(values), values)
        self.assertIn('differs from dev', summary)
        self.assertEqual('', calls)

    def test_validate_only_runs_for_pull_requests_and_calls(self):
        workflow = (ROOT / '.github/workflows/validate.yml').read_text(encoding='utf-8')
        triggers = workflow.split('\non:\n', 1)[1].split('\npermissions:', 1)[0]
        self.assertNotIn('push:', triggers)
        beta = (ROOT / '.github/workflows/beta.yml').read_text(encoding='utf-8')
        # The reuse step looks the beta's aggregate job up by name.
        self.assertIn('name: Validate beta\n', beta)
        self.assertIn('  validate:\n    if:', workflow)
        self.assertIn('"Validate beta / validate"', workflow)


class ReleasePlanTests(unittest.TestCase):
    """Runs the actual release planning step against a fake gh with a synthetic release history."""

    SHA, PARENT = 'c' * 40, 'p' * 40
    STUB = textwrap.dedent(r"""
        gh() {
          local args="$*" prefix
          case "$args" in
            *"contents/Directory.Build.props?ref=$SHA"*) props "$STUB_PREFIX" ;;
            *"contents/Directory.Build.props?ref=$STUB_PARENT"*) props "$STUB_PREVIOUS" ;;
            *commits/main*) echo "$STUB_HEAD" ;;
            *"commits/$SHA"*) echo "$STUB_PARENT" ;;
            *target_commitish*) if [ -n "$STUB_EXISTING" ]; then echo "$STUB_EXISTING"; fi ;;
            *matching-refs/tags/*)
              prefix="${args#*matching-refs/tags/}"
              prefix="${prefix%% *}"
              printf '%s\n' $STUB_TAGS | awk -v p="$prefix" 'index($0, p) == 1' ;;
            */milestones*) ;;
            */releases*) printf '%s\n' $STUB_RELEASES ;;
            *) echo "unexpected gh call: $args" >&2; return 1 ;;
          esac
        }
        props() { printf '<Project>\n  <PropertyGroup>\n    <VersionPrefix>%s</VersionPrefix>\n  </PropertyGroup>\n</Project>\n' "$1"; }
    """)

    def plan(self, prefix='1.4.0', previous=None, releases=(), tags=(), head=SHA, existing=''):
        with tempfile.TemporaryDirectory(prefix='release-plan-') as temporary:
            root = Path(temporary)
            workflow = (ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8')
            body = workflow.split('      - id: plan\n', 1)[1].split('        run: |\n', 1)[1]
            lines = []
            for line in body.splitlines(keepends=True):
                if line.strip() and not line.startswith(' ' * 10):
                    break
                lines.append(line)
            script = root / 'plan.sh'
            # GitHub runs bash steps with -eo pipefail.
            script.write_text('set -eo pipefail\n' + self.STUB + textwrap.dedent(''.join(lines)),
                              encoding='utf-8', newline='\n')
            output, summary = root / 'output', root / 'summary'
            output.touch()
            environment = {**os.environ, 'REPO': 'example/project', 'SHA': self.SHA, 'STUB_PARENT': self.PARENT,
                           'GITHUB_OUTPUT': str(output), 'GITHUB_STEP_SUMMARY': str(summary),
                           'STUB_PREFIX': prefix, 'STUB_PREVIOUS': previous or prefix, 'STUB_HEAD': head,
                           'STUB_EXISTING': existing, 'STUB_RELEASES': ' '.join(releases), 'STUB_TAGS': ' '.join(tags)}
            result = subprocess.run(['bash', script.name], cwd=root, env=environment,
                                    capture_output=True, text=True)
            values = dict(line.split('=', 1) for line in output.read_text(encoding='utf-8').splitlines())
            return result, values

    def planned(self, **history):
        result, values = self.plan(**history)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual('true', values.get('release'), values)
        self.assertEqual(values['revision'], values['version'].split('.')[3])
        return values['version']

    def test_a_raised_version_prefix_starts_at_00_even_after_only_betas(self):
        history = ('v1.3.1.05', 'v1.4.0.00-beta.1')
        self.assertEqual('1.4.0.00', self.planned(previous='1.3.1', releases=history, tags=history))

    def test_the_next_merge_adds_one_to_the_counter(self):
        history = ('v1.4.0.00', 'v1.4.0.01-beta.3')
        self.assertEqual('1.4.0.01', self.planned(releases=history, tags=history))

    def test_a_first_release_is_four_parts_and_never_a_bridge(self):
        # Before 1.4.0, a .00 release with nothing newer than 1.2.0 published a three-part bridge.
        self.assertEqual('2.0.0.00', self.planned(prefix='2.0.0', previous='1.4.0'))

    def test_the_bridge_tag_counts_as_00(self):
        self.assertEqual('1.3.0.01', self.planned(prefix='1.3.0', releases=('v1.3.0',), tags=('v1.3.0',)))

    def test_refuses_a_raised_prefix_that_is_already_released(self):
        result, _ = self.plan(previous='1.3.1', releases=('v1.4.0.00',), tags=('v1.4.0.00',))
        self.assertNotEqual(0, result.returncode)
        self.assertIn('already released', result.stdout)

    def test_skips_a_commit_that_is_no_longer_the_tip_of_main(self):
        result, values = self.plan(head='d' * 40)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({'release': 'false'}, values)

    def test_skips_a_commit_that_already_has_a_release(self):
        result, values = self.plan(existing='https://example.com/releases/v1.4.0.00')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({'release': 'false'}, values)


if __name__ == '__main__':
    unittest.main()
