import { describe, expect, it, vi } from 'vitest';
import { Session, type TokenResponse } from '../auth/session';
import { authorizedFetch, ensureOk, unwrap } from './client';
import { ApiError, toApiError } from './errors';

const tokens = (n: number): TokenResponse => ({
  accessToken: `access-${n}`,
  accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
  refreshToken: null,
  refreshTokenExpiresAt: new Date(Date.now() + 86_400_000).toISOString(),
  organizationId: 'org',
  role: 'Operator',
});

function signedIn(refreshResponses: Response[]): Session {
  const transport = vi.fn<typeof fetch>();
  for (const r of refreshResponses) {
    transport.mockResolvedValueOnce(r);
  }

  const session = new Session({ transport, locks: null, channel: null });
  session.start(tokens(1));
  return session;
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

describe('authorizedFetch', () => {
  it('sends the access token only in the Authorization header', async () => {
    const transport = vi.fn<typeof fetch>().mockResolvedValue(json({}));
    await authorizedFetch(signedIn([]), transport)('/api/v1/jobs');
    const request = transport.mock.calls[0]![0] as Request;
    expect(request.headers.get('Authorization')).toBe('Bearer access-1');
    expect(request.url).not.toContain('access-1');
  });

  it('refreshes once on 401 and retries with the new token', async () => {
    const session = signedIn([json(tokens(2))]);
    const transport = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response(null, { status: 401 })).mockResolvedValueOnce(json({ ok: true }));
    const response = await authorizedFetch(session, transport)('/api/v1/jobs', { method: 'POST', body: 'x' });
    expect(response.status).toBe(200);
    expect((transport.mock.calls[1]![0] as Request).headers.get('Authorization')).toBe('Bearer access-2');
    expect(await (transport.mock.calls[1]![0] as Request).text()).toBe('x'); // the body is replayed
  });

  it('ends the session when the retried request is still unauthorized', async () => {
    const session = signedIn([json(tokens(2))]);
    const transport = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 401 }));
    await authorizedFetch(session, transport)('/api/v1/jobs');
    expect(session.current).toBeNull();
  });

  it('does not refresh on 403 (a role or membership refusal, not an expired token)', async () => {
    const session = signedIn([]);
    const transport = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 403 }));
    expect((await authorizedFetch(session, transport)('/api/v1/secrets')).status).toBe(403);
    expect(transport).toHaveBeenCalledTimes(1);
    expect(session.current).not.toBeNull();
  });

  it('turns a network failure into an ApiError with status 0', async () => {
    const transport = vi.fn<typeof fetch>().mockRejectedValue(new TypeError('offline'));
    await expect(authorizedFetch(signedIn([]), transport)('/api/v1/jobs')).rejects.toMatchObject({ status: 0 });
  });
});

describe('errors', () => {
  it('builds an ApiError from ProblemDetails, keeping field errors and never inventing values', () => {
    const error = toApiError(400, { title: 'Invalid', errors: [{ path: 'steps[0].type', code: 'unknown-action', message: 'Unknown action.' }] });
    expect(error).toBeInstanceOf(ApiError);
    expect(error.title).toBe('Invalid');
    expect(error.errors).toEqual([{ path: 'steps[0].type', code: 'unknown-action', message: 'Unknown action.' }]);
  });

  it('falls back to a fixed message for an empty body', () => {
    expect(toApiError(413, null).title).toBe('The file is too large.');
    expect(toApiError(599, 'not json').title).toBe('Request failed (599).');
  });

  it('unwrap and ensureOk throw for non-2xx answers', async () => {
    await expect(unwrap(Promise.resolve({ error: { title: 'Nope' }, response: new Response(null, { status: 409 }) }))).rejects.toMatchObject({ status: 409, title: 'Nope' });
    await expect(ensureOk(json({ title: 'Gone' }, 404))).rejects.toMatchObject({ isNotFound: true });
    expect((await ensureOk(json({}))).status).toBe(200);
  });
});
