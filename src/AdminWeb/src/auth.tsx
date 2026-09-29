import { createContext, useCallback, useContext, useEffect, useMemo, useState, type PropsWithChildren } from 'react';
import { login, logout, parseJwtClaims, readSession, saveSession, type AuthSession, type JwtClaims } from './api';

interface AuthContextValue {
  session: AuthSession | null;
  claims: JwtClaims;
  isAuthenticated: boolean;
  can: (permission: string) => boolean;
  signIn: (email: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

export function AuthProvider({ children }: PropsWithChildren) {
  const [session, setSession] = useState<AuthSession | null>(() => readSession());

  useEffect(() => {
    const sync = () => setSession(readSession());
    window.addEventListener('task-monitoring:auth-changed', sync);
    return () => window.removeEventListener('task-monitoring:auth-changed', sync);
  }, []);

  const claims = useMemo<JwtClaims>(() => session ? parseJwtClaims(session.accessToken) : { permission: [], roles: [] }, [session]);
  const permissions = useMemo(() => new Set(claims.permission), [claims.permission]);
  const can = useCallback((permission: string) => permissions.has(permission), [permissions]);

  const signIn = useCallback(async (email: string, password: string) => {
    const next = await login(email, password);
    saveSession(next);
    setSession(next);
  }, []);

  const signOut = useCallback(async () => {
    const current = readSession();
    if (current) await logout(current);
    else saveSession(null);
    setSession(null);
  }, []);

  const value = useMemo<AuthContextValue>(() => ({ session, claims, isAuthenticated: Boolean(session), can, signIn, signOut }), [session, claims, can, signIn, signOut]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside AuthProvider.');
  return value;
}
