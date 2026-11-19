import type { components } from '../api/schema';

export type TokenResponse = components['schemas']['TokenResponse'];

/** What the dashboard keeps in memory. The refresh token is never here: it lives only in an HttpOnly cookie (ADR-077). */
export interface SessionTokens {
  accessToken: string;
  accessTokenExpiresAt: number;
  organizationId: string;
  role: string;
}

export type SessionEndReason = 'signed-out' | 'expired';

type Listener = (tokens: SessionTokens | null, reason: SessionEndReason | null) => void;

/** Asks the Api for the browser-session transport: refresh token in an HttpOnly cookie, never in a body. */
export const SessionHeader = { 'X-AutomataFlow-Session': 'browser' } as const;

/** Refresh slightly before expiry so a request never leaves with a token about to lapse. */
const EarlyRefreshMs = 30_000;

const LockName = 'automataflow-session-refresh';
const ChannelName = 'automataflow-session';

interface LockManagerLike {
  request<T>(name: string, callback: () => Promise<T>): Promise<T>;
}

export interface SessionEnvironment {
  transport: typeof fetch;
  now: () => number;
  /** Serializes refreshes across tabs (navigator.locks). Without it, two tabs could present the same cookie twice. */
  locks: LockManagerLike | null;
  /** Tells other tabs about a sign-out. */
  channel: Pick<BroadcastChannel, 'postMessage' | 'addEventListener'> | null;
}

function browserEnvironment(): SessionEnvironment {
  const nav = typeof navigator === 'undefined' ? undefined : (navigator as Navigator & { locks?: LockManagerLike });
  return {
    transport: (...args) => fetch(...args),
    now: () => Date.now(),
    locks: nav?.locks ?? null,
    channel: typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(ChannelName),
  };
}

/**
 * The browser session (ADR-077). The access token lives only in this object's memory; the refresh token only in an
 * HttpOnly, Secure, SameSite=Strict cookie that page script cannot read. A reload restores the session by asking the
 * Api to rotate the cookie.
 *
 * The Api rotates refresh tokens and revokes the whole family when a rotated token is presented again, so refreshes
 * are single-flight inside a tab AND serialized across tabs with a Web Lock: the second tab then presents the cookie the
 * first one already rotated.
 */
export class Session {
  private tokens: SessionTokens | null = null;
  private refreshing: Promise<boolean> | null = null;
  private readonly listeners = new Set<Listener>();
  private readonly env: SessionEnvironment;

  constructor(env: Partial<SessionEnvironment> = {}) {
    this.env = { ...browserEnvironment(), ...env };
    this.env.channel?.addEventListener('message', (event: MessageEvent) => {
      if (event.data === 'signed-out') {
        this.clear('signed-out');
      }
    });
  }

  get current(): SessionTokens | null {
    return this.tokens;
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  start(response: TokenResponse): void {
    this.tokens = {
      accessToken: response.accessToken,
      accessTokenExpiresAt: Date.parse(response.accessTokenExpiresAt),
      organizationId: response.organizationId,
      role: response.role,
    };
    this.emit(null);
  }

  /** On page load: an existing cookie becomes a session again (false when there is none or it was revoked). */
  restore(): Promise<boolean> {
    return this.rotateShared();
  }

  /** Revokes the session on the Api (best effort), clears it here and in every other tab. */
  async signOut(): Promise<void> {
    try {
      await this.env.transport('/api/v1/auth/logout', { method: 'POST', headers: { 'Content-Type': 'application/json', ...SessionHeader }, body: '{}' });
    } catch {
      // The cookie is revoked server-side on the next successful call at the latest; locally we end anyway.
    }

    this.env.channel?.postMessage('signed-out');
    this.clear('signed-out');
  }

  /** Ends the session locally (e.g. the Api refused the refresh). */
  end(reason: SessionEndReason): void {
    this.clear(reason);
  }

  /** An access token that is valid for at least the early-refresh margin, refreshing first if needed. */
  async accessToken(): Promise<string | null> {
    if (this.tokens === null) {
      return null;
    }

    if (this.tokens.accessTokenExpiresAt - this.env.now() < EarlyRefreshMs && !(await this.refresh())) {
      return null;
    }

    return this.tokens?.accessToken ?? null;
  }

  /** Rotates the tokens. Returns false (and ends the session) when the Api refuses: revoked, expired, reused or no membership. */
  refresh(): Promise<boolean> {
    if (this.tokens === null) {
      return Promise.resolve(false);
    }

    return this.rotateShared();
  }

  private rotateShared(): Promise<boolean> {
    this.refreshing ??= this.withLock(() => this.rotate()).finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  private withLock(work: () => Promise<boolean>): Promise<boolean> {
    return this.env.locks ? this.env.locks.request(LockName, work) : work();
  }

  private async rotate(): Promise<boolean> {
    const before = this.tokens;
    let response: Response;
    try {
      response = await this.env.transport('/api/v1/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...SessionHeader },
        body: '{}',
      });
    } catch {
      return this.tokens !== null; // network failure: keep any session; the request fails and may be retried
    }

    if (!response.ok) {
      this.clear('expired');
      return false;
    }

    // A sign-out while we waited wins: never resurrect an ended session.
    if (before !== null && this.tokens === null) {
      return false;
    }

    this.start((await response.json()) as TokenResponse);
    return true;
  }

  private clear(reason: SessionEndReason): void {
    if (this.tokens === null) {
      return;
    }

    this.tokens = null;
    this.emit(reason);
  }

  private emit(reason: SessionEndReason | null): void {
    for (const listener of this.listeners) {
      listener(this.tokens, reason);
    }
  }
}
