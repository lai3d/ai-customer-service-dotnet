import { describe, expect, it } from 'vitest';
import { cost, short } from './format';

describe('cost is an estimate', () => {
  it('never shows a missing price as zero', () => {
    expect(cost(undefined)).toBe('no price for this model');
    expect(cost(null)).toBe('no price for this model');
    expect(cost(0.0248)).toBe('$0.02480');
  });
  it('shortens ids for tables', () => {
    expect(short('d4cc91b9-83d9-48d8', 8)).toBe('d4cc91b9…');
  });
});
