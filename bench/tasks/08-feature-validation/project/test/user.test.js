import assert from 'node:assert/strict';
import { test } from 'node:test';

import * as users from '../src/user.js';

test('a user with a name and an email is created', () => {
  assert.deepEqual(users.createUser({ name: ' Ada ', email: ' ada@example.invalid ' }), { name: 'Ada', email: 'ada@example.invalid' });
});

for (const name of [undefined, null, '', '   ']) {
  test(`a name that is ${JSON.stringify(name)} is refused`, () => {
    assert.throws(
      () => users.createUser({ name, email: 'ada@example.invalid' }),
      (error) => error instanceof users.ValidationError && error.field === 'name');
  });
}

for (const email of [undefined, '', 'ada.example.invalid']) {
  test(`an email that is ${JSON.stringify(email)} is refused`, () => {
    assert.throws(
      () => users.createUser({ name: 'Ada', email }),
      (error) => error instanceof users.ValidationError && error.field === 'email');
  });
}

test('a validation error is an error', () => {
  assert.ok(new users.ValidationError('name', 'is missing') instanceof Error);
});
