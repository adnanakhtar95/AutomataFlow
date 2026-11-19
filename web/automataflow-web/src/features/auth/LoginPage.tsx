import { useState, type FormEvent } from 'react';
import { Navigate, useLocation } from 'react-router';
import { ApiError } from '../../api/errors';
import { useApp } from '../../app/AppContext';
import { Button, Field, inputClass } from '../../components/ui';

export function LoginPage() {
  const { status, signIn, endReason } = useApp();
  const location = useLocation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const from = (location.state as { from?: string } | null)?.from ?? '/';
  if (status === 'signed-in') {
    return <Navigate to={from} replace />;
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError(null);
    if (!email.trim() || !password) {
      setError('Enter your e-mail address and password.');
      return;
    }

    setBusy(true);
    try {
      await signIn(email.trim(), password);
    } catch (e) {
      setError(e instanceof ApiError ? e.title : 'Sign-in failed.');
    } finally {
      setBusy(false);
      setPassword('');
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center px-4">
      <form onSubmit={(e) => void submit(e)} className="w-full max-w-sm space-y-4 rounded-lg bg-white p-6 shadow-sm ring-1 ring-slate-200" noValidate>
        <div>
          <h1 className="text-lg font-semibold text-indigo-700">AutomataFlow</h1>
          <p className="text-sm text-slate-500">Sign in to your workspace</p>
        </div>
        {endReason === 'expired' && !error && (
          <p role="status" className="rounded-md bg-amber-50 p-2 text-sm text-amber-800">
            Your session has ended. Please sign in again.
          </p>
        )}
        <Field label="E-mail">
          <input className={inputClass} type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} name="email" />
        </Field>
        <Field label="Password">
          <input className={inputClass} type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} name="password" />
        </Field>
        {error && (
          <p role="alert" className="text-sm text-rose-700">
            {error}
          </p>
        )}
        <Button type="submit" className="w-full" disabled={busy}>
          {busy ? 'Signing in…' : 'Sign in'}
        </Button>
      </form>
    </div>
  );
}
