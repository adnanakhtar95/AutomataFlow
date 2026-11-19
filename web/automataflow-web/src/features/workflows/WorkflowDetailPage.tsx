import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, PageHeader, Spinner, StatusBadge, Table } from '../../components/ui';
import { formatDateTime, humanize } from '../../lib/format';

function PublishDialog({ workflowId, version, onDone }: { workflowId: string; version: Schemas['VersionSummary']; onDone: () => void }) {
  const { api } = useApp();
  const queries = useQueryClient();
  const [acknowledge, setAcknowledge] = useState(false);
  const detail = useQuery({
    queryKey: ['version', workflowId, version.id],
    queryFn: () => unwrap(api.GET('/api/v1/workflows/{workflowId}/versions/{versionId}', { params: { path: { workflowId, versionId: version.id } } })),
  });
  const validation = useQuery({
    queryKey: ['validation', version.id],
    queryFn: () => unwrap(api.POST('/api/v1/workflows/validate', { body: { definition: JSON.parse(detail.data!.definitionJson) as never } })),
    enabled: !!detail.data,
  });
  const publish = useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/workflows/{workflowId}/versions/{versionId}/publish', { params: { path: { workflowId, versionId: version.id } }, body: { acknowledgeAttestations: acknowledge } })),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['workflow', workflowId] });
      void queries.invalidateQueries({ queryKey: ['workflows'] });
      onDone();
    },
  });
  const attestations = validation.data?.attestations ?? [];

  return (
    <Card title={`Publish version ${version.versionNumber}`} className="ring-2 ring-indigo-200">
      <div className="space-y-3 text-sm" data-testid="publish-dialog">
        <p>Published versions are immutable. New jobs can use this version once it is published.</p>
        {validation.isPending ? (
          <Spinner label="Checking the definition…" />
        ) : validation.isError ? (
          <ErrorState error={validation.error} />
        ) : attestations.length > 0 ? (
          <div className="rounded-md bg-amber-50 p-3 text-amber-900 ring-1 ring-amber-200">
            <p className="font-medium">This definition makes claims the platform cannot verify:</p>
            <ul className="mt-1 list-disc pl-5">
              {attestations.map((a) => (
                <li key={a}>{humanize(a)}</li>
              ))}
            </ul>
            <p className="mt-2 text-xs">For example, that the target system itself refuses a second submission with the same idempotency key. If a claim is wrong, a retried record could create a duplicate.</p>
            <label className="mt-2 flex items-center gap-2">
              <input type="checkbox" checked={acknowledge} onChange={(e) => setAcknowledge(e.target.checked)} name="acknowledgeAttestations" />I confirm these claims are true for the target system.
            </label>
          </div>
        ) : null}
        {publish.isError && <ErrorState error={publish.error} />}
        <div className="flex gap-2">
          <Button disabled={publish.isPending || validation.isPending || (attestations.length > 0 && !acknowledge)} onClick={() => publish.mutate()}>
            Publish
          </Button>
          <Button variant="secondary" onClick={onDone}>
            Cancel
          </Button>
        </div>
      </div>
    </Card>
  );
}

export function WorkflowDetailPage() {
  const { workflowId = '' } = useParams();
  const { api, can } = useApp();
  const queries = useQueryClient();
  const [publishing, setPublishing] = useState<Schemas['VersionSummary'] | null>(null);
  const [viewing, setViewing] = useState<string | null>(null);

  const workflow = useQuery({
    queryKey: ['workflow', workflowId],
    queryFn: () => unwrap(api.GET('/api/v1/workflows/{workflowId}', { params: { path: { workflowId } } })),
  });
  const viewed = useQuery({
    queryKey: ['version', workflowId, viewing],
    queryFn: () => unwrap(api.GET('/api/v1/workflows/{workflowId}/versions/{versionId}', { params: { path: { workflowId, versionId: viewing! } } })),
    enabled: !!viewing,
  });
  const archive = useMutation({
    mutationFn: (versionId: string) => unwrap(api.POST('/api/v1/workflows/{workflowId}/versions/{versionId}/archive', { params: { path: { workflowId, versionId } } })),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['workflow', workflowId] }),
  });

  if (workflow.isPending) {
    return <Spinner />;
  }

  if (workflow.isError) {
    return <ErrorState error={workflow.error} onRetry={() => void workflow.refetch()} />;
  }

  const { workflow: w, versions } = workflow.data;
  const ordered = [...versions].sort((a, b) => b.versionNumber - a.versionNumber);

  return (
    <>
      <PageHeader
        title={w.name}
        description={w.description ?? undefined}
        actions={
          w.publishedVersionId &&
          can('Operator') && (
            <Link to={`/jobs/new?workflow=${w.id}`} className="rounded-md bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-indigo-500">
              Run with v{w.publishedVersionNumber}
            </Link>
          )
        }
      />
      {publishing && <div className="mb-6"><PublishDialog workflowId={w.id} version={publishing} onDone={() => setPublishing(null)} /></div>}
      {archive.isError && <ErrorState error={archive.error} />}
      <Card title="Versions">
        <Table head={['Version', 'Status', 'Created', 'Published', 'Hash', '']}>
          {ordered.map((v) => (
            <tr key={v.id} data-testid="version-row">
              <td className="px-3 py-2 font-medium">v{v.versionNumber}</td>
              <td className="px-3 py-2">
                <StatusBadge status={v.status} />
                {v.id === w.publishedVersionId && <span className="ml-2 text-xs text-emerald-700">used by new jobs</span>}
                {v.acknowledgedAttestations && <span className="ml-2 text-xs text-amber-700" title="Acknowledged attestations">{v.acknowledgedAttestations}</span>}
              </td>
              <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(v.createdAt)}</td>
              <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(v.publishedAt)}</td>
              <td className="px-3 py-2 font-mono text-xs text-slate-400" title={v.definitionHash}>
                {v.definitionHash.slice(0, 12)}
              </td>
              <td className="whitespace-nowrap px-3 py-2 text-right">
                <Button variant="ghost" onClick={() => setViewing(viewing === v.id ? null : v.id)}>
                  {viewing === v.id ? 'Hide' : 'View'}
                </Button>
                {can('Operator') && (
                  <Link to={`/workflows/${w.id}/versions/${v.id}/edit`} className="rounded-md px-3 py-1.5 text-sm font-medium text-slate-600 hover:bg-slate-100">
                    {v.status === 'Draft' ? 'Edit' : 'New version from this'}
                  </Link>
                )}
                {v.status === 'Draft' && can('Admin') && (
                  <Button variant="secondary" onClick={() => setPublishing(v)}>
                    Publish
                  </Button>
                )}
                {v.status === 'Published' && can('Admin') && (
                  <Button variant="ghost" disabled={archive.isPending} onClick={() => window.confirm(`Archive v${v.versionNumber}? New jobs can no longer use it.`) && archive.mutate(v.id)}>
                    Archive
                  </Button>
                )}
              </td>
            </tr>
          ))}
        </Table>
      </Card>
      {viewing && (
        <Card title="Definition" className="mt-6">
          {viewed.isPending ? <Spinner /> : viewed.isError ? <ErrorState error={viewed.error} /> : <pre className="max-h-[32rem] overflow-auto rounded bg-slate-50 p-3 text-xs">{JSON.stringify(JSON.parse(viewed.data.definitionJson), null, 2)}</pre>}
        </Card>
      )}
    </>
  );
}
