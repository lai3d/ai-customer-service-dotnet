// The one place the UI talks to the service. JSON only, a bearer session, problem+json on
// failure. A 401 anywhere means the session is gone -- expired, signed out elsewhere, or the
// account disabled -- and the page returns to sign-in rather than showing empty tables.

export type Role = 'admin' | 'support';
export interface Me { username: string; role: Role }
export interface Session extends Me { token: string; expiresAt: string }
export interface Problem { type: string; title: string; status: number; detail?: string }
export interface Page<T> { items: T[]; pageNumber: number; size: number; total: number }

export type TicketState = 'open' | 'claimed' | 'resolved' | 'closed';
export interface Ticket {
  ticketNumber: string; conversationId: string; category: string; summary: string; orderNumber?: string;
  state: TicketState; owner?: string; createdAt: string; updatedAt: string; version: number;
}
export interface TicketEvent {
  id: number; kind: string; actor: string; fromState?: string; toState?: string; fromOwner?: string; toOwner?: string;
  note?: string; versionAfter: number; occurredAt: string;
}
export interface TicketDetail { ticket: Ticket; history: TicketEvent[] }

export interface ConversationSummary {
  conversationId: string; firstTurnAt: string; lastTurnAt: string; turns: number; lastOutcome: string;
  inputTokens: number; outputTokens: number; tickets: number; openFeedback: number;
}
export interface Passage { entryId: string; language: string; score: number; question: string }
export interface ToolCall { name: string; outcome: string }
export interface ModelCall { seq: number; model: string; inputTokens: number; outputTokens: number; stopReason?: string; failed: boolean }
export interface Feedback {
  id: number; turnId: string; conversationId: string; issue: string; note?: string; state: 'open' | 'closed';
  createdBy: string; createdAt: string; closedBy?: string; closedAt?: string; conclusion?: string; version: number;
}
export interface Turn {
  turnId: string; startedAt: string; endedAt?: string; outcome: string; failure?: string; question: string; answer?: string;
  model?: string; modelCalls: number; inputTokens: number; outputTokens: number; costUsd?: number; traceId?: string;
  retrieval: Passage[]; tools: ToolCall[]; calls: ModelCall[]; feedback: Feedback[];
}
export interface Message { role: string; content: string; createdAt: string }
export interface ConversationDetail { conversationId: string; messages: Message[]; turns: Turn[]; tickets: Ticket[] }
export interface Overview {
  turns: number; byOutcome: Record<string, number>; inputTokens: number; outputTokens: number; costUsd: number;
  unpricedTurns: number; openTickets: number; claimedTickets: number; openFeedback: number; since: string;
}
export interface StaffAccount { username: string; role: Role; enabled: boolean; createdAt: string }
export interface AuditEvent {
  id: number; occurredAt: string; actor: string; action: string; objectType?: string; objectId?: string; outcome: string; detail?: string;
}

export class ApiError extends Error {
  constructor(public readonly problem: Problem) { super(problem.detail ? `${problem.title}: ${problem.detail}` : problem.title); }
  get status() { return this.problem.status; }
}

const BASE = '/api/admin/v1';
const TOKEN_KEY = 'ops.session';

export const session = {
  get(): Session | null {
    try { const raw = sessionStorage.getItem(TOKEN_KEY); return raw ? JSON.parse(raw) as Session : null; } catch { return null; }
  },
  set(s: Session | null) {
    try { if (s) sessionStorage.setItem(TOKEN_KEY, JSON.stringify(s)); else sessionStorage.removeItem(TOKEN_KEY); } catch { /* private mode */ }
  },
};

let onUnauthorized: (() => void) | null = null;
export function setUnauthorizedHandler(fn: () => void) { onUnauthorized = fn; }

async function call<T>(method: string, path: string, body?: unknown, auth = true): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const s = session.get();
  if (auth && s) headers.Authorization = `Bearer ${s.token}`;
  const res = await fetch(BASE + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (res.status === 204) return undefined as T;
  if (!res.ok) {
    let problem: Problem = { type: 'about:blank', title: res.statusText || 'Request failed', status: res.status };
    try { problem = await res.json() as Problem; } catch { /* not problem+json */ }
    if (res.status === 401 && auth) { session.set(null); onUnauthorized?.(); }
    throw new ApiError(problem);
  }
  return await res.json() as T;
}

const q = (params: Record<string, string | number | undefined>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== '') p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : '';
};

export const api = {
  login: (username: string, password: string) => call<Session>('POST', '/session', { username, password }, false),
  logout: () => call<void>('DELETE', '/session'),
  me: () => call<Me>('GET', '/me'),
  overview: () => call<Overview>('GET', '/overview'),

  tickets: (f: { state?: string; owner?: string; conversationId?: string; page?: number; size?: number }) => call<Page<Ticket>>('GET', '/tickets' + q(f)),
  ticket: (n: string) => call<TicketDetail>('GET', `/tickets/${encodeURIComponent(n)}`),
  ticketAction: (n: string, action: string, expectedVersion: number, extra: { assignee?: string; text?: string } = {}) =>
    call<TicketDetail>('POST', `/tickets/${encodeURIComponent(n)}/${action}`, { expectedVersion, ...extra }),

  conversations: (f: { q?: string; outcome?: string; page?: number; size?: number }) => call<Page<ConversationSummary>>('GET', '/conversations' + q(f)),
  conversation: (id: string) => call<ConversationDetail>('GET', `/conversations/${encodeURIComponent(id)}`),
  flag: (conversationId: string, turnId: string, issue: string, note: string) =>
    call<Feedback>('POST', `/conversations/${encodeURIComponent(conversationId)}/turns/${turnId}/feedback`, { issue, note }),

  feedback: (f: { state?: string; page?: number; size?: number }) => call<Page<Feedback>>('GET', '/feedback' + q(f)),
  closeFeedback: (id: number, expectedVersion: number, conclusion: string) => call<Feedback>('POST', `/feedback/${id}/close`, { expectedVersion, conclusion }),

  staff: () => call<StaffAccount[]>('GET', '/staff'),
  createStaff: (username: string, password: string, role: Role) => call<StaffAccount>('POST', '/staff', { username, password, role }),
  patchStaff: (username: string, patch: { role?: Role; enabled?: boolean; password?: string }) => call<StaffAccount>('PATCH', `/staff/${encodeURIComponent(username)}`, patch),

  audit: (f: { actor?: string; action?: string; page?: number; size?: number }) => call<Page<AuditEvent>>('GET', '/audit-events' + q(f)),
};
