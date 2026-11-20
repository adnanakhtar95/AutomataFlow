import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, Field, PageHeader, Spinner, StatusBadge, Table, inputClass } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { Roles, type Role } from '../../lib/roles';

/** The invitation link carries the token in the URL FRAGMENT: browsers never send fragments to servers or proxies. */
export function invitationLink(token: string, origin = window.location.origin): string {
  return `${origin}/invite#${encodeURIComponent(token)}`;
}

function InviteForm({ onCreated }: { onCreated: (created: Schemas['InvitationCreated']) => void }) {
  const { api, profile } = useApp();
  const queries = useQueryClient();
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<Role>('Operator');
  const invite = useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/workspace/invitations', { body: { email: email.trim(), role } })),
    onSuccess: (created) => {
      setEmail('');
      onCreated(created);
      void queries.invalidateQueries({ queryKey: ['invitations'] });
    },
  });
  const allowed = Roles.filter((r) => r !== 'Owner' || profile?.role === 'Owner');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (email.includes('@')) {
      invite.mutate();
    }
  };

  return (
    <form onSubmit={submit} className="space-y-3" noValidate>
      <Field label="E-mail address">
        <input type="email" className={inputClass} value={email} onChange={(e) => setEmail(e.target.value)} name="inviteEmail" autoComplete="off" />
      </Field>
      <Field label="Role">
        <select className={inputClass} value={role} onChange={(e) => setRole(e.target.value as Role)} name="inviteRole">
          {allowed.map((r) => (
            <option key={r}>{r}</option>
          ))}
        </select>
      </Field>
      {invite.isError && <ErrorState error={invite.error} />}
      <Button type="submit" disabled={invite.isPending || !email.includes('@')}>
        Create invitation
      </Button>
    </form>
  );
}

export function MembersPage() {
  const { api, profile, can } = useApp();
  const queries = useQueryClient();
  const [created, setCreated] = useState<Schemas['InvitationCreated'] | null>(null);
  const [copied, setCopied] = useState(false);
  const members = useQuery({ queryKey: ['members'], queryFn: () => unwrap(api.GET('/api/v1/workspace/members')) });
  const invitations = useQuery({ queryKey: ['invitations'], queryFn: () => unwrap(api.GET('/api/v1/workspace/invitations')), enabled: can('Admin') });
  const changeRole = useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: Role }) => unwrap(api.PUT('/api/v1/workspace/members/{userId}/role', { params: { path: { userId } }, body: { role } })),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['members'] }),
  });
  const remove = useMutation({
    mutationFn: (userId: string) => unwrap(api.DELETE('/api/v1/workspace/members/{userId}', { params: { path: { userId } } })),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['members'] }),
  });
  const revoke = useMutation({
    mutationFn: (invitationId: string) => unwrap(api.DELETE('/api/v1/workspace/invitations/{invitationId}', { params: { path: { invitationId } } })),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['invitations'] }),
  });

  const roleOptions = (current: string) => Roles.filter((r) => profile?.role === 'Owner' || (r !== 'Owner' && current !== 'Owner'));

  return (
    <>
      <PageHeader title="Members" description="People with access to this workspace and their roles. Removing someone ends their sessions here immediately." />
      <div className="grid gap-6 lg:grid-cols-3">
        <Card title="Members" className="lg:col-span-2">
          {members.isPending ? (
            <Spinner />
          ) : members.isError ? (
            <ErrorState error={members.error} />
          ) : (
            <Table head={['E-mail', 'Role', 'Joined', '']}>
              {members.data.map((m) => (
                <tr key={m.userId} data-testid="member-row">
                  <td className="px-3 py-2">
                    {m.email}
                    {m.userId === profile?.userId && <span className="ml-2 text-xs text-slate-400">(you)</span>}
                  </td>
                  <td className="px-3 py-2">
                    {can('Admin') && (m.role !== 'Owner' || profile?.role === 'Owner') ? (
                      <select
                        aria-label={`Role of ${m.email}`}
                        className={`${inputClass} w-32`}
                        value={m.role}
                        disabled={changeRole.isPending}
                        onChange={(e) => changeRole.mutate({ userId: m.userId, role: e.target.value as Role })}
                      >
                        {roleOptions(m.role).map((r) => (
                          <option key={r}>{r}</option>
                        ))}
                      </select>
                    ) : (
                      <StatusBadge status={m.role} />
                    )}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(m.joinedAt)}</td>
                  <td className="px-3 py-2 text-right">
                    {can('Admin') && (m.role !== 'Owner' || profile?.role === 'Owner') && (
                      <Button variant="ghost" disabled={remove.isPending} onClick={() => window.confirm(`Remove ${m.email} from this workspace?`) && remove.mutate(m.userId)}>
                        {m.userId === profile?.userId ? 'Leave' : 'Remove'}
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </Table>
          )}
          {changeRole.isError && <ErrorState error={changeRole.error} />}
          {remove.isError && <ErrorState error={remove.error} />}
        </Card>

        {can('Admin') && (
          <div className="space-y-6">
            <Card title="Invite someone">
              <InviteForm
                onCreated={(c) => {
                  setCreated(c);
                  setCopied(false);
                }}
              />
              {created && (
                <div className="mt-4 rounded-md bg-emerald-50 p-3 text-sm text-emerald-900 ring-1 ring-emerald-200" data-testid="invitation-link">
                  <p className="font-medium">Invitation for {created.email} ({created.role})</p>
                  <p className="mt-1 text-xs">Send this link to them yourself; it is shown only now and expires {formatDateTime(created.expiresAt)}.</p>
                  <input readOnly aria-label="Invitation link" className={`${inputClass} mt-2 font-mono text-xs`} value={invitationLink(created.token)} onFocus={(e) => e.target.select()} />
                  <Button
                    variant="secondary"
                    className="mt-2"
                    onClick={() => void navigator.clipboard?.writeText(invitationLink(created.token)).then(() => setCopied(true))}
                  >
                    {copied ? 'Copied' : 'Copy link'}
                  </Button>
                </div>
              )}
            </Card>
            <Card title="Invitations">
              {invitations.isPending ? (
                <Spinner />
              ) : invitations.isError ? (
                <ErrorState error={invitations.error} />
              ) : invitations.data.length === 0 ? (
                <p className="text-sm text-slate-500">No invitations yet.</p>
              ) : (
                <ul className="divide-y divide-slate-100 text-sm">
                  {invitations.data.map((i) => (
                    <li key={i.id} className="flex items-center justify-between gap-2 py-2">
                      <span>
                        {i.email} <span className="text-xs text-slate-400">{i.role}</span>
                        <br />
                        <span className="text-xs text-slate-500">
                          {i.state} · expires {formatDateTime(i.expiresAt)}
                        </span>
                      </span>
                      {i.state === 'Open' && (
                        <Button variant="ghost" disabled={revoke.isPending} onClick={() => revoke.mutate(i.id)}>
                          Revoke
                        </Button>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          </div>
        )}
      </div>
    </>
  );
}
