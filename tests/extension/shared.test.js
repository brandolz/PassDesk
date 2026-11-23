'use strict';
// Validación de las respuestas del conector (extension/src/shared.js), sin navegador: node --test shared.test.js
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

const load = () => import(pathToFileURL(path.join(__dirname, '..', '..', 'extension', 'src', 'shared.js')).href);

test('un secreto de más de 4096 caracteres no se recorta: se rechaza con su propio error (P-7)', async () => {
  const { normalizeReply, PROTOCOL_VERSION } = await load();
  const reply = (fields) => normalizeReply({ v: PROTOCOL_VERSION, ok: true, rid: 'r1', ...fields }, 'r1', null);

  const max = 'x'.repeat(4096);
  assert.deepEqual(reply({ username: 'u', password: max }), { ok: true, username: 'u', password: max });
  assert.deepEqual(reply({ username: 'u', password: max + 'x' }), { ok: false, error: 'too_long' });
  assert.deepEqual(reply({ username: max + 'x', password: 'p' }), { ok: false, error: 'too_long' });
  assert.deepEqual(reply({ password: 42 }), { ok: false, error: 'bad_reply' });
});
