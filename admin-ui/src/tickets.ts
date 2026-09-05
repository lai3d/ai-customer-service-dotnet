// The state machine as the page understands it, mirrored from the server. The server is
// the authority -- hiding a button is a user-interface decision -- but a button that would
// only ever earn a 422 is noise, so the page shows what the rules allow.
import type { Me, Ticket, TicketState } from './api';

export type Action = 'claim' | 'assign' | 'release' | 'resolve' | 'close' | 'reopen' | 'note';

export const STATES: TicketState[] = ['open', 'claimed', 'resolved', 'closed'];

export function allowedActions(t: Pick<Ticket, 'state' | 'owner'>, me: Me): Action[] {
  const owns = t.owner === me.username;
  const ownerOrAdmin = owns || me.role === 'admin';
  const out: Action[] = ['note'];
  switch (t.state) {
    case 'open':
      if (!t.owner) out.push('claim');
      if (me.role === 'admin') out.push('assign');
      break;
    case 'claimed':
      if (ownerOrAdmin) out.push('assign', 'release', 'resolve', 'close');
      break;
    case 'resolved':
      if (ownerOrAdmin) out.push('close');
      out.push('reopen');
      break;
    case 'closed':
      out.push('reopen');
      break;
  }
  return out;
}

/** Actions whose request must carry text, and what the text is. */
export const TEXT_FOR: Partial<Record<Action, { label: string; required: boolean }>> = {
  resolve: { label: 'Conclusion: what was done for the customer', required: true },
  reopen: { label: 'Reason for reopening', required: true },
  close: { label: 'Closing note (optional)', required: false },
  note: { label: 'Internal note', required: true },
};

export function describeEvent(kind: string, e: { actor: string; toOwner?: string; note?: string }): string {
  switch (kind) {
    case 'created': return `created by the ${e.actor}`;
    case 'claimed': return `${e.actor} claimed it`;
    case 'assigned': return `${e.actor} assigned it to ${e.toOwner ?? '?'}`;
    case 'released': return `${e.actor} released it to the queue`;
    case 'resolved': return `${e.actor} resolved it`;
    case 'closed': return `${e.actor} closed it`;
    case 'reopened': return `${e.actor} reopened it`;
    case 'note': return `${e.actor} noted`;
    default: return `${e.actor}: ${kind}`;
  }
}
