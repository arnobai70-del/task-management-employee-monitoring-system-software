const SESSION_KEY = 'task-monitoring.admin.session.v2';
const LEGACY_SESSION_KEY = 'task-monitoring.admin.session.v1';
const API_BASE = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/$/, '');

export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
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

interface PagedEnvelope {
  items: unknown[];
  page: number;
  pageSize: number;
  totalCount: number;
}

const LEGACY_COMPLETE_LIST_PATHS = new Set([
  '/api/employees',
  '/api/projects',
  '/api/tasks',
  '/api/surveys',
  '/api/website-work'
]);

let refreshPromise: Promise<AuthSession> | null = null;

export function apiUrl(path: string): string {
  return `${API_BASE}${path}`;
}

export function readSession(): AuthSession | null {
  sessionStorage.removeItem(LEGACY_SESSION_KEY);
  const raw = sessionStorage.getItem(SESSION_KEY);
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as AuthSession;
    if (!parsed.accessToken || !parsed.accessTokenExpiresAtUtc) return null;
    return parsed;
  } catch {
    sessionStorage.removeItem(SESSION_KEY);
    return null;
  }
}

export function saveSession(session: AuthSession | null): void {
  sessionStorage.removeItem(LEGACY_SESSION_KEY);
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
  const response = await fetch(`${API_BASE}/api/auth/web/login`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password })
  });
  if (!response.ok) throw await readError(response);
  return (await response.json()) as AuthSession;
}

async function requestRefresh(): Promise<AuthSession> {
  const response = await fetch(`${API_BASE}/api/auth/web/refresh`, {
    method: 'POST',
    credentials: 'same-origin'
  });
  if (!response.ok) throw await readError(response);
  const refreshed = (await response.json()) as AuthSession;
  saveSession(refreshed);
  return refreshed;
}

async function refreshSession(): Promise<AuthSession> {
  if (!refreshPromise) {
    refreshPromise = requestRefresh().finally(() => {
      refreshPromise = null;
    });
  }
  return refreshPromise;
}

export async function logout(session: AuthSession): Promise<void> {
  try {
    let current: AuthSession | null = session;
    if (expiresSoon(current)) {
      try {
        current = await refreshSession();
      } catch {
        current = null;
      }
    }

    if (current) {
      await fetch(`${API_BASE}/api/auth/web/logout`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { Authorization: `Bearer ${current.accessToken}` }
      });
    }
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
      session = await refreshSession();
    } catch (error) {
      saveSession(null);
      throw error;
    }
  }
  return session.accessToken;
}

function isPagedEnvelope(value: unknown): value is PagedEnvelope {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<PagedEnvelope>;
  return Array.isArray(candidate.items)
    && typeof candidate.page === 'number'
    && typeof candidate.pageSize === 'number'
    && typeof candidate.totalCount === 'number';
}

function shouldLoadCompleteLegacyList(path: string, init: RequestInit): boolean {
  const method = (init.method || 'GET').toUpperCase();
  if (method !== 'GET') return false;

  try {
    const requestUrl = new URL(path, 'http://admin.local');
    return LEGACY_COMPLETE_LIST_PATHS.has(requestUrl.pathname)
      && requestUrl.searchParams.get('page') === '1'
      && requestUrl.searchParams.get('pageSize') === '100';
  } catch {
    return false;
  }
}

function withPage(path: string, page: number): string {
  const requestUrl = new URL(path, 'http://admin.local');
  requestUrl.searchParams.set('page', String(page));
  return `${requestUrl.pathname}${requestUrl.search}`;
}

export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  let session = readSession();
  if (!session) throw new ApiRequestError(401, 'authentication_required', 'Please sign in again.');

  if (expiresSoon(session)) {
    try {
      session = await refreshSession();
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
      session = await refreshSession();
      response = await execute(session.accessToken);
    } catch (error) {
      saveSession(null);
      throw error;
    }
  }

  if (!response.ok) throw await readError(response);
  if (response.status === 204) return undefined as T;

  const parsed = await response.json() as unknown;
  if (!shouldLoadCompleteLegacyList(path, init) || !isPagedEnvelope(parsed) || parsed.items.length >= parsed.totalCount) {
    return parsed as T;
  }

  // Several older management screens predate visible pagination and intentionally ask
  // for page 1 with the server's maximum page size. Aggregate their remaining pages so
  // record 101+ is never silently omitted while those screens are incrementally upgraded.
  const pageSize = Math.max(1, parsed.pageSize);
  const totalPages = Math.ceil(parsed.totalCount / pageSize);
  const items = [...parsed.items];

  for (let page = 2; page <= totalPages; page += 1) {
    const next = await apiFetch<PagedEnvelope>(withPage(path, page), init);
    if (!isPagedEnvelope(next)) {
      throw new ApiRequestError(500, 'invalid_paged_response', 'The server returned an invalid paged response.');
    }
    items.push(...next.items);
    if (next.items.length === 0) break;
  }

  return {
    ...parsed,
    items,
    page: 1,
    totalCount: Math.max(parsed.totalCount, items.length)
  } as T;
}
