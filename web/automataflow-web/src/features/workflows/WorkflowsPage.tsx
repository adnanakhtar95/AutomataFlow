import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { unwrap } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Card, EmptyState, ErrorState, PageHeader, Pager, Spinner, StatusBadge, Table, inputClass } from '../../components/ui';
import { formatDateTime } from '../../lib/format';

const PageSize = 25;

export function WorkflowsPage() {
  const { api, can } = useApp();
  const [params, setParams] = useSearchParams();
  const search = params.get('search') ?? '';
  const skip = Number(params.get('skip') ?? 0) || 0;
  const [draft, setDraft] = useState(search);

  // Debounced search: the query string is the source of truth (shareable, back button works).
  useEffect(() => {
    const timer = setTimeout(() => {
      if (draft !== search) {
        setParams(draft ? { search: draft } : {});
      }
    }, 300);
    return () => clearTimeout(timer);
  }, [draft, search, setParams]);

  const workflows = useQuery({
    queryKey: ['workflows', search, skip],
    queryFn: () => unwrap(api.GET('/api/v1/workflows', { params: { query: { search: search || undefined, skip, take: PageSize } } })),
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <PageHeader
        title="Workflows"
        description="Versioned automation definitions. Jobs always run a published version."
        actions={can('Operator') && <Link to="/workflows/new" className="rounded-md bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-indigo-500">New workflow</Link>}
      />
      <Card>
        <input type="search" aria-label="Search workflows" placeholder="Search by name or description" className={`${inputClass} mb-3 max-w-sm`} value={draft} maxLength={64} onChange={(e) => setDraft(e.target.value)} />
        {workflows.isPending ? (
          <Spinner />
        ) : workflows.isError ? (
          <ErrorState error={workflows.error} onRetry={() => void workflows.refetch()} />
        ) : workflows.data.length === 0 && skip === 0 ? (
          <EmptyState title={search ? 'No workflow matches' : 'No workflows yet'}>{!search && can('Operator') && 'Create one to automate a business process.'}</EmptyState>
        ) : (
          <>
            <Table head={['Name', 'Description', 'Published', 'Versions', 'Created']}>
              {workflows.data.map((w) => (
                <tr key={w.id} data-testid="workflow-row">
                  <td className="px-3 py-2">
                    <Link to={`/workflows/${w.id}`} className="font-medium text-indigo-700 hover:underline">
                      {w.name}
                    </Link>
                  </td>
                  <td className="max-w-md truncate px-3 py-2 text-slate-600">{w.description}</td>
                  <td className="px-3 py-2">{w.publishedVersionNumber ? <StatusBadge status="Published" /> : <span className="text-xs text-slate-400">not published</span>}{w.publishedVersionNumber ? <span className="ml-1 text-xs text-slate-500">v{w.publishedVersionNumber}</span> : null}</td>
                  <td className="px-3 py-2 text-slate-600">{w.latestVersion}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(w.createdAt)}</td>
                </tr>
              ))}
            </Table>
            <Pager skip={skip} take={PageSize} count={workflows.data.length} onChange={(s) => setParams({ ...(search ? { search } : {}), skip: String(s) })} />
          </>
        )}
      </Card>
    </>
  );
}
