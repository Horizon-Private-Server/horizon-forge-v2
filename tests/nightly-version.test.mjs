import assert from 'node:assert/strict';
import test from 'node:test';

import { createNightlyVersion } from '../scripts/nightly-version.mjs';

test('nightly versions are ordered and commit-specific', () => {
  assert.equal(createNightlyVersion('2.0.0', '123', 'A1B2C3D4E5F6'), '2.0.0-nightly.123.a1b2c3d4');
  assert.throws(() => createNightlyVersion('2.0', '123', 'a1b2c3d4'), /Base version/);
  assert.throws(() => createNightlyVersion('2.0.0', '0', 'a1b2c3d4'), /Run number/);
});
