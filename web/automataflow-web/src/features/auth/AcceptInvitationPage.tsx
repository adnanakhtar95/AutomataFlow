import { useMutation, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import type { Schemas } from '../../api/client';
import { ApiError, toApiError } from '../../api/errors';
import { useApp } from '../../app/AppContext';
import { SessionHeader } from '../../auth/session';
import { Button, ErrorState, Field, Spinner, inputClass } from '../../components/ui';

/** Reads the invitation token from the URL fragment (never sent to a server, so it is not in any access log). */
export function tokenFromFragment(hash: string): string {
  return decodeURIComponent(hash.replace(/^#/, '')).trim();
}

async function post<T>(path: string, body: unknown, browserSession = false): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(browserSession ? SessionHeader : {}) },
    body: JSON.stringify(body),
  });
  const json: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    throw toApiError(response.status, json);
  }

  return json as T;
}

export const MinimumPasswordLength = 12;

/** Public page: accept a workspace invitation, creating the account or proving the existing one's password. */
export function AcceptInvitationPage() {
  const { startSession } = useApp();
  const navigate = useNavigate();
  const [token] = useState(() => tokenFromFragment(window.location.hash));
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [formError, setFormError] = useState<string | null>(null);

  const preview = useQuery({
    queryKey: ['invitation-preview', token],
    queryFn: () => post<Schemas['InvitationPreview']>('/api/v1/invitations/preview', { token }),
    enabled: token.length > 0,
    retry: false,
  });
  const accept = useMutation({
    mutationFn: () => post<Schemas['TokenResponse']>('/api/v1/invitations/accept', { token, password }, true),
    onSuccess: (session) => {
      // Remove the token from the address bar and history before entering the workspace.
      window.history.replaceState(null, '', '/invite');
      startSession(session);
      navigate('/', { replace: true });
    },
    onSettled: () => {
      setPassword('');
      setConfirm('');
    },
  });

  if (!token) {
    return <Centered><ErrorState error={new ApiError(404, 'This invitation link is incomplete. Open the full link you received.')} /></Centered>;
  }

  if (preview.isPending) {
    return <Centered><Spinner label="Checking the invitation…" /></Centered>;
  }

  if (preview.isError) {
    return <Centered><ErrorState error={preview.error} /></Centered>;
  }

  const p = preview.data;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!p.accountExists && password.length < MinimumPasswordLength) {
      setFormError(`Choose a password of at least ${MinimumPasswordLength} characters.`);
      return;
    }

    if (!p.accountExists && password !== confirm) {
      setFormError('The passwords do not match.');
      return;
    }

    setFormError(null);
    accept.mutate();
  };

  return (
    <Centered>
      <form onSubmit={submit} className="space-y-4" noValidate data-testid="accept-invitation">
        <div>
          <h1 className="text-lg font-semibold text-indigo-700">Join {p.organizationName}</h1>
          <p className="text-sm text-slate-500">
            You are invited as <strong>{p.role}</strong> with the address <strong>{p.email}</strong>.
          </p>
        </div>
        <Field label={p.accountExists ? 'Your password' : 'Choose a password'} hint={p.accountExists ? 'You already have an account: sign in to accept.' : `At least ${MinimumPasswordLength} characters.`}>
          <input type="password" className={inputClass} value={password} onChange={(e) => setPassword(e.target.value)} name="password" autoComplete={p.accountExists ? 'current-password' : 'new-password'} />
        </Field>
        {!p.accountExists && (
          <Field label="Repeat the password">
            <input type="password" className={inputClass} value={confirm} onChange={(e) => setConfirm(e.target.value)} name="confirm" autoComplete="new-password" />
          </Field>
        )}
        {formError && <p role="alert" className="text-sm text-rose-700">{formError}</p>}
        {accept.isError && <ErrorState error={accept.error} />}
        <Button type="submit" className="w-full" disabled={accept.isPending}>
          {p.accountExists ? 'Sign in and join' : 'Create account and join'}
        </Button>
      </form>
    </Centered>
  );
}

function Centered({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center px-4">
      <div className="w-full max-w-sm rounded-lg bg-white p-6 shadow-sm ring-1 ring-slate-200">{children}</div>
    </div>
  );
}
