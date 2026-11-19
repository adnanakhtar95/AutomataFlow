import { useState } from 'react';
import { Link, NavLink, Outlet, useMatches } from 'react-router';
import { useApp } from '../app/AppContext';
import type { Role } from '../lib/roles';

interface NavItem {
  to: string;
  label: string;
  minimum: Role;
  end?: boolean;
}

const navigation: NavItem[] = [
  { to: '/', label: 'Overview', minimum: 'Viewer', end: true },
  { to: '/workflows', label: 'Workflows', minimum: 'Viewer' },
  { to: '/jobs', label: 'Jobs', minimum: 'Viewer' },
  { to: '/jobs/new', label: 'New job', minimum: 'Operator', end: true },
  { to: '/settings/secrets', label: 'Secrets', minimum: 'Admin' },
];

export interface Crumb {
  label: string;
  to?: string;
}

/** Route handle: a static label or a function of the route params. */
export interface RouteHandle {
  crumb?: string | ((params: Record<string, string | undefined>) => string);
}

function Breadcrumbs() {
  const matches = useMatches();
  const crumbs: Crumb[] = matches
    .filter((m) => (m.handle as RouteHandle | undefined)?.crumb)
    .map((m) => {
      const crumb = (m.handle as RouteHandle).crumb!;
      return { label: typeof crumb === 'function' ? crumb(m.params) : crumb, to: m.pathname };
    });

  if (crumbs.length <= 1) {
    return null;
  }

  return (
    <nav aria-label="Breadcrumb" className="mb-4 text-sm text-slate-500">
      <ol className="flex flex-wrap items-center gap-1">
        {crumbs.map((c, i) => (
          <li key={c.to ?? i} className="flex items-center gap-1">
            {i > 0 && <span aria-hidden>/</span>}
            {i < crumbs.length - 1 && c.to ? (
              <Link to={c.to} className="hover:text-slate-800">
                {c.label}
              </Link>
            ) : (
              <span className="text-slate-800">{c.label}</span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

function UserMenu() {
  const { profile, signOut } = useApp();
  const [open, setOpen] = useState(false);

  return (
    <div className="relative">
      <button
        type="button"
        className="flex items-center gap-2 rounded-md px-2 py-1 text-sm text-slate-700 hover:bg-slate-100"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <span className="flex h-7 w-7 items-center justify-center rounded-full bg-indigo-100 text-xs font-semibold text-indigo-700" aria-hidden>
          {(profile?.email ?? '?').charAt(0).toUpperCase()}
        </span>
        <span className="hidden max-w-48 truncate sm:inline">{profile?.email ?? '…'}</span>
      </button>
      {open && (
        <div role="menu" className="absolute right-0 z-20 mt-2 w-64 rounded-md bg-white p-2 text-sm shadow-lg ring-1 ring-slate-200">
          <div className="border-b border-slate-100 px-2 pb-2">
            <p className="truncate font-medium text-slate-800">{profile?.email}</p>
            <p className="text-xs text-slate-500">
              {profile?.role} in {profile?.organizationName}
            </p>
          </div>
          <button type="button" role="menuitem" className="mt-1 w-full rounded px-2 py-1.5 text-left text-slate-700 hover:bg-slate-100" onClick={() => void signOut()}>
            Sign out
          </button>
        </div>
      )}
    </div>
  );
}

export function Layout() {
  const { profile, can } = useApp();
  const [menuOpen, setMenuOpen] = useState(false);
  const items = navigation.filter((n) => can(n.minimum));

  const nav = (
    <nav aria-label="Main" className="space-y-1">
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.end ?? false}
          onClick={() => setMenuOpen(false)}
          className={({ isActive }) =>
            `block rounded-md px-3 py-2 text-sm font-medium ${isActive ? 'bg-indigo-50 text-indigo-700' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900'}`
          }
        >
          {item.label}
        </NavLink>
      ))}
    </nav>
  );

  return (
    <div className="min-h-screen lg:flex">
      <aside className="hidden w-60 shrink-0 border-r border-slate-200 bg-white px-3 py-4 lg:block">
        <Link to="/" className="mb-6 block px-3 text-lg font-semibold text-indigo-700">
          AutomataFlow
        </Link>
        {nav}
      </aside>
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-between gap-4 border-b border-slate-200 bg-white px-4 py-2">
          <div className="flex items-center gap-3">
            <button type="button" className="rounded-md p-1.5 text-slate-600 hover:bg-slate-100 lg:hidden" aria-label="Open navigation" onClick={() => setMenuOpen((o) => !o)}>
              ☰
            </button>
            <div>
              <p className="text-xs uppercase tracking-wide text-slate-400">Workspace</p>
              <p className="text-sm font-semibold text-slate-800" data-testid="workspace-name">
                {profile?.organizationName ?? '…'}
              </p>
            </div>
          </div>
          <UserMenu />
        </header>
        {menuOpen && <div className="border-b border-slate-200 bg-white px-3 py-3 lg:hidden">{nav}</div>}
        <main className="mx-auto w-full max-w-7xl flex-1 px-4 py-6 sm:px-6">
          <Breadcrumbs />
          <Outlet />
        </main>
      </div>
    </div>
  );
}
