// The medal-sync scripts must change nothing unless the owner passes -Apply or -Install.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
const scripts = path.join(root, 'scripts/storage/medal-sync');
const pins = JSON.parse(fs.readFileSync(path.join(root, 'tools/medal-sync/config/dependencies.json'), 'utf8'));
const windows = process.platform === 'win32';
const pwsh = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']).status === 0;

// PowerShell keeps its own caches under LOCALAPPDATA\Microsoft; anything else is ours.
function ourFiles(directory) {
  return fs.readdirSync(directory).filter((name) => name !== 'Microsoft');
}

test('pinned downloads name their version and carry a SHA-256 digest', () => {
  assert.match(pins.rclone.sha256, /^[0-9a-f]{64}$/);
  assert.match(pins.winfsp.sha256, /^[0-9a-f]{64}$/);
  assert.ok(pins.rclone.url.startsWith('https://downloads.rclone.org/'));
  assert.ok(pins.rclone.url.endsWith(`/${pins.rclone.version}/${pins.rclone.asset}`));
  assert.ok(pins.winfsp.url.startsWith('https://github.com/winfsp/winfsp/releases/download/'));
  assert.ok(pins.winfsp.url.endsWith(`/${pins.winfsp.asset}`));
});

test('dependency preview writes nothing and installs nothing', { skip: !(windows && pwsh) }, (t) => {
  const localAppData = fs.mkdtempSync(path.join(os.tmpdir(), 'medal-sync-preview-'));
  t.after(() => fs.rmSync(localAppData, { recursive: true, force: true }));
  const result = spawnSync('pwsh', ['-NoProfile', '-File', path.join(scripts, 'install-medal-sync-dependencies.ps1'), '-StateRoot', path.join(localAppData, 'state')], {
    env: { ...process.env, LOCALAPPDATA: localAppData },
    encoding: 'utf8',
  });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Preview: with -Apply, rclone v\d+\.\d+\.\d+ is downloaded/);
  assert.ok(result.stdout.includes(pins.rclone.sha256));
  assert.match(result.stdout, /WinFsp: (installed at|NOT installed)/);
  assert.deepEqual(ourFiles(localAppData), []);
});

test('scripts never install WinFsp or touch Medal, and keep downloads behind -Apply', () => {
  const install = fs.readFileSync(path.join(scripts, 'install-medal-sync-dependencies.ps1'), 'utf8').replace(/\r\n/g, '\n');
  const publish = fs.readFileSync(path.join(scripts, 'publish-medal-sync.ps1'), 'utf8');
  for (const source of [install, publish]) {
    assert.doesNotMatch(source, /msiexec|Start-Process|RunAs|-Verb/i);
    assert.doesNotMatch(source, /AppData\\Roaming\\Medal|\\Medal\\|Clips/);
  }
  const download = install.indexOf('Invoke-WebRequest');
  const gate = install.indexOf('if (-not $Apply) {\n    Write-Host "Preview');
  assert.ok(gate > 0 && download > gate, 'the download must come after the preview return');
  assert.ok(publish.includes('-p:RestoreLockedMode=true'), 'publish must restore from the lock file');
});

test('the extracted rclone executable has its own reviewed pin', () => {
  assert.match(pins.rclone.exeSha256, /^[0-9a-f]{64}$/);
  const install = fs.readFileSync(path.join(scripts, 'install-medal-sync-dependencies.ps1'), 'utf8');
  assert.ok(install.includes('Pinned rclone already installed; no download or replacement needed.'));
});
