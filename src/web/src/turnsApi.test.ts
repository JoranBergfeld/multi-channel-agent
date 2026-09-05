import { describe, expect, it } from 'vitest';
import { isDefinitiveRejection } from './turnsApi';

describe('isDefinitiveRejection', () => {
  it('clears only permanent client rejections, preserving every retryable or ambiguous status', () => {
    for (const status of [400, 403, 404, 409]) {
      expect(isDefinitiveRejection(status), `${status} should be definitive`).toBe(true);
    }

    for (const status of [401, 408, 429, 500, 503]) {
      expect(isDefinitiveRejection(status), `${status} should remain retryable or ambiguous`).toBe(false);
    }
  });
});
