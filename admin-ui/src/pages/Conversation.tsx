import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router';
import { api, type ConversationDetail, type Turn } from '../api';
import { Markdown } from '../components/Markdown';
import { ErrorNote, Pill } from '../components/ui';
import { cost, num, when } from '../format';

export function ConversationPage() {
  const { id = '' } = useParams();
  const [d, setD] = useState<ConversationDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const load = useCallback(() => api.conversation(id).then(d => { setD(d); setError(null); }, setError), [id]);
  useEffect(() => { void load(); }, [load]);
  if (error && !d) return <ErrorNote error={error} />;
  if (!d) return <p className="empty">Loading…</p>;
  return (
    <>
      <h2>Conversation <span className="mono">{d.conversationId}</span></h2>
      <p className="note muted">Opening this page was recorded with your name. The transcript is the model's windowed memory; the turns below are the operational record.</p>
      <div className="two">
        <div>
          <h3>Transcript</h3>
          <div className="transcript">
            {d.messages.length === 0 && <p className="empty">No messages in memory.</p>}
            {d.messages.map((m, i) => (
              <div key={i} className={`msg ${m.role}`}>
                {m.role === 'assistant' ? <Markdown text={m.content} /> : m.content}
                <div className="meta">{m.role} · {when(m.createdAt)}</div>
              </div>
            ))}
          </div>
          {d.tickets.length > 0 && <>
            <h3>Tickets raised here</h3>
            <table><tbody>
              {d.tickets.map(t => <tr key={t.ticketNumber}><td><Link to={`/tickets/${t.ticketNumber}`} className="mono">{t.ticketNumber}</Link></td><td><Pill kind={t.state}>{t.state}</Pill></td><td>{t.summary}</td></tr>)}
            </tbody></table>
          </>}
        </div>
        <div>
          <h3>Turns · {d.turns.length}</h3>
          {d.turns.length === 0 && <p className="empty">No turn records. This conversation predates the record, or every turn was refused before it began.</p>}
          {d.turns.map(t => <TurnCard key={t.turnId} turn={t} conversationId={d.conversationId} onChanged={() => void load()} />)}
        </div>
      </div>
    </>
  );
}

function TurnCard({ turn: t, conversationId, onChanged }: { turn: Turn; conversationId: string; onChanged: () => void }) {
  const [flagging, setFlagging] = useState(false);
  const [issue, setIssue] = useState('incorrect');
  const [note, setNote] = useState('');
  const [error, setError] = useState<unknown>(null);
  const scores = t.retrieval.map(p => p.score);
  const lo = Math.min(...scores), hi = Math.max(...scores);
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setError(null);
    try { await api.flag(conversationId, t.turnId, issue, note); setFlagging(false); setNote(''); onChanged(); } catch (err) { setError(err); }
  };
  return (
    <div className="turn">
      <div className="head">
        <Pill kind={t.outcome}>{t.outcome}</Pill>
        <span>{when(t.startedAt)}</span>
        <span>{t.model ?? 'no model call'}</span>
        <span>{t.modelCalls} model call{t.modelCalls === 1 ? '' : 's'}</span>
        <span>{num(t.inputTokens)} in / {num(t.outputTokens)} out</span>
        <span>{t.modelCalls > 0 ? cost(t.costUsd) : ''}</span>
        {t.traceId && <a href={`http://localhost:16688/trace/${t.traceId}`} target="_blank" rel="noreferrer">trace</a>}
      </div>
      {t.failure && <p className="note error">{t.failure}</p>}
      <details><summary>question &amp; answer</summary>
        <p className="note"><strong>Q:</strong> {t.question}</p>
        {t.answer ? <Markdown text={t.answer} /> : <p className="empty">no answer was produced</p>}
      </details>
      <details><summary>retrieval · {t.retrieval.length} passages</summary>
        {t.retrieval.map(p => (
          <div key={p.entryId + p.language}>
            <div className="head"><span className="mono">{p.entryId}</span><span>{p.language}</span><span className="num">{p.score.toFixed(4)}</span></div>
            <div className="bar" style={{ width: (hi > lo ? 18 + 82 * (p.score - lo) / (hi - lo) : 100) + '%' }} />
          </div>
        ))}
        <p className="note muted">Bars are relative within this result set; absolute scores sit in a narrow band.</p>
      </details>
      <details><summary>tools &amp; model calls</summary>
        {t.tools.length === 0 ? <p className="empty">no tool calls</p> : t.tools.map((c, i) => <Pill key={i} kind={c.outcome}>{c.name} → {c.outcome}</Pill>)}
        <table><tbody>{t.calls.map(c => <tr key={c.seq}><td>call {c.seq}</td><td className="mono">{c.model}</td><td className="num">{num(c.inputTokens)} / {num(c.outputTokens)}</td><td>{c.stopReason ?? ''}</td><td>{c.failed ? <Pill kind="failed">failed</Pill> : null}</td></tr>)}</tbody></table>
      </details>
      {t.feedback.length > 0 && <div className="note">{t.feedback.map(f => <div key={f.id}><Pill kind={f.state === 'open' ? 'open' : 'closed'}>{f.issue}</Pill> {f.note} {f.conclusion ? <em>— {f.conclusion}</em> : null}</div>)}</div>}
      {!flagging && t.answer && <div className="actions"><button onClick={() => setFlagging(true)}>flag this answer</button></div>}
      {flagging && (
        <form className="action" onSubmit={submit}>
          <select value={issue} onChange={e => setIssue(e.target.value)} aria-label="Issue">
            <option value="incorrect">incorrect</option><option value="incomplete">incomplete</option><option value="other">other</option>
          </select>
          <textarea placeholder="what is wrong, in a sentence" value={note} onChange={e => setNote(e.target.value)} />
          <div className="actions"><button className="primary" type="submit">flag</button><button type="button" onClick={() => setFlagging(false)}>cancel</button></div>
          <ErrorNote error={error} />
        </form>
      )}
    </div>
  );
}
