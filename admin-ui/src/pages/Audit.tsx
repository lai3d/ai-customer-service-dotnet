import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, type AuditEvent, type Page } from '../api';
import { Empty, ErrorNote, Pager, Pill } from '../components/ui';
import { when } from '../format';

export function Audit() {
  const [params, setParams] = useSearchParams();
  const actor = params.get('actor') ?? '';
  const page = Number(params.get('page') ?? '1');
  const [data, setData] = useState<Page<AuditEvent> | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { setData(null); api.audit({ actor: actor || undefined, page, size: 50 }).then(setData, setError); }, [actor, page]);
  return (
    <>
      <h2>Audit</h2>
      <p className="note muted">Who looked at what, and what was refused. Ticket changes are the ticket's own history and are not repeated here.</p>
      <div className="toolbar">
        <input placeholder="actor" defaultValue={actor} onKeyDown={e => { if (e.key === 'Enter') { const p = new URLSearchParams(params); const v = (e.target as HTMLInputElement).value.trim(); if (v) p.set('actor', v); else p.delete('actor'); p.delete('page'); setParams(p); } }} aria-label="Actor" />
      </div>
      <ErrorNote error={error} />
      {data && data.items.length === 0 && <Empty>Nothing recorded.</Empty>}
      {data && data.items.length > 0 && (
        <table>
          <thead><tr><th>when</th><th>actor</th><th>action</th><th>object</th><th>outcome</th><th>detail</th></tr></thead>
          <tbody>{data.items.map(e => (
            <tr key={e.id}>
              <td>{when(e.occurredAt)}</td><td>{e.actor}</td><td>{e.action}</td>
              <td className="mono">{e.objectType === 'conversation' && e.objectId ? <Link to={`/conversations/${e.objectId}`}>{e.objectId}</Link>
                : e.objectType === 'ticket' && e.objectId ? <Link to={`/tickets/${e.objectId}`}>{e.objectId}</Link> : e.objectId ?? ''}</td>
              <td><Pill kind={e.outcome}>{e.outcome}</Pill></td>
              <td>{e.detail}</td>
            </tr>))}
          </tbody>
        </table>
      )}
      {data && <Pager page={data.pageNumber} size={data.size} total={data.total} onPage={p => { const s = new URLSearchParams(params); s.set('page', String(p)); setParams(s); }} />}
    </>
  );
}
