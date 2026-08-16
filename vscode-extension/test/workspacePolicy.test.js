'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { isTaskInPairedWorkspace } = require('../workspacePolicy');

const taskScope = { Workspace: 1, Global: 2 };
const paired = { uri: { scheme: 'file', fsPath: 'C:\\work\\agent' } };

test('task policy accepts workspace and the exact paired folder only', () => {
  assert.equal(isTaskInPairedWorkspace({ scope: taskScope.Workspace }, paired, taskScope), true);
  assert.equal(isTaskInPairedWorkspace({ scope: paired }, paired, taskScope), true);
  assert.equal(isTaskInPairedWorkspace({ scope: { uri: { scheme: 'file', fsPath: 'C:\\work\\other' } } }, paired, taskScope), false);
});

test('task policy rejects global, unknown, virtual, and missing scopes', () => {
  assert.equal(isTaskInPairedWorkspace({ scope: taskScope.Global }, paired, taskScope), false);
  assert.equal(isTaskInPairedWorkspace({ scope: undefined }, paired, taskScope), false);
  assert.equal(isTaskInPairedWorkspace({ scope: { uri: { scheme: 'vscode-remote', fsPath: 'C:\\work\\agent' } } }, paired, taskScope), false);
  assert.equal(isTaskInPairedWorkspace({}, paired, taskScope), false);
});
