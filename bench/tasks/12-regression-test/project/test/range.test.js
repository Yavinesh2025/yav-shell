import assert from 'node:assert/strict';
import { test } from 'node:test';

import { parseRange } from '../src/range.js';

test('single numbers and ranges', () => {
  assert.deepEqual(parseRange('1-3, 5'), [1, 2, 3, 5]);
});
