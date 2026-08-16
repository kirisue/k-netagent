'use strict';

const vscode = require('vscode');
const crypto = require('node:crypto');
const net = require('node:net');
const path = require('node:path');
const {
  PROTOCOL_VERSION,
  AUTHENTICATION_BYTES,
  computeAuthenticationMac,
  computeServerProof,
  FrameDecoder,
  sendFrame
} = require('./protocol');
const { isTaskInPairedWorkspace } = require('./workspacePolicy');

let pairing = null;
let socket = null;
let reconnectTimer = null;
let reconnectDelay = 250;
let reconnectAttempts = 0;
let manuallyDisconnected = false;

function activate(context) {
  context.subscriptions.push(
    vscode.commands.registerCommand('knetagent.pairReadOnlyBridge', pair),
    vscode.commands.registerCommand('knetagent.disconnectReadOnlyBridge', () => disconnect(true)),
    vscode.workspace.onDidChangeWorkspaceFolders(() => disconnect(true)),
    vscode.workspace.onDidGrantWorkspaceTrust(() => {
      if (pairing && !socket) scheduleReconnect();
    })
  );
}

async function pair() {
  const claim = currentWorkspaceClaim();
  if (!claim.ok) {
    await vscode.window.showErrorMessage(claim.error);
    return;
  }
  let input = await vscode.window.showInputBox({
    title: 'Pair K.netagent read-only bridge',
    prompt: 'Paste the in-memory pairing JSON copied by K.netagent. It is not saved by this extension.',
    password: true,
    ignoreFocusOut: true
  });
  if (!input) return;
  let parsed;
  try {
    parsed = JSON.parse(input);
    input = undefined;
    if (parsed.protocol !== PROTOCOL_VERSION || typeof parsed.pipeName !== 'string' ||
        !/^knetagent-[a-f0-9]{32}$/i.test(parsed.pipeName) || typeof parsed.secret !== 'string') {
      throw new Error('shape');
    }
    const pipeName = parsed.pipeName;
    const secret = Buffer.from(parsed.secret, 'base64');
    parsed.secret = '';
    parsed = null;
    if (secret.length !== AUTHENTICATION_BYTES) {
      secret.fill(0);
      throw new Error('secret');
    }
    disconnect(true);
    pairing = { pipeName, secret };
    manuallyDisconnected = false;
    reconnectDelay = 250;
    reconnectAttempts = 0;
    connect();
  } catch {
    input = undefined;
    disconnect(true);
    await vscode.window.showErrorMessage('The K.netagent pairing JSON is invalid.');
  }
}

function connect() {
  if (!pairing || socket) return;
  const claim = currentWorkspaceClaim();
  if (!claim.ok) {
    disconnect(true);
    void vscode.window.showErrorMessage(claim.error);
    return;
  }
  const candidate = net.createConnection(`\\\\.\\pipe\\${pairing.pipeName}`);
  const decoder = new FrameDecoder();
  const state = { ready: false, authenticated: false, expectedServerProof: null };
  let processing = Promise.resolve();
  socket = candidate;
  candidate.on('data', chunk => {
    let frames;
    try { frames = decoder.push(chunk); }
    catch { candidate.destroy(); return; }
    for (const frame of frames) {
      processing = processing.then(() => handleFrame(candidate, frame, claim.value, state)).then(result => {
        if (result === 'ready') {
          reconnectDelay = 250;
          reconnectAttempts = 0;
          candidate.setTimeout(0);
          void vscode.window.showInformationMessage('K.netagent read-only bridge paired.');
        }
      }).catch(() => candidate.destroy());
    }
  });
  candidate.on('error', () => { /* The close handler performs bounded reconnect. */ });
  candidate.on('close', () => {
    decoder.clear();
    if (state.expectedServerProof) state.expectedServerProof.fill(0);
    state.expectedServerProof = null;
    if (socket === candidate) socket = null;
    if (pairing && !manuallyDisconnected) scheduleReconnect();
  });
  candidate.setTimeout(10_000, () => {
    if (!state.ready || !state.authenticated) candidate.destroy();
  });
}

