import assert from 'node:assert/strict';
import { test } from 'node:test';

import { parseRange } from '../src/range.js';

test('single numbers and ranges', () => {
  assert.deepEqual(parseRange('1-3, 5'), [1, 2, 3, 5]);
});

test('a range that is written backwards', () => {
  assert.deepEqual(parseRange('5-3'), [3, 4, 5]);
  assert.deepEqual(parseRange('2-2'), [2]);
});
