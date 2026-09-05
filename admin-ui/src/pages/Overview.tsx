import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { api, type Overview } from '../api';
import { ErrorNote } from '../components/ui';
import { cost, num, when } from '../format';

export function OverviewPage() {
  const [o, setO] = useState<Overview | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { api.overview().then(setO, setError); }, []);
  if (error) return <ErrorNote error={error} />;
  if (!o) return <p className="empty">Loading…</p>;
  const outcomes = Object.entries(o.byOutcome).sort((a, b) => b[1] - a[1]);
  return (
    <>
      <h2>The last seven days <span className="note muted">since {when(o.since)}</span></h2>
      <div className="cards">
        <div className="card"><div className="k">turns</div><div className="v">{num(o.turns)}</div></div>
        <div className="card"><div className="k">open tickets</div><div className="v">{num(o.openTickets)}</div><div className="sub">{num(o.claimedTickets)} claimed</div></div>
        <div className="card"><div className="k">open feedback</div><div className="v">{num(o.openFeedback)}</div></div>
        <div className="card"><div className="k">tokens</div><div className="v">{num(o.inputTokens + o.outputTokens)}</div><div className="sub">{num(o.inputTokens)} in · {num(o.outputTokens)} out</div></div>
        <div className="card"><div className="k">estimated cost</div><div className="v">{cost(o.costUsd)}</div>
          <div className="sub">{o.unpricedTurns > 0 ? `${num(o.unpricedTurns)} turns on a model with no price, not counted` : 'every turn priced'}</div></div>
      </div>
      <h3>By outcome</h3>
      <table><tbody>
        {outcomes.length === 0 && <tr><td className="empty">No turns recorded yet.</td></tr>}
        {outcomes.map(([k, v]) => <tr key={k}><td><span className={`pill ${k}`}>{k}</span></td><td className="num">{num(v)}</td>
          <td><Link to={`/conversations?outcome=${k}`}>conversations</Link></td></tr>)}
      </tbody></table>
    </>
  );
}
