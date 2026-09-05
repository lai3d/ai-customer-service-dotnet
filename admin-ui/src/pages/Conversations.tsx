import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { api, type ConversationSummary, type Page } from '../api';
import { Empty, ErrorNote, Pager, Pill } from '../components/ui';
import { ago, num, short } from '../format';

const OUTCOMES = ['completed', 'failed', 'cancelled', 'interrupted', 'tool_limit', 'budget_exceeded', 'retrieval_failed', 'memory_failed', 'record_failed', 'running'];

export function Conversations() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const q = params.get('q') ?? '';
  const outcome = params.get('outcome') ?? '';
  const page = Number(params.get('page') ?? '1');
  const [data, setData] = useState<Page<ConversationSummary> | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { setData(null); api.conversations({ q: q || undefined, outcome: outcome || undefined, page, size: 25 }).then(setData, setError); }, [q, outcome, page]);
  const set = (k: string, v: string) => { const p = new URLSearchParams(params); if (v) p.set(k, v); else p.delete(k); p.delete('page'); setParams(p); };
  return (
    <>
      <h2>Conversations</h2>
      <div className="toolbar">
        <input placeholder="conversation id starts with…" defaultValue={q} onKeyDown={e => { if (e.key === 'Enter') set('q', (e.target as HTMLInputElement).value.trim()); }} aria-label="Conversation id" />
        <select value={outcome} onChange={e => set('outcome', e.target.value)} aria-label="Last outcome">
          <option value="">any outcome</option>
          {OUTCOMES.map(o => <option key={o} value={o}>{o}</option>)}
        </select>
      </div>
      <ErrorNote error={error} />
      {data && data.items.length === 0 && <Empty>No recorded turns match. Records begin when this version was deployed; older chat memory is not a history.</Empty>}
      {data && data.items.length > 0 && (
        <table>
          <thead><tr><th>conversation</th><th>turns</th><th>last outcome</th><th>tokens</th><th>tickets</th><th>open feedback</th><th>last turn</th></tr></thead>
          <tbody>
            {data.items.map(c => (
              <tr key={c.conversationId} className="link" onClick={() => navigate(`/conversations/${c.conversationId}`)}>
                <td className="mono">{short(c.conversationId, 13)}</td>
                <td className="num">{c.turns}</td>
                <td><Pill kind={c.lastOutcome}>{c.lastOutcome}</Pill></td>
                <td className="num">{num(c.inputTokens + c.outputTokens)}</td>
                <td className="num">{c.tickets}</td>
                <td className="num">{c.openFeedback}</td>
                <td title={c.lastTurnAt}>{ago(c.lastTurnAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {data && <Pager page={data.pageNumber} size={data.size} total={data.total} onPage={p => { const s = new URLSearchParams(params); s.set('page', String(p)); setParams(s); }} />}
    </>
  );
}
