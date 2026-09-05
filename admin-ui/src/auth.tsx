import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, session, setUnauthorizedHandler, type Session } from './api';

interface Auth {
  me: Session | null;
  login(username: string, password: string): Promise<void>;
  logout(): Promise<void>;
}

const Ctx = createContext<Auth>(null!);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Session | null>(() => session.get());
  useEffect(() => { setUnauthorizedHandler(() => setMe(null)); }, []);
  const login = useCallback(async (u: string, p: string) => { const s = await api.login(u, p); session.set(s); setMe(s); }, []);
  const logout = useCallback(async () => { try { await api.logout(); } finally { session.set(null); setMe(null); } }, []);
  const value = useMemo(() => ({ me, login, logout }), [me, login, logout]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export const useAuth = () => useContext(Ctx);
