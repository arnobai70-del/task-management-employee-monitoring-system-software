const SESSION_KEY = 'task-monitoring.admin.session.v1';
const API_BASE = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/$/, '');

export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
}

export interface AuthSession extends AuthResponse {}

export interface JwtClaims {
  sub?: string;
  email?: string;
  permission: string[];
  roles: string[];
  exp?: number;
}

export class ApiRequestError extends Error {
  constructor(public readonly status: number, public readonly code: string, message: string) {
    super(message);
    this.name = 'ApiRequestError';
  }
}

export function apiUrl(path: string): string {
  return `${API_BASE}${path}`;
}

export function readSession(): AuthSession | null {
  const raw = sessionStorage.getItem(SESSION_KEY);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as AuthSession;
    if (!parsed.accessToken || !parsed.refreshToken) return null;
    return parsed;
  } catch {
    sessionStorage.removeItem(SESSION_KEY);
    return null;
  }
}

export function saveSession(session: AuthSession | null): void {
  if (session) sessionStorage.setItem(SESSION_KEY, JSON.stringify(session));
  else sessionStorage.removeItem(SESSION_KEY);
  window.dispatchEvent(new Event('task-monitoring:auth-changed'));
}

function decodeBase64Url(value: string): string {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = normalized.padEnd(Math.ceil(normalized.length / 4) * 4, '=');
  return decodeURIComponent(
    Array.from(atob(padded))
      .map(char => `%${char.charCodeAt(0).toString(16).padStart(2, '0')}`)
      .join('')
  );
}

function claimValues(payload: Record<string, unknown>, keys: string[]): string[] {
  for (const key of keys) {
    const value = payload[key];
    if (typeof value === 'string') return [value];
    if (Array.isArray(value)) return value.filter((item): item is string => typeof item === 'string');
  }
  return [];
}

export function parseJwtClaims(token: string): JwtClaims {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) return { permission: [], roles: [] };
    const payload = JSON.parse(decodeBase64Url(parts[1])) as Record<string, unknown>;
    return {
      sub: typeof payload.sub === 'string' ? payload.sub : undefined,
      email: typeof payload.email === 'string' ? payload.email : undefined,
      exp: typeof payload.exp === 'number' ? payload.exp : undefined,
      permission: claimValues(payload, ['permission']),
      roles: claimValues(payload, ['http://schemas.microsoft.com/ws/2008/06/identity/claims/role', 'role'])
    };
  } catch {
    return { permission: [], roles: [] };
  }
}

async function readError(response: Response): Promise<ApiRequestError> {
  try {
    const body = (await response.json()) as { code?: string; message?: string; title?: string };
    return new ApiRequestError(response.status, body.code || 'request_failed', body.message || body.title || `Request failed (${response.status}).`);
  } catch {
    return new ApiRequestError(response.status, 'request_failed', `Request failed (${response.status}).`);
  }
}

export async function login(email: string, password: string): Promise<AuthSession> {
  const response = await fetch(`${API_BASE}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password })
  });
  if (!response.ok) throw await readError(response);
  return (await response.json()) as AuthSession;
}

async function refreshSession(session: AuthSession): Promise<AuthSession> {
  const response = await fetch(`${API_BASE}/api/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken: session.refreshToken })
  });
  if (!response.ok) throw await readError(response);
  const refreshed = (await response.json()) as AuthSession;
  saveSession(refreshed);
  return refreshed;
}

export async function logout(session: AuthSession): Promise<void> {
  try {
    await fetch(`${API_BASE}/api/auth/logout`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${session.accessToken}`
      },
      body: JSON.stringify({ refreshToken: session.refreshToken })
    });
  } finally {
    saveSession(null);
  }
}

function expiresSoon(session: AuthSession): boolean {
  const expiresAt = Date.parse(session.accessTokenExpiresAtUtc);
  return !Number.isFinite(expiresAt) || expiresAt <= Date.now() + 30_000;
}

export async function getValidAccessToken(): Promise<string> {
  let session = readSession();
  if (!session) throw new ApiRequestError(401, 'authentication_required', 'Please sign in again.');
  if (expiresSoon(session)) {
    try {
      session = await refreshSession(session);
    } catch (error) {
      saveSession(null);
      throw error;
    }
  }
  return session.accessToken;
}

export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  let session = readSession();
  if (!session) throw new ApiRequestError(401, 'authentication_required', 'Please sign in again.');

  if (expiresSoon(session)) {
    try {
      session = await refreshSession(session);
    } catch (error) {
      saveSession(null);
      throw error;
    }
  }

  const execute = (accessToken: string) => fetch(`${API_BASE}${path}`, {
    ...init,
    headers: {
      ...(init.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init.headers || {}),
      Authorization: `Bearer ${accessToken}`
    }
  });

  let response = await execute(session.accessToken);
  if (response.status === 401) {
    try {
      session = await refreshSession(session);
      response = await execute(session.accessToken);
    } catch (error) {
      saveSession(null);
      throw error;
    }
  }

  if (!response.ok) throw await readError(response);
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
