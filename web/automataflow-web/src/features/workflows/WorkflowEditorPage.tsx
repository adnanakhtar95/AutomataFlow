import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, Field, PageHeader, Spinner, inputClass } from '../../components/ui';
import { parseDefinition, starterDefinition, type WorkflowDefinitionJson } from './definition';
import { DefinitionForm } from './editor/DefinitionForm';
import { TemplatePicker } from './TemplatePicker';

type Tab = 'form' | 'json';

function ValidationResult({ result }: { result: Schemas['DefinitionValidation'] }) {
  return (
    <div role="status" data-testid="validation-result" className={`rounded-md p-3 text-sm ring-1 ${result.valid ? 'bg-emerald-50 text-emerald-900 ring-emerald-200' : 'bg-rose-50 text-rose-900 ring-rose-200'}`}>
      <p className="font-medium">{result.valid ? 'The definition is valid.' : `The definition has ${result.errors.length} error(s).`}</p>
      {[...result.errors, ...result.warnings].length > 0 && (
        <ul className="mt-2 list-disc space-y-1 pl-5">
          {result.errors.map((e, i) => (
            <li key={`e${i}`}>
              {e.path && <code className="mr-1 rounded bg-white/60 px-1">{e.path}</code>}
              {e.message} <span className="text-xs opacity-70">({e.code})</span>
            </li>
          ))}
          {result.warnings.map((w, i) => (
            <li key={`w${i}`} className="text-amber-800">
              Warning: {w.path && <code className="mr-1">{w.path}</code>}
              {w.message}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Create a workflow, edit a draft, or start a new version from an existing one (published versions are never edited). */
export function WorkflowEditorPage() {
  const { workflowId, versionId } = useParams();
  const isNew = !workflowId;
  const { api } = useApp();
  const navigate = useNavigate();
  const queries = useQueryClient();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [edited, setDefinition] = useState<WorkflowDefinitionJson | null>(isNew ? starterDefinition('new-workflow') : null);
  const [tab, setTab] = useState<Tab>('form');
  const [jsonText, setJsonText] = useState('');
  const [jsonError, setJsonError] = useState<string | null>(null);

  const catalog = useQuery({ queryKey: ['catalog'], queryFn: () => unwrap(api.GET('/api/v1/workflow-catalog')), staleTime: Infinity });
  const source = useQuery({
    queryKey: ['version', workflowId, versionId],
    queryFn: () => unwrap(api.GET('/api/v1/workflows/{workflowId}/versions/{versionId}', { params: { path: { workflowId: workflowId!, versionId: versionId! } } })),
    enabled: !isNew,
  });

  // Until the user edits, the definition is the loaded version's.
  const loaded = useMemo(() => (source.data ? parseDefinition(source.data.definitionJson) : null), [source.data]);
  const definition = edited ?? loaded;

  const isDraft = source.data?.version.status === 'Draft';
  const validate = useMutation({ mutationFn: (d: WorkflowDefinitionJson) => unwrap(api.POST('/api/v1/workflows/validate', { body: { definition: d as never } })) });
  const save = useMutation({
    mutationFn: async (d: WorkflowDefinitionJson) => {
      if (isNew) {
        return unwrap(api.POST('/api/v1/workflows', { body: { name, description: description || null, definition: d as never } }));
      }

      return isDraft
        ? unwrap(api.PUT('/api/v1/workflows/{workflowId}/versions/{versionId}', { params: { path: { workflowId: workflowId!, versionId: versionId! } }, body: { definition: d as never } }))
        : unwrap(api.POST('/api/v1/workflows/{workflowId}/versions', { params: { path: { workflowId: workflowId! } }, body: { definition: d as never } }));
    },
    onSuccess: (saved) => {
      void queries.invalidateQueries({ queryKey: ['workflows'] });
      void queries.invalidateQueries({ queryKey: ['workflow', saved.workflowId] });
      navigate(`/workflows/${saved.workflowId}`);
    },
  });

  /** The definition currently shown: on the JSON tab, the parsed text (null while it does not parse). */
  const current = (): WorkflowDefinitionJson | null => {
    if (tab === 'form') {
      return definition;
    }

    const parsed = parseDefinition(jsonText);
    setJsonError(parsed ? null : 'The JSON does not parse as an object.');
    return parsed;
  };

  const switchTab = (next: Tab) => {
    if (next === tab) {
      return;
    }

    if (next === 'json') {
      setJsonText(JSON.stringify(definition, null, 2));
      setJsonError(null);
      setTab('json');
      return;
    }

    const parsed = current();
    if (parsed) {
      setDefinition(parsed);
      setTab('form');
    }
  };

  if (!isNew && source.isPending) {
    return <Spinner />;
  }

  if (source.isError || catalog.isError) {
    return <ErrorState error={source.error ?? catalog.error} />;
  }

  if (!catalog.data || !definition) {
    return <Spinner />;
  }

  const title = isNew ? 'New workflow' : isDraft ? `Edit draft v${source.data!.version.versionNumber}` : `New version from v${source.data!.version.versionNumber}`;

  return (
    <>
      <PageHeader
        title={title}
        description={isNew ? 'Start from a minimal definition and shape it with the form, or paste JSON.' : !isDraft ? 'Saving creates a new draft version; the source version stays unchanged.' : undefined}
        actions={
          <>
            <Button variant="secondary" disabled={validate.isPending} onClick={() => { const d = current(); if (d) validate.mutate(d); }}>
              Validate
            </Button>
            <Button disabled={save.isPending || (isNew && !name.trim())} onClick={() => { const d = current(); if (d) save.mutate(d); }}>
              {isNew ? 'Create draft' : isDraft ? 'Save draft' : 'Save as new version'}
            </Button>
          </>
        }
      />
      {isNew && (
        <Card className="mb-6">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Workflow name *" hint="Unique in this workspace.">
              <input className={inputClass} value={name} maxLength={64} onChange={(e) => setName(e.target.value)} name="workflowName" />
            </Field>
            <Field label="Description">
              <input className={inputClass} value={description} maxLength={500} onChange={(e) => setDescription(e.target.value)} />
            </Field>
          </div>
        </Card>
      )}
      {isNew && (
        <div className="mb-6">
          <TemplatePicker name={name} />
        </div>
      )}
      <div className="mb-4 space-y-3">
        {validate.data && <ValidationResult result={validate.data} />}
        {validate.isError && <ErrorState error={validate.error} />}
        {save.isError && <ErrorState error={save.error} />}
      </div>
      <div role="tablist" className="mb-4 flex gap-1 border-b border-slate-200">
        {(['form', 'json'] as Tab[]).map((t) => (
          <button
            key={t}
            role="tab"
            type="button"
            aria-selected={tab === t}
            onClick={() => switchTab(t)}
            className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium ${tab === t ? 'border-indigo-600 text-indigo-700' : 'border-transparent text-slate-500 hover:text-slate-800'}`}
          >
            {t === 'form' ? 'Guided form' : 'JSON'}
          </button>
        ))}
      </div>
      {tab === 'form' ? (
        <DefinitionForm definition={definition} catalog={catalog.data} onChange={setDefinition} />
      ) : (
        <Card>
          <Field label="Definition (JSON)" error={jsonError}>
            <textarea aria-label="Definition JSON" className={`${inputClass} h-[36rem] font-mono text-xs`} spellCheck={false} value={jsonText} onChange={(e) => setJsonText(e.target.value)} />
          </Field>
        </Card>
      )}
    </>
  );
}
