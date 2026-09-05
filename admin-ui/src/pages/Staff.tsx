import { useEffect, useState, type FormEvent } from 'react';
import { api, type Role, type StaffAccount } from '../api';
import { useAuth } from '../auth';
import { ErrorNote, Pill } from '../components/ui';
import { when } from '../format';

export function Staff() {
  const { me } = useAuth();
  const [list, setList] = useState<StaffAccount[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [username, setUsername] = useState(''); const [password, setPassword] = useState(''); const [role, setRole] = useState<Role>('support');
  const load = () => api.staff().then(setList, setError);
  useEffect(() => { void load(); }, []);
  const create = async (e: FormEvent) => {
    e.preventDefault(); setError(null);
    try { await api.createStaff(username.trim(), password, role); setUsername(''); setPassword(''); await load(); } catch (err) { setError(err); }
  };
  const patch = async (u: string, p: { role?: Role; enabled?: boolean; password?: string }) => {
    setError(null); try { await api.patchStaff(u, p); await load(); } catch (err) { setError(err); }
  };
  const reset = (u: string) => {
    const pw = window.prompt(`New password for ${u} (at least 12 characters)`);
    if (pw) void patch(u, { password: pw });
  };
  return (
    <>
      <h2>Staff accounts</h2>
      <ErrorNote error={error} />
      {list && (
        <table>
          <thead><tr><th>username</th><th>role</th><th>enabled</th><th>created</th><th></th></tr></thead>
          <tbody>{list.map(a => {
            const self = a.username === me!.username;
            return (
              <tr key={a.username}>
                <td>{a.username}{self ? <span className="empty"> (you)</span> : ''}</td>
                <td><Pill kind={a.role}>{a.role}</Pill></td>
                <td>{a.enabled ? 'yes' : <Pill kind="failed">disabled</Pill>}</td>
                <td>{when(a.createdAt)}</td>
                <td className="actions">
                  {!self && <button onClick={() => void patch(a.username, { role: a.role === 'admin' ? 'support' : 'admin' })}>make {a.role === 'admin' ? 'support' : 'admin'}</button>}
                  {!self && <button onClick={() => void patch(a.username, { enabled: !a.enabled })}>{a.enabled ? 'disable' : 'enable'}</button>}
                  <button onClick={() => reset(a.username)}>reset password</button>
                </td>
              </tr>);
          })}</tbody>
        </table>
      )}
      <h3>New account</h3>
      <form className="action panel" onSubmit={create}>
        <div className="toolbar">
          <input placeholder="username (a-z 0-9 . _ -)" value={username} onChange={e => setUsername(e.target.value)} required pattern="[a-z0-9._\-]{2,64}" aria-label="Username" />
          <input type="password" placeholder="password, 12+ characters" value={password} onChange={e => setPassword(e.target.value)} required minLength={12} autoComplete="new-password" aria-label="Password" />
          <select value={role} onChange={e => setRole(e.target.value as Role)} aria-label="Role"><option value="support">support</option><option value="admin">admin</option></select>
          <button className="primary" type="submit">create</button>
        </div>
      </form>
    </>
  );
}
