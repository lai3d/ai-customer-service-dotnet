import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { api, type Page, type Ticket } from '../api';
import { useAuth } from '../auth';
import { Empty, ErrorNote, Pager, Pill } from '../components/ui';
import { ago, short } from '../format';
import { STATES } from '../tickets';

export function Tickets() {
  const { me } = useAuth();
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const state = params.get('state') ?? 'open';
  const owner = params.get('owner') ?? '';
  const page = Number(params.get('page') ?? '1');
  const [data, setData] = useState<Page<Ticket> | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    setData(null);
    api.tickets({ state: state === 'all' ? undefined : state, owner: owner || undefined, page, size: 25 }).then(setData, setError);
  }, [state, owner, page]);
  const set = (k: string, v: string) => { const p = new URLSearchParams(params); if (v) p.set(k, v); else p.delete(k); p.delete('page'); setParams(p); };
  return (
    <>
      <h2>Tickets</h2>
      <div className="toolbar">
        <select value={state} onChange={e => set('state', e.target.value)} aria-label="State">
          <option value="all">all states</option>
          {STATES.map(s => <option key={s} value={s}>{s}</option>)}
        </select>
        <select value={owner} onChange={e => set('owner', e.target.value)} aria-label="Owner">
          <option value="">any owner</option>
          <option value={me!.username}>mine</option>
        </select>
      </div>
      <ErrorNote error={error} />
      {data && data.items.length === 0 && <Empty>Nothing here. A ticket appears when the assistant raises one.</Empty>}
      {data && data.items.length > 0 && (
        <table>
          <thead><tr><th>ticket</th><th>state</th><th>owner</th><th>category</th><th>summary</th><th>conversation</th><th>updated</th></tr></thead>
          <tbody>
            {data.items.map(t => (
              <tr key={t.ticketNumber} className="link" onClick={() => navigate(`/tickets/${t.ticketNumber}`)}>
                <td className="mono">{t.ticketNumber}</td>
                <td><Pill kind={t.state}>{t.state}</Pill></td>
                <td>{t.owner ?? <span className="empty">—</span>}</td>
                <td>{t.category}</td>
                <td>{t.summary}</td>
                <td className="mono">{short(t.conversationId)}</td>
                <td title={t.updatedAt}>{ago(t.updatedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {data && <Pager page={data.pageNumber} size={data.size} total={data.total} onPage={p => { const q = new URLSearchParams(params); q.set('page', String(p)); setParams(q); }} />}
    </>
  );
}
