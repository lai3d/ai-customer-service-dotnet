import { describe, expect, it } from 'vitest';
import { ApiError } from './api';

describe('ApiError', () => {
  it('reads like the problem it carries', () => {
    const e = new ApiError({ type: 'about:blank', title: 'Ticket has changed', status: 409, detail: 'version 2 not 1' });
    expect(e.status).toBe(409);
    expect(e.message).toBe('Ticket has changed: version 2 not 1');
  });
});