async function handleFrame(candidate, frame, workspaceClaim, state) {
  if (!frame || typeof frame !== 'object' || typeof frame.type !== 'string') throw new Error('invalid frame');
  if (frame.type === 'challenge') {
    if (!pairing || frame.protocol !== PROTOCOL_VERSION || typeof frame.challenge !== 'string') throw new Error('challenge');
    const challenge = Buffer.from(frame.challenge, 'base64');
    if (challenge.length !== AUTHENTICATION_BYTES) throw new Error('challenge length');
    const clientNonce = crypto.randomBytes(AUTHENTICATION_BYTES);
    const mac = computeAuthenticationMac(pairing.secret, challenge, clientNonce);
    const serverProof = computeServerProof(pairing.secret, challenge, clientNonce);
    try {
      if (state.expectedServerProof) state.expectedServerProof.fill(0);
      state.expectedServerProof = Buffer.from(serverProof);
      sendFrame(candidate, {
        type: 'authenticate',
        protocol: PROTOCOL_VERSION,
        clientNonce: clientNonce.toString('base64'),
        mac: mac.toString('base64')
      });
    } finally {
      challenge.fill(0);
      clientNonce.fill(0);
      mac.fill(0);
      serverProof.fill(0);
    }
    return;
  }
  if (frame.type === 'authenticated') {
    if (frame.protocol !== PROTOCOL_VERSION || typeof frame.serverMac !== 'string' || !state.expectedServerProof)
      throw new Error('protocol');
    const supplied = Buffer.from(frame.serverMac, 'base64');
    const valid = supplied.length === AUTHENTICATION_BYTES &&
      crypto.timingSafeEqual(supplied, state.expectedServerProof);
    supplied.fill(0);
    state.expectedServerProof.fill(0);
    state.expectedServerProof = null;
    if (!valid) throw new Error('server proof');
    state.authenticated = true;
    sendFrame(candidate, { type: 'workspace', ...workspaceClaim });
    return;
  }
  if (frame.type === 'ready') {
    if (frame.protocol !== PROTOCOL_VERSION || !state.authenticated) throw new Error('protocol');
    state.ready = true;
    return 'ready';
  }
  if (frame.type === 'ping') {
    if (!state.ready || typeof frame.id !== 'string' || !/^[a-f0-9]{32}$/i.test(frame.id)) {
      throw new Error('heartbeat');
    }
    sendFrame(candidate, { type: 'pong', id: frame.id });
    return;
  }
  if (frame.type === 'request') {
    if (!state.ready) throw new Error('not ready');
    if (typeof frame.id !== 'string' || !/^[a-f0-9]{32}$/i.test(frame.id) || typeof frame.operation !== 'string')
      throw new Error('request');
    const current = currentWorkspaceClaim();
    if (!current.ok || JSON.stringify(current.value) !== JSON.stringify(workspaceClaim)) {
      sendFrame(candidate, { type: 'response', id: frame.id, ok: false, error: { code: 'workspace_changed' } });
      candidate.destroy();
      return;
    }
    try {
      const result = await query(frame.operation, vscode.workspace.workspaceFolders[0]);
      sendFrame(candidate, { type: 'response', id: frame.id, ok: true, result });
    } catch {
      sendFrame(candidate, { type: 'response', id: frame.id, ok: false, error: { code: 'query_failed' } });
    }
    return;
  }
  throw new Error('unexpected frame');
}

async function query(operation, folder) {
  switch (operation) {
    case 'activeEditor': return activeEditor(folder);
    case 'diagnostics': return diagnostics(folder);
    case 'tasks': return tasks(folder);
    case 'extensions': return extensions();
    default: throw new Error('unsupported operation');
  }
}

function activeEditor(folder) {
  const editor = vscode.window.activeTextEditor;
  const relativePath = editor ? relativeFilePath(editor.document.uri, folder) : null;
  if (!editor || !relativePath) return { hasEditor: false };
  return {
    hasEditor: true,
    relativePath,
    languageId: safeText(editor.document.languageId, 80),
    isDirty: Boolean(editor.document.isDirty),
    selection: range(editor.selection),
    visibleRanges: editor.visibleRanges.slice(0, 20).map(range)
  };
}

function diagnostics(folder) {
  const items = [];
  let total = 0;
  for (const [uri, diagnosticsForFile] of vscode.languages.getDiagnostics()) {
    const relativePath = relativeFilePath(uri, folder);
    if (!relativePath) continue;
    total += diagnosticsForFile.length;
    for (const diagnostic of diagnosticsForFile) {
      if (items.length >= 100) break;
      items.push({
        relativePath,
        severity: severity(diagnostic.severity),
        code: safeText(typeof diagnostic.code === 'object' ? diagnostic.code?.value : diagnostic.code, 80),
        source: safeText(diagnostic.source, 80),
        startLine: diagnostic.range.start.line + 1,
        startCharacter: diagnostic.range.start.character + 1,
        endLine: diagnostic.range.end.line + 1,
        endCharacter: diagnostic.range.end.character + 1
      });
    }
  }
  return { total, truncated: total > items.length, items };
}

