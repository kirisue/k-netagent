'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const extensionRoot = path.resolve(__dirname, '..');
const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.json'), 'utf8'));
const ignored = fs.readFileSync(path.join(extensionRoot, '.vscodeignore'), 'utf8')
  .split(/\r?\n/u)
  .map(value => value.trim())
  .filter(Boolean);

test('maintainer packaging uses an exact official vsce version', () => {
  assert.equal(manifest.private, true, 'the bridge must remain non-publishable through npm');
  assert.deepEqual(manifest.dependencies, undefined, 'the installed extension must not need npm dependencies');
  assert.deepEqual(manifest.repository, {
    type: 'git',
    url: 'https://github.com/kirisue/k-netagent.git',
    directory: 'vscode-extension'
  });
  assert.match(manifest.devDependencies?.['@vscode/vsce'] ?? '', /^\d+\.\d+\.\d+$/u);
  assert.equal(manifest.devDependencies['@vscode/vsce'], '3.9.2');
  assert.equal(manifest.scripts?.['package:vsix'], 'vsce package --no-dependencies');
  assert.equal(manifest.scripts?.['vscode:prepublish'], 'npm test');
});

test('VSIX ignore rules exclude development and credential material', () => {
  for (const required of [
    'node_modules/**',
    'test/**',
    'package-lock.json',
    '.env',
    '.env.*',
    '*.key',
    '*.pem',
    '*.p12',
    '*.pfx',
    'secrets/**',
    'credentials/**'
  ]) {
    assert.ok(ignored.includes(required), `missing .vscodeignore rule: ${required}`);
  }
});
