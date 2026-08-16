'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  MAX_FRAME_BYTES,
  computeAuthenticationMac,
  computeServerProof,
  encodeFrame,
  FrameDecoder
} = require('../protocol');

test('HMAC fixture matches the C# protocol fixture', () => {
  const secret = Buffer.from(Array.from({ length: 32 }, (_, index) => index));
  const challenge = Buffer.from(Array.from({ length: 32 }, (_, index) => index + 32));
  const nonce = Buffer.from(Array.from({ length: 32 }, (_, index) => index + 64));
  assert.equal(computeAuthenticationMac(secret, challenge, nonce).toString('hex'),
    '2089bf69fcee708a781bb4aca3e0c5c44be4128c8cf86417e9dea51d8b2fd59e');
  assert.equal(computeServerProof(secret, challenge, nonce).toString('hex'),
    '8ea5bc5f851650fb2bb9fea6968009f0347684961f0aac9c684d7bfab58d6c51');
});

test('length-prefixed decoder handles split and coalesced frames', () => {
  const first = encodeFrame({ type: 'one', value: 1 });
  const second = encodeFrame({ type: 'two', value: 2 });
  const decoder = new FrameDecoder();
  assert.deepEqual(decoder.push(first.subarray(0, 3)), []);
  assert.deepEqual(decoder.push(Buffer.concat([first.subarray(3), second])), [
    { type: 'one', value: 1 },
    { type: 'two', value: 2 }
  ]);
});

test('encoder and decoder reject frames above 256 KiB', () => {
  assert.throws(() => encodeFrame({ value: 'x'.repeat(MAX_FRAME_BYTES) }), /256 KiB/);
  const decoder = new FrameDecoder();
  const invalid = Buffer.alloc(4);
  invalid.writeUInt32LE(MAX_FRAME_BYTES + 1);
  assert.throws(() => decoder.push(invalid), /invalid length/);
  assert.throws(() => new FrameDecoder().push(Buffer.alloc(MAX_FRAME_BYTES + 5)), /buffered frame/);
});
