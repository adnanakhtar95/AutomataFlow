import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { authorizedFetch, createApi, unwrap, type Api, type Schemas } from '../api/client';
import { Session, SessionHeader, type SessionEndReason, type SessionTokens } from '../auth/session';
import { toApiError } from '../api/errors';
import { atLeast, type Role } from '../lib/roles';

export type Profile = Schemas['MeResponse'];

type Status = 'restoring' | 'signed-out' | 'signed-in';

interface AppContextValue {
  status: Status;
  endReason: SessionEndReason | null;
  session: Session;
  api: Api;
  /** fetch with the bearer token, for uploads and downloads that openapi-fetch does not model. */
  authFetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  profile: Profile | undefined;
  signIn: (email: string, password: string) => Promise<void>;
  signOut: () => Promise<void>;
  can: (minimum: Role) => boolean;
}

const AppContext = createContext<AppContextValue | null>(null);

export function AppProvider({ session, children }: { session: Session; children: ReactNode }) {
  const queries = useQueryClient();
  const [tokens, setTokens] = useState<SessionTokens | null>(session.current);
  const [status, setStatus] = useState<Status>(session.current ? 'signed-in' : 'restoring');
  const [endReason, setEndReason] = useState<SessionEndReason | null>(null);
  const api = useMemo(() => createApi(session), [session]);
  const authFetch = useMemo(() => authorizedFetch(session), [session]);

  useEffect(() => {
    const unsubscribe = session.subscribe((next, reason) => {
      setTokens(next);
      setStatus(next ? 'signed-in' : 'signed-out');
      if (!next) {
        setEndReason(reason);
        queries.clear(); // nothing of the previous session stays cached
      }
    });

    if (session.current === null) {
      void session.restore().then((restored) => {
        if (!restored) {
          setStatus('signed-out');
        }
      });
    }

    return unsubscribe;
  }, [session, queries]);

  const profile = useQuery({
    queryKey: ['me', tokens?.organizationId],
    queryFn: () => unwrap(api.GET('/api/v1/me')),
    enabled: status === 'signed-in',
    staleTime: 60_000,
  });

  const value: AppContextValue = {
    status,
    endReason,
    session,
    api,
    authFetch,
    profile: profile.data,
    signIn: async (email, password) => {
      const response = await fetch('/api/v1/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...SessionHeader },
        body: JSON.stringify({ email, password }),
      });
      const body: unknown = await response.json().catch(() => null);
      if (!response.ok) {
        throw toApiError(response.status, body);
      }

      setEndReason(null);
      session.start(body as Schemas['TokenResponse']);
    },
    signOut: () => session.signOut(),
    can: (minimum) => atLeast(profile.data?.role ?? tokens?.role, minimum),
  };

  return <AppContext.Provider value={value}>{children}</AppContext.Provider>;
}

export function useApp(): AppContextValue {
  const value = useContext(AppContext);
  if (value === null) {
    throw new Error('useApp must be used inside AppProvider.');
  }

  return value;
}
