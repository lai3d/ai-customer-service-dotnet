import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router';
import { api, type TicketDetail } from '../api';
import { useAuth } from '../auth';
import { ErrorNote, Pill } from '../components/ui';
import { when } from '../format';
import { allowedActions, describeEvent, TEXT_FOR, type Action } from '../tickets';

export function TicketDetailPage() {
  const { number = '' } = useParams();
  const { me } = useAuth();
  const [d, setD] = useState<TicketDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [pending, setPending] = useState<Action | null>(null);
  const [text, setText] = useState('');
  const [assignee, setAssignee] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => api.ticket(number).then(d => { setD(d); setError(null); }, setError), [number]);
  useEffect(() => { void load(); }, [load]);

  if (error && !d) return <ErrorNote error={error} />;
  if (!d) return <p className="empty">Loading…</p>;
  const t = d.ticket;
  const actions = allowedActions(t, me!);

  // The action is a parameter, not read back from state: React applies setState after the
  // handler returns, so a "claim" that set `pending` and then read it saw null and did
  // nothing. Found by driving the page in a real browser -- claim and release are the two
  // buttons that open no form, so no typed test exercised the path that failed.
  const perform = async (action: Action, extra: { assignee?: string; text?: string }) => {
    setBusy(true); setError(null);
    try {
      // The version the page read travels with the request. A stale one is a 409 and writes
      // nothing, which is also what makes a double-submitted form land once.
      const next = await api.ticketAction(number, action, t.version, extra);
      setD(next); setPending(null); setText(''); setAssignee('');
    } catch (err) { setError(err); } finally { setBusy(false); }
  };
  const run = (e: FormEvent) => {
    e.preventDefault();
    if (pending) void perform(pending, pending === 'assign' ? { assignee } : { text });
  };
  const start = (a: Action) => {
    setError(null);
    if (a === 'claim' || a === 'release') { void perform(a, {}); return; }
    setPending(a);
  };
  const needs = pending ? TEXT_FOR[pending] : undefined;

  return (
    <>
      <h2><span className="mono">{t.ticketNumber}</span> <Pill kind={t.state}>{t.state}</Pill></h2>
      <div className="two">
        <div>
          <div className="panel">
            <div className="kv">
              <span className="k">summary</span><span>{t.summary}</span>
              <span className="k">category</span><span>{t.category}</span>
              <span className="k">order</span><span>{t.orderNumber ?? '—'}</span>
              <span className="k">owner</span><span>{t.owner ?? <span className="empty">unowned — in the queue</span>}</span>
              <span className="k">conversation</span><span><Link to={`/conversations/${t.conversationId}`} className="mono">{t.conversationId}</Link></span>
              <span className="k">created</span><span>{when(t.createdAt)}</span>
              <span className="k">updated</span><span>{when(t.updatedAt)} · version {t.version}</span>
            </div>
            <div className="actions">
              {(['claim', 'assign', 'release', 'resolve', 'close', 'reopen', 'note'] as Action[]).filter(a => actions.includes(a)).map(a =>
                <button key={a} className={a === 'claim' || a === 'resolve' ? 'primary' : ''} disabled={busy} onClick={() => start(a)}>{a}</button>)}
              <button disabled={busy} onClick={() => void load()}>refresh</button>
            </div>
            {pending && needs && (
              <form className="action" onSubmit={run}>
                <label>{needs.label}
                  <textarea autoFocus value={text} onChange={e => setText(e.target.value)} required={needs.required} />
                </label>
                <div className="actions">
                  <button className="primary" type="submit" disabled={busy || (needs.required && !text.trim())}>{pending}</button>
                  <button type="button" onClick={() => setPending(null)}>cancel</button>
                </div>
              </form>
            )}
            {pending === 'assign' && (
              <form className="action" onSubmit={run}>
                <label>Assign to (username)<input autoFocus value={assignee} onChange={e => setAssignee(e.target.value)} required /></label>
                <div className="actions">
                  <button className="primary" type="submit" disabled={busy || !assignee.trim()}>assign</button>
                  <button type="button" onClick={() => setPending(null)}>cancel</button>
                </div>
              </form>
            )}
            <ErrorNote error={error} />
          </div>
        </div>
        <div>
          <h3>History</h3>
          <ul className="history">
            {d.history.map(e => (
              <li key={e.id}>
                <div className="t">{when(e.occurredAt)} · v{e.versionAfter}</div>
                <div>{describeEvent(e.kind, e)}{e.fromState && e.toState && e.fromState !== e.toState ? <> <span className="empty">({e.fromState} → {e.toState})</span></> : null}</div>
                {e.note && <p className="note">{e.note}</p>}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </>
  );
}
