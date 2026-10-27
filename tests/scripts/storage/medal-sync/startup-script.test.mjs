// set-medal-startup.ps1 rewires Medal's startup entries; it must change nothing
// without -Apply, keep the first saved originals, and restore them exactly.
// The round trip runs against a scratch HKCU key and scratch shortcut folders.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
const script = path.join(root, 'scripts/storage/medal-sync/set-medal-startup.ps1');
const windows = process.platform === 'win32';
const pwsh = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']).status === 0;

function powershell(command, env) {
  return spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', command], {
    env: { ...process.env, ...env },
    encoding: 'utf8',
  });
}

function scratch(t) {
  // The long form: shortcuts report long paths even when TEMP uses an 8.3 name.
  const dir = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'medal-startup-')));
  const env = {
    T_SOFT: `HKCU:\\Software\\LarsonServer\\MedalSyncScriptTest-${crypto.randomBytes(6).toString('hex')}`,
    T_STATE: path.join(dir, 'state'),
    T_LINKS: path.join(dir, 'links'),
    T_MEDAL: path.join(dir, 'Medal', 'current', 'Medal.exe'),
  };
  env.T_LAUNCHER = path.join(env.T_STATE, 'bin', 'medal-sync-launch.exe');
  env.T_LNK = path.join(env.T_LINKS, 'Medal.lnk');
  for (const file of [env.T_MEDAL, env.T_LAUNCHER]) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, '');
  }
  fs.mkdirSync(env.T_LINKS);
  t.after(() => {
    powershell('Remove-Item -LiteralPath $env:T_SOFT -Recurse -Force -ErrorAction SilentlyContinue', env);
    fs.rmSync(dir, { recursive: true, force: true });
  });
  const setup = powershell(`
    $run = Join-Path $env:T_SOFT 'Microsoft\\Windows\\CurrentVersion\\Run'
    New-Item -Path $run -Force | Out-Null
    New-ItemProperty -LiteralPath $run -Name Medal -Value ('"' + $env:T_MEDAL + '"') -PropertyType String | Out-Null
    New-ItemProperty -LiteralPath $run -Name Other -Value 'C:\\Tools\\other.exe' -PropertyType String | Out-Null
    $protocol = Join-Path $env:T_SOFT 'Classes\\medal\\shell\\open\\command'
    New-Item -Path $protocol -Force | Out-Null
    Set-Item -LiteralPath $protocol -Value ('"' + $env:T_MEDAL + '" "%1"')
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($env:T_LNK)
    $link.TargetPath = $env:T_MEDAL
    $link.Save()`, env);
  assert.equal(setup.status, 0, setup.stderr);
  return env;
}

function state(env) {
  const result = powershell(`
    $key = Get-Item -LiteralPath (Join-Path $env:T_SOFT 'Microsoft\\Windows\\CurrentVersion\\Run')
    $run = [ordered]@{}
    foreach ($name in $key.GetValueNames()) { $run[$name] = $key.GetValue($name) }
    $protocol = (Get-Item -LiteralPath (Join-Path $env:T_SOFT 'Classes\\medal\\shell\\open\\command')).GetValue('')
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($env:T_LNK).TargetPath
    [ordered]@{ run = $run; protocol = $protocol; target = $target } | ConvertTo-Json -Compress`, env);
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}

function run(env, ...args) {
  return spawnSync('pwsh', ['-NoProfile', '-NonInteractive', '-File', script, ...args,
    '-StateRoot', env.T_STATE, '-SoftwareKey', env.T_SOFT, '-ShortcutFolder', env.T_LINKS], { encoding: 'utf8' });
}

test('preview reports the entries and changes nothing', { skip: !(windows && pwsh) }, (t) => {
  const env = scratch(t);
  const before = state(env);
  const result = run(env);
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Run value 'Medal': STARTS MEDAL DIRECTLY/);
  assert.match(result.stdout, /Preview only; nothing was changed\./);
  assert.deepEqual(state(env), before);
  assert.deepEqual(fs.readdirSync(env.T_STATE), ['bin']);
});

