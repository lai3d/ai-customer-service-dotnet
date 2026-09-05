import { describe, expect, it } from 'vitest';
import { allowedActions, describeEvent } from './tickets';

const alice = { username: 'alice', role: 'support' as const };
const root = { username: 'root', role: 'admin' as const };

describe('the state machine as the page understands it', () => {
  it('lets anyone claim an unowned open ticket and only admins assign it', () => {
    expect(allowedActions({ state: 'open', owner: undefined }, alice)).toEqual(['note', 'claim']);
    expect(allowedActions({ state: 'open', owner: undefined }, root)).toEqual(['note', 'claim', 'assign']);
  });
  it('gives release, resolve and close to the owner or an admin only', () => {
    expect(allowedActions({ state: 'claimed', owner: 'alice' }, alice)).toEqual(['note', 'assign', 'release', 'resolve', 'close']);
    expect(allowedActions({ state: 'claimed', owner: 'bob' }, alice)).toEqual(['note']);
    expect(allowedActions({ state: 'claimed', owner: 'bob' }, root)).toEqual(['note', 'assign', 'release', 'resolve', 'close']);
  });
  it('lets anyone reopen a resolved or closed ticket', () => {
    expect(allowedActions({ state: 'resolved', owner: 'bob' }, alice)).toEqual(['note', 'reopen']);
    expect(allowedActions({ state: 'closed', owner: 'bob' }, alice)).toEqual(['note', 'reopen']);
  });
  it('describes history as sentences', () => {
    expect(describeEvent('created', { actor: 'assistant' })).toBe('created by the assistant');
    expect(describeEvent('assigned', { actor: 'root', toOwner: 'alice' })).toBe('root assigned it to alice');
  });
});
