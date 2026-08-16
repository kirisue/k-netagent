'use strict';

const path = require('node:path');

function isTaskInPairedWorkspace(task, pairedFolder, taskScope) {
  if (!task || !pairedFolder || !taskScope) return false;
  if (task.scope === taskScope.Global || task.scope === undefined || task.scope === null) return false;
  if (task.scope === taskScope.Workspace) return true;
  return isSameLocalFolder(task.scope, pairedFolder);
}

function isSameLocalFolder(candidate, expected) {
  if (!candidate?.uri || !expected?.uri || candidate.uri.scheme !== 'file' || expected.uri.scheme !== 'file') {
    return false;
  }
  const left = path.resolve(candidate.uri.fsPath);
  const right = path.resolve(expected.uri.fsPath);
  return process.platform === 'win32'
    ? left.localeCompare(right, undefined, { sensitivity: 'accent' }) === 0
    : left === right;
}

module.exports = { isTaskInPairedWorkspace };
