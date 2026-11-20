import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { unwrap } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, Spinner } from '../../components/ui';

/** Ready-made workflow packages (Phase 9): copying one creates a draft the customer reviews and publishes. */
export function TemplatePicker({ name }: { name: string }) {
  const { api } = useApp();
  const navigate = useNavigate();
  const queries = useQueryClient();
  const templates = useQuery({ queryKey: ['workflow-templates'], queryFn: () => unwrap(api.GET('/api/v1/workflow-templates')), staleTime: Infinity });
  const use = useMutation({
    mutationFn: (templateId: string) => unwrap(api.POST('/api/v1/workflow-templates/{templateId}/instantiate', { params: { path: { templateId } }, body: { name } })),
    onSuccess: (created) => {
      void queries.invalidateQueries({ queryKey: ['workflows'] });
      navigate(`/workflows/${created.workflowId}`);
    },
  });

  return (
    <Card title="Start from a template">
      {templates.isPending ? (
        <Spinner />
      ) : templates.isError ? (
        <ErrorState error={templates.error} />
      ) : (
        <ul className="grid gap-3 md:grid-cols-3" data-testid="templates">
          {templates.data.map((t) => (
            <li key={t.id} className="flex flex-col rounded-md p-3 ring-1 ring-slate-200">
              <span className="text-xs font-medium uppercase tracking-wide text-indigo-600">{t.category}</span>
              <span className="font-medium text-slate-900">{t.title}</span>
              <p className="mt-1 flex-1 text-xs text-slate-600">{t.summary}</p>
              <p className="mt-2 text-xs text-slate-500">{t.setup}</p>
              <Button variant="secondary" className="mt-3" disabled={!name.trim() || use.isPending} onClick={() => use.mutate(t.id)} aria-label={`Use template ${t.title}`}>
                Use this template
              </Button>
            </li>
          ))}
        </ul>
      )}
      {!name.trim() && <p className="mt-2 text-xs text-slate-500">Enter a workflow name above first.</p>}
      {use.isError && <ErrorState error={use.error} />}
    </Card>
  );
}
