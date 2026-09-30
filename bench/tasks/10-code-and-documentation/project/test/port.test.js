import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

import { port } from '../src/config.js';

test('the default port is 8080', () => {
  assert.equal(port({}), 8080);
});

test('a port that is set is used', () => {
  assert.equal(port({ PORT: '5000' }), 5000);
});

test('the documentation names the default that is in effect', () => {
  const text = readFileSync(new URL('../README.md', import.meta.url), 'utf8');
  assert.match(text, /port 8080/);
  assert.match(text, /localhost:8080/);
  assert.doesNotMatch(text, /3000/);
  assert.match(text, /PORT=5000/);
});
