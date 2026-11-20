import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, Field, PageHeader, Spinner, StatusBadge, Table, inputClass } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { invitationLink } from '../settings/MembersPage';

/** Platform operators only (never a workspace role): onboard, suspend and reactivate workspaces. Counts only, no customer data. */
export function PlatformWorkspacesPage() {
  const { api, profile } = useApp();
  const queries = useQueryClient();
  const [name, setName] = useState('');
  const [slug, setSlug] = useState('');
  const [ownerEmail, setOwnerEmail] = useState('');
  const [created, setCreated] = useState<Schemas['PlatformWorkspaceCreated'] | null>(null);
  const workspaces = useQuery({ queryKey: ['platform-workspaces'], queryFn: () => unwrap(api.GET('/api/v1/platform/workspaces')) });
  const create = useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/platform/workspaces', { body: { name: name.trim(), slug: slug.trim(), ownerEmail: ownerEmail.trim() } })),
    onSuccess: (result) => {
      setCreated(result);
      setName('');
      setSlug('');
      setOwnerEmail('');
      void queries.invalidateQueries({ queryKey: ['platform-workspaces'] });
    },
  });
  const status = useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'suspend' | 'reactivate' }) =>
      action === 'suspend'
        ? unwrap(api.POST('/api/v1/platform/workspaces/{organizationId}/suspend', { params: { path: { organizationId: id } } }))
        : unwrap(api.POST('/api/v1/platform/workspaces/{organizationId}/reactivate', { params: { path: { organizationId: id } } })),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['platform-workspaces'] }),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (name.trim() && slug.trim() && ownerEmail.includes('@')) {
      create.mutate();
    }
  };

  return (
    <>
      <PageHeader title="Platform: workspaces" description="Onboard customer workspaces and control their status. Only platform operators see this page." />
      <div className="grid gap-6 lg:grid-cols-3">
        <Card title="Workspaces" className="lg:col-span-2">
          {workspaces.isPending ? (
            <Spinner />
          ) : workspaces.isError ? (
            <ErrorState error={workspaces.error} />
          ) : (
            <Table head={['Workspace', 'Status', 'Members', 'Jobs (active / total)', 'Created', '']}>
              {workspaces.data.map((w) => (
                <tr key={w.id} data-testid="platform-workspace-row">
                  <td className="px-3 py-2">
                    {w.name} <code className="text-xs text-slate-400">{w.slug}</code>
                  </td>
                  <td className="px-3 py-2">
                    <StatusBadge status={w.status} />
                  </td>
                  <td className="px-3 py-2">{w.memberCount}</td>
                  <td className="px-3 py-2">
                    {w.activeJobs} / {w.totalJobs}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(w.createdAt)}</td>
                  <td className="px-3 py-2 text-right">
                    {w.id !== profile?.organizationId &&
                      (w.status === 'Active' ? (
                        <Button variant="ghost" disabled={status.isPending} onClick={() => window.confirm(`Suspend ${w.name}? Its members are signed out and no job runs until it is reactivated.`) && status.mutate({ id: w.id, action: 'suspend' })}>
                          Suspend
                        </Button>
                      ) : (
                        <Button variant="secondary" disabled={status.isPending} onClick={() => status.mutate({ id: w.id, action: 'reactivate' })}>
                          Reactivate
                        </Button>
                      ))}
                  </td>
                </tr>
              ))}
            </Table>
          )}
          {status.isError && <ErrorState error={status.error} />}
        </Card>
        <Card title="Onboard a business">
          <form onSubmit={submit} className="space-y-3" noValidate>
            <Field label="Workspace name">
              <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} name="workspaceName" />
            </Field>
            <Field label="Identifier" hint="Lowercase letters, digits and single hyphens.">
              <input className={`${inputClass} font-mono`} value={slug} onChange={(e) => setSlug(e.target.value.toLowerCase())} name="workspaceSlug" />
            </Field>
            <Field label="Owner's e-mail address">
              <input type="email" className={inputClass} value={ownerEmail} onChange={(e) => setOwnerEmail(e.target.value)} name="ownerEmail" autoComplete="off" />
            </Field>
            {create.isError && <ErrorState error={create.error} />}
            <Button type="submit" disabled={create.isPending}>
              Create workspace
            </Button>
          </form>
          {created && (
            <div className="mt-4 rounded-md bg-emerald-50 p-3 text-sm text-emerald-900 ring-1 ring-emerald-200" data-testid="owner-invitation-link">
              <p className="font-medium">
                {created.workspace.name} created. Send this Owner invitation to {created.ownerInvitation.email}:
              </p>
              <input readOnly aria-label="Owner invitation link" className={`${inputClass} mt-2 font-mono text-xs`} value={invitationLink(created.ownerInvitation.token)} onFocus={(e) => e.target.select()} />
              <p className="mt-1 text-xs">Shown only now; expires {formatDateTime(created.ownerInvitation.expiresAt)}.</p>
            </div>
          )}
        </Card>
      </div>
    </>
  );
}
