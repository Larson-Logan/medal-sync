import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
const script = path.join(root, 'scripts/storage/medal-sync/set-medal-sync-exclusions.ps1');
const available = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']).status === 0;
function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'medal-exclusions-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const files = ['local', 'server'].map(name => path.join(dir, name, '.stignore'));
  for (const file of files) { fs.mkdirSync(path.dirname(file)); fs.writeFileSync(file, '// keep comments\r\n!important\r\n*.tmp\r\n'); }
  return { dir, files, backup: path.join(dir, 'private', 'Backups') };
}
function run(f, apply = false, before = '', extra = {}) {
  return spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', `
    $ErrorActionPreference = 'Stop'
    ${before}
    $pattern = if ($env:T_PATTERN) { $env:T_PATTERN } else { '/Videos/Clips' }
    & $env:T_SCRIPT -IgnoreFile @($env:T_FILES | ConvertFrom-Json) -Pattern $pattern -BackupRoot $env:T_BACKUP -Apply:($env:T_APPLY -eq '1')
  `], { encoding: 'utf8', env: { ...process.env, T_SCRIPT: script, T_FILES: JSON.stringify(f.files), T_BACKUP: f.backup, T_APPLY: apply ? '1' : '0', ...extra } });
}

test('exclusion preview writes nothing; apply preserves hidden files, backs up originals and is repeatable', { skip: !available }, t => {
  const f = fixture(t);
  const originals = f.files.map(file => fs.readFileSync(file));
  let result = run(f);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Preview only/);
  assert.ok(!fs.existsSync(f.backup));
  assert.deepEqual(f.files.map(file => fs.readFileSync(file)), originals);
  const hidden = process.platform === 'win32' ? `[IO.File]::SetAttributes(($env:T_FILES | ConvertFrom-Json)[0], [IO.FileAttributes]::Hidden)` : '';
  result = run(f, true, hidden);
  assert.equal(result.status, 0, result.stderr);
  const directories = fs.readdirSync(f.backup);
  assert.equal(directories.length, 1);
  const receipt = JSON.parse(fs.readFileSync(path.join(f.backup, directories[0], 'files.json'), 'utf8'));
  assert.equal(receipt.length, 2);
  for (let i = 0; i < f.files.length; i++) {
    assert.deepEqual(fs.readFileSync(receipt[i].backup), originals[i]);
    assert.equal(fs.readFileSync(f.files[i], 'utf8'), '/Videos/Clips\n// keep comments\n!important\n*.tmp\n');
  }
  result = run(f, true);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(fs.readdirSync(f.backup), directories);
});

test('exclusions refuse a backup inside a synced folder and an absent peer without partial writes', { skip: !available }, t => {
  const f = fixture(t);
  const original = fs.readFileSync(f.files[0]);
  let result = run(f, true, '', { T_BACKUP: path.join(path.dirname(f.files[0]), 'backups') });
  assert.notEqual(result.status, 0);
  assert.deepEqual(fs.readFileSync(f.files[0]), original);
  fs.unlinkSync(f.files[1]);
  result = run(f, true);
  assert.notEqual(result.status, 0);
  assert.ok(!fs.existsSync(f.backup));
  assert.deepEqual(fs.readFileSync(f.files[0]), original);
});

test('a locked second peer rolls back the first write and retains both backups', { skip: !(available && process.platform === 'win32') }, t => {
  const f = fixture(t);
  const originals = f.files.map(file => fs.readFileSync(file));
  const result = run(f, true, `
    $second = ($env:T_FILES | ConvertFrom-Json)[1]
    $held = [IO.File]::Open($second, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
  `);
  assert.notEqual(result.status, 0);
  assert.deepEqual(f.files.map(file => fs.readFileSync(file)), originals);
  assert.equal(fs.readdirSync(f.backup).length, 1);
});

test('broad, relative and ambiguous exclusion patterns are refused before writing', { skip: !available }, t => {
  const f = fixture(t);
  const originals = f.files.map(file => fs.readFileSync(file));
  for (const pattern of ['/', '//Videos/Clips', '/Videos/*', '/Videos/../Clips', '/Videos//Clips']) {
    assert.notEqual(run(f, true, '', { T_PATTERN: pattern }).status, 0, pattern);
  }
  assert.deepEqual(f.files.map(file => fs.readFileSync(file)), originals);
  assert.ok(!fs.existsSync(f.backup));
});
