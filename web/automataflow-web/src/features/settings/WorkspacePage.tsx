import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { unwrap } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, Field, PageHeader, Spinner, Stat, inputClass } from '../../components/ui';
import { formatDateTime } from '../../lib/format';

const metricLabels: Record<string, string> = {
  'records.imported': 'Records imported',
  'attempts.started': 'Record attempts started',
};

export function WorkspacePage() {
  const { api, can } = useApp();
  const queries = useQueryClient();
  const [name, setName] = useState<string | null>(null);
  const workspace = useQuery({ queryKey: ['workspace'], queryFn: () => unwrap(api.GET('/api/v1/workspace')) });
  const usage = useQuery({ queryKey: ['usage', 30], queryFn: () => unwrap(api.GET('/api/v1/workspace/usage', { params: { query: { days: 30 } } })), enabled: can('Admin') });
  const rename = useMutation({
    mutationFn: (value: string) => unwrap(api.PUT('/api/v1/workspace', { body: { name: value } })),
    onSuccess: () => {
      setName(null);
      void queries.invalidateQueries({ queryKey: ['workspace'] });
      void queries.invalidateQueries({ queryKey: ['me'] });
    },
  });

  if (workspace.isPending) {
    return <Spinner />;
  }

  if (workspace.isError) {
    return <ErrorState error={workspace.error} />;
  }

  const w = workspace.data;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (name?.trim()) {
      rename.mutate(name.trim());
    }
  };

  return (
    <>
      <PageHeader title="Workspace" description={`Created ${formatDateTime(w.createdAt)} · ${w.memberCount} member${w.memberCount === 1 ? '' : 's'}`} />
      <div className="grid gap-6 lg:grid-cols-2">
        <Card title="Profile">
          {can('Owner') ? (
            <form onSubmit={submit} className="space-y-3" noValidate>
              <Field label="Workspace name">
                <input className={inputClass} value={name ?? w.name} maxLength={200} onChange={(e) => setName(e.target.value)} name="workspaceName" />
              </Field>
              <p className="text-xs text-slate-500">
                Identifier: <code>{w.slug}</code>
              </p>
              {rename.isError && <ErrorState error={rename.error} />}
              <Button type="submit" disabled={rename.isPending || !name || name.trim() === w.name}>
                Save
              </Button>
            </form>
          ) : (
            <dl className="space-y-1 text-sm">
              <dt className="text-slate-500">Name</dt>
              <dd>{w.name}</dd>
              <dt className="text-slate-500">Identifier</dt>
              <dd>
                <code>{w.slug}</code>
              </dd>
            </dl>
          )}
        </Card>
        {can('Admin') && (
          <Card title="Usage (last 30 days)">
            {usage.isPending ? (
              <Spinner />
            ) : usage.isError ? (
              <ErrorState error={usage.error} />
            ) : (
              <div className="grid grid-cols-2 gap-4" data-testid="usage">
                {Object.keys(metricLabels).map((metric) => (
                  <Stat key={metric} label={metricLabels[metric]!} value={usage.data.totals[metric] ?? 0} />
                ))}
              </div>
            )}
          </Card>
        )}
      </div>
    </>
  );
}
