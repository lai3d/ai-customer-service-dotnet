import { useEffect, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, type Feedback, type Page } from '../api';
import { Empty, ErrorNote, Pager, Pill } from '../components/ui';
import { short, when } from '../format';

export function FeedbackPage() {
  const [params, setParams] = useSearchParams();
  const state = params.get('state') ?? 'open';
  const page = Number(params.get('page') ?? '1');
  const [data, setData] = useState<Page<Feedback> | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [closing, setClosing] = useState<Feedback | null>(null);
  const [conclusion, setConclusion] = useState('');
  const load = () => api.feedback({ state: state === 'all' ? undefined : state, page, size: 25 }).then(setData, setError);
  useEffect(() => { setData(null); void load(); }, [state, page]); // eslint-disable-line react-hooks/exhaustive-deps
  const close = async (e: FormEvent) => {
    e.preventDefault(); if (!closing) return; setError(null);
    try { await api.closeFeedback(closing.id, closing.version, conclusion); setClosing(null); setConclusion(''); await load(); } catch (err) { setError(err); }
  };
  return (
    <>
      <h2>Answer feedback</h2>
      <p className="note muted">Closing a flag means the report was handled — an FAQ revised, a conclusion written — not that the customer's issue is resolved.</p>
      <div className="toolbar">
        <select value={state} onChange={e => { const p = new URLSearchParams(params); p.set('state', e.target.value); p.delete('page'); setParams(p); }} aria-label="State">
          <option value="open">open</option><option value="closed">closed</option><option value="all">all</option>
        </select>
      </div>
      <ErrorNote error={error} />
      {data && data.items.length === 0 && <Empty>No feedback in this state.</Empty>}
      {data && data.items.length > 0 && (
        <table>
          <thead><tr><th>state</th><th>issue</th><th>note</th><th>conversation</th><th>raised</th><th>handling</th><th></th></tr></thead>
          <tbody>{data.items.map(f => (
            <tr key={f.id}>
              <td><Pill kind={f.state}>{f.state}</Pill></td>
              <td>{f.issue}</td>
              <td>{f.note ?? <span className="empty">—</span>}</td>
              <td><Link to={`/conversations/${f.conversationId}`} className="mono">{short(f.conversationId)}</Link></td>
              <td>{f.createdBy} · {when(f.createdAt)}</td>
              <td>{f.state === 'closed' ? <>{f.closedBy} · {when(f.closedAt)}<br /><em>{f.conclusion}</em></> : ''}</td>
              <td>{f.state === 'open' && <button onClick={() => { setClosing(f); setConclusion(''); }}>close</button>}</td>
            </tr>))}
          </tbody>
        </table>
      )}
      {closing && (
        <form className="action panel" onSubmit={close}>
          <label>Conclusion for #{closing.id} ({closing.issue}){' '}
            <textarea autoFocus required value={conclusion} onChange={e => setConclusion(e.target.value)} placeholder="what was done about it" />
          </label>
          <div className="actions"><button className="primary" type="submit" disabled={!conclusion.trim()}>close feedback</button><button type="button" onClick={() => setClosing(null)}>cancel</button></div>
        </form>
      )}
      {data && <Pager page={data.pageNumber} size={data.size} total={data.total} onPage={p => { const s = new URLSearchParams(params); s.set('page', String(p)); setParams(s); }} />}
    </>
  );
}