async function tasks(folder) {
  const fetched = await vscode.tasks.fetchTasks();
  const eligible = fetched.filter(task => isTaskInPairedWorkspace(task, folder, vscode.TaskScope));
  const items = eligible.slice(0, 100).map(task => ({
    name: safeText(task.name, 240),
    source: safeText(task.source, 120),
    definitionType: safeText(task.definition?.type, 80),
    scope: task.scope === vscode.TaskScope.Workspace ? 'workspace' : 'folder',
    group: safeText(task.group?.id, 80),
    isBackground: Boolean(task.isBackground)
  }));
  return { total: eligible.length, truncated: eligible.length > items.length, items };
}

function extensions() {
  const all = vscode.extensions.all;
  const items = all.slice(0, 200).map(extension => ({
    id: safeText(extension.id, 160).toLowerCase(),
    displayName: safeText(extension.packageJSON?.displayName, 240),
    version: safeText(extension.packageJSON?.version, 60),
    isActive: Boolean(extension.isActive),
    extensionKind: Array.isArray(extension.extensionKind)
      ? extension.extensionKind.map(value => String(value)).slice(0, 4).join(',')
      : safeText(extension.extensionKind, 80)
  }));
  return { total: all.length, truncated: all.length > items.length, items };
}

function currentWorkspaceClaim() {
  const folders = vscode.workspace.workspaceFolders;
  if (!vscode.workspace.isTrusted) return { ok: false, error: 'Trust this VS Code workspace before pairing K.netagent.' };
  if (vscode.env.uiKind !== vscode.UIKind.Desktop || vscode.env.remoteName) return { ok: false, error: 'Only a local desktop VS Code window can pair.' };
  if (!folders || folders.length !== 1 || folders[0].uri.scheme !== 'file') return { ok: false, error: 'Open exactly one local folder in VS Code before pairing.' };
  return {
    ok: true,
    value: {
      trusted: true,
      uiKind: 'desktop',
      remoteName: null,
      folders: [{ scheme: 'file', path: folders[0].uri.fsPath }]
    }
  };
}

function relativeFilePath(uri, folder) {
  if (!uri || uri.scheme !== 'file') return null;
  const value = path.relative(folder.uri.fsPath, uri.fsPath);
  if (!value || value.startsWith(`..${path.sep}`) || value === '..' || path.isAbsolute(value)) return null;
  return value.split(path.sep).join('/');
}

function range(value) {
  return {
    startLine: value.start.line + 1,
    startCharacter: value.start.character + 1,
    endLine: value.end.line + 1,
    endCharacter: value.end.character + 1
  };
}

function severity(value) {
  return value === vscode.DiagnosticSeverity.Error ? 'Error' :
    value === vscode.DiagnosticSeverity.Warning ? 'Warning' :
      value === vscode.DiagnosticSeverity.Information ? 'Information' : 'Hint';
}

function safeText(value, max) {
  if (value === undefined || value === null) return '';
  return String(value).replace(/[\u0000-\u001f]+/g, ' ').trim().slice(0, max);
}

function scheduleReconnect() {
  if (reconnectTimer || !pairing || manuallyDisconnected) return;
  if (reconnectAttempts >= 8) {
    disconnect(true);
    void vscode.window.showWarningMessage('K.netagent bridge could not reconnect. Generate a new in-memory pairing code and pair again.');
    return;
  }
  reconnectAttempts += 1;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, reconnectDelay);
  reconnectDelay = Math.min(reconnectDelay * 2, 5_000);
}

function disconnect(forgetPairing) {
  manuallyDisconnected = Boolean(forgetPairing);
  if (reconnectTimer) clearTimeout(reconnectTimer);
  reconnectTimer = null;
  if (socket) socket.destroy();
  socket = null;
  if (forgetPairing && pairing) {
    pairing.secret.fill(0);
    pairing = null;
  }
}

function deactivate() {
  disconnect(true);
}

module.exports = { activate, deactivate };