test('apply, a Medal update, re-apply and restore round-trip exactly', { skip: !(windows && pwsh) }, (t) => {
  const env = scratch(t);
  const original = state(env);
  const launcherRun = `"${env.T_LAUNCHER}" --at-sign-in`;

  let result = run(env, '-Apply');
  assert.equal(result.status, 0, result.stderr);
  const applied = state(env);
  assert.deepEqual(applied.run, { Other: 'C:\\Tools\\other.exe', MedalSync: launcherRun });
  assert.equal(applied.protocol, `"${env.T_LAUNCHER}" "%1"`);
  assert.equal(applied.target.toLowerCase(), env.T_LAUNCHER.toLowerCase());
  const stable = path.join(env.T_LINKS, 'Medal Library.lnk');
  assert.ok(fs.existsSync(stable));
  const stableTarget = powershell(`(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $env:T_LINKS 'Medal Library.lnk')).TargetPath`, env);
  assert.equal(stableTarget.stdout.trim().toLowerCase(), env.T_LAUNCHER.toLowerCase());
  const originals = fs.readFileSync(path.join(env.T_STATE, 'startup-originals.json'), 'utf8');
  assert.equal(run(env, '-Check').status, 0);

  // A Medal update restores its own Run value: -Check notices, -Apply fixes it
  // and the first saved originals are kept.
  powershell(`New-ItemProperty -LiteralPath (Join-Path $env:T_SOFT 'Microsoft\\Windows\\CurrentVersion\\Run') -Name Medal -Value ('"' + $env:T_MEDAL + '"') -PropertyType String -Force | Out-Null`, env);
  assert.equal(run(env, '-Check').status, 1);
  result = run(env, '-Apply');
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(state(env).run, applied.run);
  assert.equal(fs.readFileSync(path.join(env.T_STATE, 'startup-originals.json'), 'utf8'), originals);

  result = run(env, '-Restore');
  assert.equal(result.status, 0, result.stderr);
  const restored = state(env);
  assert.deepEqual(restored.run, { Other: 'C:\\Tools\\other.exe', Medal: original.run.Medal });
  assert.equal(restored.protocol, original.protocol);
  assert.equal(restored.target.toLowerCase(), original.target.toLowerCase());
  assert.ok(!fs.existsSync(stable), 'only the helper-created stable shortcut is removed on restore');
  assert.ok(!fs.existsSync(path.join(env.T_STATE, 'startup-originals.json')), 'the originals file is retired');
  assert.ok(fs.readdirSync(env.T_STATE).some((name) => name.startsWith('startup-originals.json.restored-')));
});

test('an unrelated stable-shortcut name is refused before startup mutation', { skip: !(windows && pwsh) }, (t) => {
  const env = scratch(t);
  const before = state(env);
  powershell(`$link=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $env:T_LINKS 'Medal Library.lnk')); $link.TargetPath='C:\\Tools\\unrelated.exe'; $link.Save()`, env);
  const result = run(env, '-Apply');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /unrelated shortcut/);
  assert.deepEqual(state(env), before);
  assert.ok(!fs.existsSync(path.join(env.T_STATE, 'startup-originals.json')));
});

test('the script stays per-user and starts nothing', () => {
  const source = fs.readFileSync(script, 'utf8');
  assert.doesNotMatch(source, /HKLM:|HKEY_LOCAL_MACHINE|Start-Process|RunAs|-Verb|Invoke-WebRequest/i);
  assert.ok(source.includes("Only per-user (HKCU) registry keys are changed."));
  const preview = source.indexOf("Write-Host 'Preview only; nothing was changed.'");
  const firstApplyWrite = source.indexOf('New-ItemProperty -LiteralPath $runKey -Name $runValueName');
  assert.ok(preview > 0 && firstApplyWrite > preview, 'apply writes come after the preview exit');
});
