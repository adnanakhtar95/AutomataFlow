import { describe, expect, it, vi } from 'vitest';
import { Session, type SessionEnvironment, type TokenResponse } from './session';

const token = (n: number, expiresInMs = 15 * 60_000): TokenResponse => ({
  accessToken: `access-${n}`,
  accessTokenExpiresAt: new Date(Date.now() + expiresInMs).toISOString(),
  refreshToken: null,
  refreshTokenExpiresAt: new Date(Date.now() + 86_400_000).toISOString(),
  organizationId: 'org-1',
  role: 'Owner',
});

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function env(transport: typeof fetch, extra: Partial<SessionEnvironment> = {}): Partial<SessionEnvironment> {
  return { transport, locks: null, channel: null, ...extra };
}

describe('Session (ADR-077)', () => {
  it('restores from the cookie by calling refresh with the browser-session header and no token in the body', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValue(ok(token(1)));
    const session = new Session(env(transport));

    expect(await session.restore()).toBe(true);
    expect(session.current?.accessToken).toBe('access-1');
    const [url, init] = transport.mock.calls[0]!;
    expect(url).toBe('/api/v1/auth/refresh');
    expect((init!.headers as Record<string, string>)['X-AutomataFlow-Session']).toBe('browser');
    expect(init!.body).toBe('{}');
  });

  it('stays signed out when the Api refuses the cookie', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 401 }));
    const session = new Session(env(transport));
    expect(await session.restore()).toBe(false);
    expect(session.current).toBeNull();
  });

  it('never keeps a refresh token in memory or web storage', async () => {
    const session = new Session(env(vi.fn<typeof fetch>()));
    session.start({ ...token(1), refreshToken: 'should-not-be-kept' });
    expect(JSON.stringify(session.current)).not.toContain('should-not-be-kept');
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('shares one in-flight refresh between concurrent callers (the Api revokes the family on reuse)', async () => {
    let resolve: (r: Response) => void = () => {};
    const transport = vi.fn<typeof fetch>().mockImplementation(() => new Promise<Response>((r) => (resolve = r)));
    const session = new Session(env(transport));
    session.start(token(1, 1_000)); // expires inside the early-refresh margin

    const a = session.accessToken();
    const b = session.accessToken();
    resolve(ok(token(2)));
    expect(await a).toBe('access-2');
    expect(await b).toBe('access-2');
    expect(transport).toHaveBeenCalledTimes(1);
  });

  it('serializes refreshes across tabs with a Web Lock', async () => {
    const names: string[] = [];
    const locks = { request: <T,>(name: string, work: () => Promise<T>): Promise<T> => (names.push(name), work()) };
    const session = new Session(env(vi.fn<typeof fetch>().mockResolvedValue(ok(token(2))), { locks }));
    session.start(token(1));
    await session.refresh();
    expect(names).toEqual(['automataflow-session-refresh']);
  });

  it('ends the session as expired when a refresh is refused', async () => {
    const session = new Session(env(vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 401 }))));
    const reasons: (string | null)[] = [];
    session.start(token(1));
    session.subscribe((_, reason) => reasons.push(reason));
    expect(await session.refresh()).toBe(false);
    expect(session.current).toBeNull();
    expect(reasons).toEqual(['expired']);
  });

  it('signs out on the Api, locally and in other tabs', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 204 }));
    const channel = { postMessage: vi.fn(), addEventListener: vi.fn() };
    const session = new Session(env(transport, { channel }));
    session.start(token(1));
    await session.signOut();
    expect(transport.mock.calls[0]![0]).toBe('/api/v1/auth/logout');
    expect(channel.postMessage).toHaveBeenCalledWith('signed-out');
    expect(session.current).toBeNull();
  });

  it('ends this tab when another tab signs out', () => {
    let listener: (event: MessageEvent) => void = () => {};
    const channel = { postMessage: vi.fn(), addEventListener: vi.fn((_: string, l: (event: MessageEvent) => void) => (listener = l)) };
    const session = new Session(env(vi.fn<typeof fetch>(), { channel: channel as never }));
    session.start(token(1));
    listener(new MessageEvent('message', { data: 'signed-out' }));
    expect(session.current).toBeNull();
  });

  it('keeps the session on a network failure during refresh', async () => {
    const session = new Session(env(vi.fn<typeof fetch>().mockRejectedValue(new TypeError('offline'))));
    session.start(token(1));
    expect(await session.refresh()).toBe(true);
    expect(session.current?.accessToken).toBe('access-1');
  });
});
