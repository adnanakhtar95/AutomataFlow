import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { AppProvider } from '../app/AppContext';
import { routes } from '../app/router';
import { Session } from '../auth/session';

export type Handler = (request: Request) => Response | Promise<Response> | undefined;

export const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

export const tokenBody = (role = 'Owner') => ({
  accessToken: 'access-token',
  accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
  refreshToken: null,
  refreshTokenExpiresAt: new Date(Date.now() + 86_400_000).toISOString(),
  organizationId: 'org-1',
  role,
});

export const profile = (role = 'Owner') => ({
  userId: 'user-1',
  email: 'ada@example.test',
  organizationId: 'org-1',
  organizationName: 'Acme Logistics',
  organizationSlug: 'acme',
  role,
  isPlatformOperator: false,
  memberships: [{ organizationId: 'org-1', organizationName: 'Acme Logistics', organizationSlug: 'acme', role }],
});

/**
 * Renders the real routes with a fake network. `handlers` answer requests by method and path; anything unanswered
 * fails loudly. `signedIn` decides whether the cookie restore at start-up succeeds, and with which role.
 */
export function renderApp(path: string, { signedIn, handlers = [] }: { signedIn: string | false; handlers?: Handler[] }) {
  const requests: Request[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(new URL(String(input), 'http://localhost'), init);
    requests.push(request);
    const { pathname } = new URL(request.url);
    if (pathname === '/api/v1/auth/refresh') {
      return signedIn ? json(tokenBody(signedIn)) : json({ title: 'Authentication failed.' }, 401);
    }

    if (pathname === '/api/v1/me' && signedIn) {
      return json(profile(signedIn));
    }

    for (const handler of handlers) {
      const response = await handler(request);
      if (response) {
        return response;
      }
    }

    throw new Error(`Unexpected request ${request.method} ${pathname}`);
  });
  vi.stubGlobal('fetch', fetchMock);

  const session = new Session({ locks: null, channel: null });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const view = render(
    <QueryClientProvider client={queryClient}>
      <AppProvider session={session}>
        <RouterProvider router={router} />
      </AppProvider>
    </QueryClientProvider>,
  );
  return { ...view, router, session, requests, fetchMock };
}

export function on(method: string, path: string | RegExp, respond: (request: Request) => Response | Promise<Response>): Handler {
  return (request) => {
    const { pathname } = new URL(request.url);
    const matches = typeof path === 'string' ? pathname === path : path.test(pathname);
    return request.method === method && matches ? respond(request) : undefined;
  };
}
