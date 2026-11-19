import createClient from 'openapi-fetch';
import type { Session } from '../auth/session';
import { ApiError, networkError, toApiError } from './errors';
import type { components, paths } from './schema';

export type Schemas = components['schemas'];

/**
 * fetch with the session's bearer token. On a 401 the session is refreshed once and the request retried; a second 401
 * ends the session (the user is sent to sign-in). Tokens are only ever sent in the Authorization header, never in a URL.
 */
export function authorizedFetch(session: Session, transport: typeof fetch = (...args) => fetch(...args)) {
  return async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const original = new Request(typeof input === 'string' ? new URL(input, origin()) : input, init);
    const attempt = async (token: string | null): Promise<Response> => {
      const request = original.clone();
      if (token) {
        request.headers.set('Authorization', `Bearer ${token}`);
      }

      try {
        return await transport(request);
      } catch {
        throw networkError();
      }
    };

    const first = await attempt(await session.accessToken());
    if (first.status !== 401 || session.current === null) {
      return first;
    }

    if (!(await session.refresh())) {
      return first;
    }

    const second = await attempt(await session.accessToken());
    if (second.status === 401) {
      session.end('expired');
    }

    return second;
  };
}

/** The page's own origin: the dashboard talks to the Api same-origin only (ADR-076). */
function origin(): string {
  return globalThis.location?.origin ?? 'http://localhost';
}

export function createApi(session: Session) {
  return createClient<paths>({ baseUrl: origin(), fetch: authorizedFetch(session) });
}

export type Api = ReturnType<typeof createApi>;

/** Unwraps an openapi-fetch result: the data, or an ApiError built from the ProblemDetails body. */
export async function unwrap<T>(call: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await call;
  if (!response.ok) {
    throw toApiError(response.status, error);
  }

  return data as T;
}

/** For raw responses (uploads, downloads): throws an ApiError for a non-2xx answer. */
export async function ensureOk(response: Response): Promise<Response> {
  if (response.ok) {
    return response;
  }

  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    // not JSON
  }

  throw toApiError(response.status, body);
}

export { ApiError };
