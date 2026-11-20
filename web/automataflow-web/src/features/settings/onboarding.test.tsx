import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { json, on, renderApp, tokenBody } from '../../test/harness';
import { tokenFromFragment } from '../auth/AcceptInvitationPage';
import { invitationLink } from './MembersPage';

afterEach(() => {
  window.history.replaceState(null, '', '/');
});

const members = [
  { userId: 'user-1', email: 'ada@example.test', role: 'Owner', joinedAt: '2026-11-01T10:00:00Z' },
  { userId: 'user-2', email: 'bob@example.test', role: 'Operator', joinedAt: '2026-11-02T10:00:00Z' },
];

describe('invitation links', () => {
  it('carry the token only in the URL fragment, which browsers never send to servers', () => {
    const link = invitationLink('abc_DEF-123', 'https://automataflow.example.com');
    expect(link).toBe('https://automataflow.example.com/invite#abc_DEF-123');
    expect(new URL(link).search).toBe('');
    expect(tokenFromFragment(new URL(link).hash)).toBe('abc_DEF-123');
  });
});

describe('accepting an invitation', () => {
  it('creates the account, starts the session and removes the token from the address bar', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/invite#secret-token');
    let accepted: unknown = null;
    const app = renderApp('/invite', {
      signedIn: false,
      handlers: [
        on('POST', '/api/v1/invitations/preview', () => json({ organizationName: 'Acme Freight', email: 'new@example.test', role: 'Operator', expiresAt: '2026-12-01T00:00:00Z', accountExists: false })),
        on('POST', '/api/v1/invitations/accept', async (r) => {
          accepted = await r.json();
          return json(tokenBody('Operator'));
        }),
        on('GET', '/api/v1/overview', () => json({ totalJobs: 0, jobsByStatus: {}, totalRecords: 0, recordsByStatus: {}, totalWorkflows: 0, publishedWorkflows: 0, recentJobs: [], recentWorkflows: [] })),
      ],
    });

    expect(await screen.findByText('Join Acme Freight')).toBeInTheDocument();
    await user.type(screen.getByLabelText(/Choose a password/), 'short');
    await user.click(screen.getByRole('button', { name: 'Create account and join' }));
    expect(screen.getByRole('alert')).toHaveTextContent('at least 12 characters');

    await user.clear(screen.getByLabelText(/Choose a password/));
    await user.type(screen.getByLabelText(/Choose a password/), 'Correct-Horse-2026');
    await user.type(screen.getByLabelText(/Repeat the password/), 'Correct-Horse-2026');
    await user.click(screen.getByRole('button', { name: 'Create account and join' }));

    await waitFor(() => expect(app.router.state.location.pathname).toBe('/'));
    expect(accepted).toEqual({ token: 'secret-token', password: 'Correct-Horse-2026' });
    expect(window.location.hash).toBe('');
    const accept = app.requests.find((r) => new URL(r.url).pathname === '/api/v1/invitations/accept')!;
    expect(accept.headers.get('X-AutomataFlow-Session')).toBe('browser');
  });

  it('asks an existing account for its password and shows an unusable link as an error', async () => {
    window.history.replaceState(null, '', '/invite#used-token');
    renderApp('/invite', { signedIn: false, handlers: [on('POST', '/api/v1/invitations/preview', () => json({ title: 'The invitation is invalid, expired, revoked or already used.' }, 404))] });
    expect(await screen.findByRole('alert')).toHaveTextContent('invalid, expired, revoked or already used');
  });
});

describe('members page', () => {
  it('lets an Admin invite and shows the one-time link with the token in the fragment', async () => {
    const user = userEvent.setup();
    renderApp('/settings/members', {
      signedIn: 'Admin',
      handlers: [
        on('GET', '/api/v1/workspace/members', () => json(members)),
        on('GET', '/api/v1/workspace/invitations', () => json([])),
        on('POST', '/api/v1/workspace/invitations', () => json({ id: 'inv-1', email: 'carol@example.test', role: 'Operator', expiresAt: '2026-11-27T00:00:00Z', token: 'tok-123' }, 201)),
      ],
    });

    expect(await screen.findAllByTestId('member-row')).toHaveLength(2);
    // Admins cannot change or remove an Owner, and cannot invite Owners.
    expect(screen.queryByLabelText('Role of ada@example.test')).toBeNull();
    expect(within(screen.getByLabelText('Role of bob@example.test')).queryByText('Owner')).toBeNull();
    expect(within(screen.getByRole('combobox', { name: 'Role' })).queryByText('Owner')).toBeNull();

    await user.type(screen.getByLabelText('E-mail address'), 'carol@example.test');
    await user.click(screen.getByRole('button', { name: 'Create invitation' }));
    const link = await screen.findByLabelText('Invitation link');
    expect((link as HTMLInputElement).value).toMatch(/\/invite#tok-123$/);
  });

  it('shows members but no invitation tools to a Viewer', async () => {
    renderApp('/settings/members', { signedIn: 'Viewer', handlers: [on('GET', '/api/v1/workspace/members', () => json(members))] });
    expect(await screen.findAllByTestId('member-row')).toHaveLength(2);
    expect(screen.queryByText('Invite someone')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Remove' })).toBeNull();
  });
});

describe('platform area', () => {
  it('is hidden from workspace users, even Owners', async () => {
    renderApp('/platform/workspaces', { signedIn: 'Owner', handlers: [] });
    expect(await screen.findByText('You do not have access to this page')).toBeInTheDocument();
    expect(within(screen.getAllByRole('navigation', { name: 'Main' })[0]!).queryByText('Platform')).toBeNull();
  });
});
