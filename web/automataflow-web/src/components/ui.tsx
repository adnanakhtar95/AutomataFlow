import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { ApiError } from '../api/errors';
import { humanize } from '../lib/format';

type Variant = 'primary' | 'secondary' | 'danger' | 'ghost';

const variants: Record<Variant, string> = {
  primary: 'bg-indigo-600 text-white hover:bg-indigo-500 disabled:bg-indigo-300',
  secondary: 'bg-white text-slate-700 ring-1 ring-slate-300 hover:bg-slate-50 disabled:text-slate-400',
  danger: 'bg-rose-600 text-white hover:bg-rose-500 disabled:bg-rose-300',
  ghost: 'text-slate-600 hover:bg-slate-100 disabled:text-slate-300',
};

export function Button({ variant = 'primary', className = '', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant }) {
  return (
    <button
      type="button"
      {...props}
      className={`inline-flex items-center justify-center gap-2 rounded-md px-3 py-1.5 text-sm font-medium shadow-sm transition disabled:cursor-not-allowed ${variants[variant]} ${className}`}
    />
  );
}

export function Card({ title, actions, children, className = '' }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`rounded-lg bg-white shadow-sm ring-1 ring-slate-200 ${className}`}>
      {(title || actions) && (
        <header className="flex items-center justify-between gap-4 border-b border-slate-200 px-4 py-3">
          <h2 className="text-sm font-semibold text-slate-800">{title}</h2>
          {actions && <div className="flex items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className="p-4">{children}</div>
    </section>
  );
}

export function PageHeader({ title, description, actions }: { title: string; description?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-6 flex flex-wrap items-start justify-between gap-4">
      <div>
        <h1 className="text-xl font-semibold text-slate-900">{title}</h1>
        {description && <p className="mt-1 text-sm text-slate-500">{description}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}

const statusColors: Record<string, string> = {
  Pending: 'bg-slate-100 text-slate-700',
  Running: 'bg-sky-100 text-sky-800',
  Completed: 'bg-emerald-100 text-emerald-800',
  Succeeded: 'bg-emerald-100 text-emerald-800',
  Reconciled: 'bg-emerald-100 text-emerald-800',
  ReconciledFound: 'bg-emerald-100 text-emerald-800',
  Published: 'bg-emerald-100 text-emerald-800',
  CompletedWithErrors: 'bg-amber-100 text-amber-800',
  NeedsReview: 'bg-amber-100 text-amber-900 ring-1 ring-amber-300',
  AwaitingFence: 'bg-amber-100 text-amber-800',
  Indeterminate: 'bg-amber-100 text-amber-800',
  Draft: 'bg-slate-100 text-slate-700',
  Failed: 'bg-rose-100 text-rose-800',
  Invalid: 'bg-rose-50 text-rose-700',
  Cancelled: 'bg-slate-200 text-slate-600',
  Archived: 'bg-slate-200 text-slate-600',
  Abandoned: 'bg-slate-200 text-slate-600',
};

export function StatusBadge({ status }: { status: string }) {
  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${statusColors[status] ?? 'bg-slate-100 text-slate-700'}`}>
      {humanize(status)}
    </span>
  );
}

export function Spinner({ label = 'Loading…' }: { label?: string }) {
  return (
    <div role="status" className="flex items-center gap-2 py-6 text-sm text-slate-500">
      <span className="h-4 w-4 animate-spin rounded-full border-2 border-slate-300 border-t-indigo-600" aria-hidden />
      {label}
    </div>
  );
}

/** Shows an error as text (never HTML). Validation errors list their path and message. */
export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const apiError = error instanceof ApiError ? error : null;
  const title = apiError?.isNotFound ? 'Not found, or not in this workspace.' : apiError?.title ?? 'Something went wrong.';
  return (
    <div role="alert" className="rounded-md bg-rose-50 p-4 text-sm text-rose-800 ring-1 ring-rose-200">
      <p className="font-medium">{title}</p>
      {apiError?.detail && <p className="mt-1">{apiError.detail}</p>}
      {apiError && apiError.errors.length > 0 && (
        <ul className="mt-2 list-disc space-y-1 pl-5">
          {apiError.errors.map((e, i) => (
            <li key={i}>
              {e.path && <code className="mr-1 rounded bg-rose-100 px-1">{e.path}</code>}
              {e.message || e.code}
            </li>
          ))}
        </ul>
      )}
      {onRetry && (
        <Button variant="secondary" className="mt-3" onClick={onRetry}>
          Try again
        </Button>
      )}
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed border-slate-300 p-8 text-center">
      <p className="text-sm font-medium text-slate-700">{title}</p>
      {children && <div className="mt-2 text-sm text-slate-500">{children}</div>}
    </div>
  );
}

export function Field({ label, hint, error, children }: { label: string; hint?: ReactNode; error?: string | null; children: ReactNode }) {
  return (
    <label className="block text-sm">
      <span className="font-medium text-slate-700">{label}</span>
      <div className="mt-1">{children}</div>
      {hint && !error && <span className="mt-1 block text-xs text-slate-500">{hint}</span>}
      {error && <span className="mt-1 block text-xs text-rose-700">{error}</span>}
    </label>
  );
}

export const inputClass =
  'block w-full rounded-md border-0 px-2.5 py-1.5 text-sm text-slate-900 shadow-sm ring-1 ring-slate-300 placeholder:text-slate-400 focus:ring-2 focus:ring-indigo-600 disabled:bg-slate-50';

export function Stat({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="rounded-lg bg-white p-4 shadow-sm ring-1 ring-slate-200">
      <p className="text-xs font-medium uppercase tracking-wide text-slate-500">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-slate-900">{value}</p>
      {hint && <p className="mt-1 text-xs text-slate-500">{hint}</p>}
    </div>
  );
}

export function Table({ head, children, empty }: { head: ReactNode[]; children: ReactNode; empty?: ReactNode }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full divide-y divide-slate-200 text-sm">
        <thead>
          <tr>
            {head.map((h, i) => (
              <th key={i} scope="col" className="whitespace-nowrap px-3 py-2 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100">{children}</tbody>
      </table>
      {empty}
    </div>
  );
}

export function Pager({ skip, take, count, onChange }: { skip: number; take: number; count: number; onChange: (skip: number) => void }) {
  return (
    <div className="mt-3 flex items-center justify-end gap-2 text-sm text-slate-600">
      <span>
        {count === 0 ? 'No rows' : `Rows ${skip + 1}–${skip + count}`}
      </span>
      <Button variant="secondary" disabled={skip === 0} onClick={() => onChange(Math.max(0, skip - take))}>
        Previous
      </Button>
      <Button variant="secondary" disabled={count < take} onClick={() => onChange(skip + take)}>
        Next
      </Button>
    </div>
  );
}
