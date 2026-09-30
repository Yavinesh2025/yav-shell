import assert from 'node:assert/strict';
import { test } from 'node:test';

import { loadAll } from '../src/load.js';

const later = (value, milliseconds) => new Promise((resolve) => setTimeout(() => resolve(value), milliseconds));

test('everything that was asked for is loaded', async () => {
  assert.deepEqual(await loadAll(['a', 'b'], (name) => later(name.toUpperCase(), 5)), ['A', 'B']);
});

test('the results are in the order of the names', async () => {
  const delays = { slow: 40, fast: 1 };
  assert.deepEqual(await loadAll(['slow', 'fast'], (name) => later(name, delays[name])), ['slow', 'fast']);
});

test('everything is loaded at the same time', async () => {
  let running = 0;
  let most = 0;
  await loadAll(['a', 'b', 'c'], async (name) => {
    running++;
    most = Math.max(most, running);
    await later(name, 10);
    running--;
    return name;
  });
  assert.equal(most, 3);
});

test('a failure is not swallowed', async () => {
  await assert.rejects(loadAll(['a'], async () => { throw new Error('not there'); }), /not there/);
});

test('nothing gives nothing', async () => {
  assert.deepEqual(await loadAll([], () => later('x', 1)), []);
});
