import type { ReactNode } from 'react';
import { isRouteErrorResponse, Link, Navigate, useLocation, useRouteError } from 'react-router';
import { ApiError } from '../../api/errors';
import { useApp } from '../../app/AppContext';
import { ErrorState, Spinner } from '../../components/ui';
import type { Role } from '../../lib/roles';

/** Signed-in routes only; while the session is being restored from the cookie, nothing protected renders. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { status } = useApp();
  const location = useLocation();

  if (status === 'restoring') {
    return <Spinner label="Restoring your session…" />;
  }

  if (status === 'signed-out') {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }

  return <>{children}</>;
}

/** Hides a page the caller's role may not use. The Api enforces the same rule; this only avoids a dead end. */
export function RequireRole({ minimum, children }: { minimum: Role; children: ReactNode }) {
  const { can, profile } = useApp();
  if (!profile) {
    return <Spinner />;
  }

  return can(minimum) ? <>{children}</> : <Forbidden />;
}

/** Platform-operator pages; workspace users (whatever their role) see "no access". The Api enforces the same rule. */
export function RequirePlatformOperator({ children }: { children: ReactNode }) {
  const { profile } = useApp();
  if (!profile) {
    return <Spinner />;
  }

  return profile.isPlatformOperator ? <>{children}</> : <Forbidden />;
}

export function Forbidden() {
  return (
    <div className="py-12 text-center">
      <h1 className="text-lg font-semibold text-slate-900">You do not have access to this page</h1>
      <p className="mt-2 text-sm text-slate-500">Ask a workspace administrator if you need it.</p>
      <Link to="/" className="mt-4 inline-block text-sm text-indigo-700 hover:underline">
        Back to the overview
      </Link>
    </div>
  );
}

export function NotFound() {
  return (
    <div className="py-12 text-center">
      <h1 className="text-lg font-semibold text-slate-900">Page not found</h1>
      <Link to="/" className="mt-4 inline-block text-sm text-indigo-700 hover:underline">
        Back to the overview
      </Link>
    </div>
  );
}

export function RouteError() {
  const error = useRouteError();
  if (isRouteErrorResponse(error) && error.status === 404) {
    return <NotFound />;
  }

  if (error instanceof ApiError && error.isForbidden) {
    return <Forbidden />;
  }

  return (
    <div className="p-6">
      <ErrorState error={error instanceof ApiError ? error : new Error('Unexpected error')} />
    </div>
  );
}
