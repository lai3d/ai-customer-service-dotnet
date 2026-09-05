import { useState, type FormEvent } from 'react';
import { useAuth } from '../auth';
import { ErrorNote } from '../components/ui';

export function Login() {
  const { login } = useAuth();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await login(username.trim(), password); } catch (err) { setError(err); } finally { setBusy(false); }
  };
  return (
    <form className="login" onSubmit={submit}>
      <h1>Operations sign-in</h1>
      <p className="note muted">Staff only. Every conversation you open is recorded.</p>
      <input autoFocus autoComplete="username" placeholder="username" value={username} onChange={e => setUsername(e.target.value)} aria-label="Username" />
      <input type="password" autoComplete="current-password" placeholder="password" value={password} onChange={e => setPassword(e.target.value)} aria-label="Password" />
      <button className="primary" type="submit" disabled={busy || !username || !password}>Sign in</button>
      <ErrorNote error={error} />
    </form>
  );
}
