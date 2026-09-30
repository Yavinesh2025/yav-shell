import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { test } from 'node:test';

import * as math from '../src/math.js';
import { cartTotal } from '../src/cart.js';
import { invoiceLine } from '../src/invoice.js';

test('the function has its new name', () => {
  assert.equal(math.totalWithTax(100, 0.19), 119);
});

test('the old name is gone', () => {
  assert.equal(math.calc, undefined);
  for (const file of readdirSync(new URL('../src/', import.meta.url))) {
    const text = readFileSync(new URL('../src/' + file, import.meta.url), 'utf8');
    assert.doesNotMatch(text, /\bcalc\b/, file);
  }
});

test('what uses it works as before', () => {
  assert.equal(cartTotal([{ price: 10, quantity: 2 }, { price: 5, quantity: 1 }], 0.1), 27.5);
  assert.equal(invoiceLine('Pears', 10, 0.07), 'Pears: 10.70');
});
