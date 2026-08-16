'use strict';

const crypto = require('node:crypto');

const PROTOCOL_VERSION = 1;
const MAX_FRAME_BYTES = 256 * 1024;
const AUTHENTICATION_BYTES = 32;
const AUTHENTICATION_CONTEXT = Buffer.from('K.netagent VS Code bridge v1\0', 'utf8');
const SERVER_PROOF_CONTEXT = Buffer.from('K.netagent VS Code bridge server v1\0', 'utf8');

function computeAuthenticationMac(secret, challenge, clientNonce) {
  return computeMac(secret, challenge, clientNonce, AUTHENTICATION_CONTEXT);
}

function computeServerProof(secret, challenge, clientNonce) {
  return computeMac(secret, challenge, clientNonce, SERVER_PROOF_CONTEXT);
}

function computeMac(secret, challenge, clientNonce, context) {
  for (const [name, value] of [['secret', secret], ['challenge', challenge], ['clientNonce', clientNonce]]) {
    if (!Buffer.isBuffer(value) || value.length !== AUTHENTICATION_BYTES) {
      throw new TypeError(`${name} must be a 32-byte Buffer`);
    }
  }
  return crypto.createHmac('sha256', secret)
    .update(context)
    .update(challenge)
    .update(clientNonce)
    .digest();
}

function encodeFrame(value) {
  const payload = Buffer.from(JSON.stringify(value), 'utf8');
  if (payload.length === 0 || payload.length > MAX_FRAME_BYTES) {
    throw new RangeError('VS Code bridge frame exceeds the 256 KiB limit');
  }
  const frame = Buffer.allocUnsafe(4 + payload.length);
  frame.writeUInt32LE(payload.length, 0);
  payload.copy(frame, 4);
  return frame;
}

class FrameDecoder {
  constructor() {
    this.buffer = Buffer.alloc(0);
  }

  push(chunk) {
    if (!Buffer.isBuffer(chunk)) throw new TypeError('frame chunk must be a Buffer');
    if (this.buffer.length + chunk.length > MAX_FRAME_BYTES + 4) {
      this.buffer = Buffer.alloc(0);
      throw new RangeError('VS Code bridge buffered frame exceeds the 256 KiB limit');
    }
    this.buffer = this.buffer.length === 0 ? chunk : Buffer.concat([this.buffer, chunk]);
    const values = [];
    while (this.buffer.length >= 4) {
      const length = this.buffer.readUInt32LE(0);
      if (length === 0 || length > MAX_FRAME_BYTES) {
        this.buffer = Buffer.alloc(0);
        throw new RangeError('VS Code bridge frame has an invalid length');
      }
      if (this.buffer.length < 4 + length) break;
      const payload = this.buffer.subarray(4, 4 + length);
      this.buffer = this.buffer.subarray(4 + length);
      let value;
      try { value = JSON.parse(payload.toString('utf8')); }
      catch { throw new SyntaxError('VS Code bridge frame is not valid JSON'); }
      values.push(value);
    }
    return values;
  }

  clear() {
    this.buffer = Buffer.alloc(0);
  }
}

function sendFrame(socket, value) {
  socket.write(encodeFrame(value));
}

module.exports = {
  PROTOCOL_VERSION,
  MAX_FRAME_BYTES,
  AUTHENTICATION_BYTES,
  computeAuthenticationMac,
  computeServerProof,
  encodeFrame,
  FrameDecoder,
  sendFrame
};
