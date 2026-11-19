import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ensureOk, unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, EmptyState, ErrorState, Field, PageHeader, Spinner, inputClass } from '../../components/ui';
import { formatBytes } from '../../lib/format';
import { parseDefinition } from '../workflows/definition';

/** Mirrors the Api's defaults (Imports:MaxBytes); the Api re-checks everything, this only gives early feedback. */
export const MaxUploadBytes = 10 * 1024 * 1024;
const Accepted: Record<string, string> = { '.csv': 'text/csv', '.json': 'application/json' };

/** Client-side pre-check of a record file. Returns an error message or null. */
export function checkRecordFile(file: File | null): string | null {
  if (!file) {
    return 'Choose a .csv or .json record file.';
  }

  const extension = file.name.slice(file.name.lastIndexOf('.')).toLowerCase();
  if (!(extension in Accepted)) {
    return 'Only .csv and .json record files are accepted.';
  }

  if (file.size === 0) {
    return 'The file is empty.';
  }

  if (file.size > MaxUploadBytes) {
    return `The file is larger than ${formatBytes(MaxUploadBytes)}.`;
  }

  return null;
}

export function NewJobPage() {
  const { api, authFetch } = useApp();
  const queries = useQueryClient();
  const [search] = useSearchParams();
  const [workflowId, setWorkflowId] = useState(search.get('workflow') ?? '');
  const [values, setValues] = useState<Record<string, string>>({});
  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [ignoreUnknown, setIgnoreUnknown] = useState(false);
  const [maxConcurrency, setMaxConcurrency] = useState('2');
  const [maxAttempts, setMaxAttempts] = useState('3');
  const [idempotencyKey] = useState(() => crypto.randomUUID());

  const workflows = useQuery({ queryKey: ['workflows', 'published-picker'], queryFn: () => unwrap(api.GET('/api/v1/workflows', { params: { query: { take: 200 } } })) });
  const publishable = (workflows.data ?? []).filter((w) => w.publishedVersionId);
  const selected = publishable.find((w) => w.id === workflowId);

  const version = useQuery({
    queryKey: ['version', selected?.id, selected?.publishedVersionId],
    queryFn: () => unwrap(api.GET('/api/v1/workflows/{workflowId}/versions/{versionId}', { params: { path: { workflowId: selected!.id, versionId: selected!.publishedVersionId! } } })),
    enabled: !!selected?.publishedVersionId,
  });
  const definition = useMemo(() => (version.data ? parseDefinition(version.data.definitionJson) : null), [version.data]);

  const create = useMutation({
    mutationFn: async (): Promise<Schemas['JobCreated']> => {
      const form = new FormData();
      form.append('workflowVersionId', selected!.publishedVersionId!);
      form.append('parameters', JSON.stringify(Object.fromEntries((definition?.parameters ?? []).map((p) => [p.name, values[p.name] ?? '']))));
      form.append('options', JSON.stringify({ maxConcurrency: Number(maxConcurrency), maxAttempts: Number(maxAttempts) }));
      form.append('ignoreUnknownColumns', String(ignoreUnknown));
      const extension = file!.name.slice(file!.name.lastIndexOf('.')).toLowerCase();
      form.append('file', new File([file!], file!.name, { type: Accepted[extension] }));
      const response = await ensureOk(await authFetch('/api/v1/jobs', { method: 'POST', body: form, headers: { 'Idempotency-Key': idempotencyKey } }));
      return (await response.json()) as Schemas['JobCreated'];
    },
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['jobs'] }),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const error = checkRecordFile(file);
    setFileError(error);
    if (!error && selected) {
      create.mutate();
    }
  };

  if (create.data) {
    const c = create.data;
    return (
      <>
        <PageHeader title="Job created" />
        <Card>
          <div className="space-y-2 text-sm" data-testid="job-created">
            <p>
              <strong>{c.totalRecords}</strong> records imported, <strong>{c.totalRecords - c.invalidRecords}</strong> valid and queued,{' '}
              <strong className={c.invalidRecords > 0 ? 'text-rose-700' : ''}>{c.invalidRecords}</strong> invalid.
            </p>
            {c.replayed && <p className="text-slate-500">This upload was already submitted; the existing job is shown.</p>}
            {c.invalidRecords > 0 && <p className="text-slate-500">Invalid records are not executed. Their validation errors are listed on the job page.</p>}
            <Link to={`/jobs/${c.jobId}`} className="inline-block rounded-md bg-indigo-600 px-3 py-1.5 font-medium text-white hover:bg-indigo-500">
              Open the job
            </Link>
          </div>
        </Card>
      </>
    );
  }

  return (
    <>
      <PageHeader title="New job" description="Run a published workflow over a CSV or JSON record file." />
      {workflows.isPending ? (
        <Spinner />
      ) : workflows.isError ? (
        <ErrorState error={workflows.error} />
      ) : publishable.length === 0 ? (
        <EmptyState title="No published workflow">
          Publish a workflow version first. <Link to="/workflows" className="text-indigo-700 hover:underline">Go to workflows</Link>
        </EmptyState>
      ) : (
        <form onSubmit={submit} className="grid gap-6 lg:grid-cols-3" noValidate>
          <div className="space-y-6 lg:col-span-2">
            <Card title="1. Workflow">
              <Field label="Published workflow">
                <select className={inputClass} value={workflowId} onChange={(e) => { setWorkflowId(e.target.value); setValues({}); }} name="workflow">
                  <option value="">Choose…</option>
                  {publishable.map((w) => (
                    <option key={w.id} value={w.id}>
                      {w.name} (v{w.publishedVersionNumber})
                    </option>
                  ))}
                </select>
              </Field>
            </Card>

            {selected && (
              <Card title="2. Parameters">
                {version.isPending ? (
                  <Spinner />
                ) : version.isError ? (
                  <ErrorState error={version.error} />
                ) : (definition?.parameters ?? []).length === 0 ? (
                  <p className="text-sm text-slate-500">This workflow declares no parameters.</p>
                ) : (
                  <div className="grid gap-4 sm:grid-cols-2">
                    {definition!.parameters!.map((p) => (
                      <Field key={p.name} label={p.name} hint={p.type === 'url' ? 'An origin, e.g. https://crm.example.com' : undefined}>
                        <input className={inputClass} name={`param-${p.name}`} value={values[p.name] ?? ''} onChange={(e) => setValues({ ...values, [p.name]: e.target.value })} />
                      </Field>
                    ))}
                  </div>
                )}
                {(definition?.secrets ?? []).length > 0 && (
                  <p className="mt-4 text-xs text-slate-500">
                    Uses workspace secrets: {definition!.secrets!.join(', ')}. Values are set under Secrets and never shown.
                  </p>
                )}
              </Card>
            )}

            {selected && (
              <Card title="3. Records">
                <Field label="Record file" hint="A .csv file with a header row, or a .json array of flat objects." error={fileError}>
                  <input
                    type="file"
                    name="file"
                    accept=".csv,.json"
                    className="block w-full text-sm text-slate-600 file:mr-3 file:rounded-md file:border-0 file:bg-slate-100 file:px-3 file:py-1.5 file:text-sm file:font-medium"
                    onChange={(e) => {
                      const f = e.target.files?.[0] ?? null;
                      setFile(f);
                      setFileError(f ? checkRecordFile(f) : null);
                    }}
                  />
                </Field>
                {(definition?.inputs ?? []).length > 0 && (
                  <div className="mt-4">
                    <p className="text-xs font-medium uppercase tracking-wide text-slate-500">Expected columns</p>
                    <ul className="mt-1 flex flex-wrap gap-1 text-xs">
                      {definition!.inputs!.map((i) => (
                        <li key={i.name} className="rounded bg-slate-100 px-1.5 py-0.5 text-slate-700">
                          {i.name}
                          {i.required ? '*' : ''} <span className="text-slate-400">{i.type}</span>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
                <label className="mt-4 flex items-center gap-2 text-sm text-slate-700">
                  <input type="checkbox" checked={ignoreUnknown} onChange={(e) => setIgnoreUnknown(e.target.checked)} name="ignoreUnknownColumns" />
                  Ignore columns the workflow does not declare
                </label>
              </Card>
            )}
          </div>

          {selected && (
            <div className="space-y-6">
              <Card title="Options">
                <div className="space-y-4">
                  <Field label="Parallel records" hint="Records of this job running at the same time.">
                    <input type="number" min={1} max={20} className={inputClass} value={maxConcurrency} onChange={(e) => setMaxConcurrency(e.target.value)} />
                  </Field>
                  <Field label="Attempts per record" hint="Retries apply only to transient failures that are safe to repeat.">
                    <input type="number" min={1} max={10} className={inputClass} value={maxAttempts} onChange={(e) => setMaxAttempts(e.target.value)} />
                  </Field>
                </div>
              </Card>
              {create.isError && <ErrorState error={create.error} />}
              <Button type="submit" className="w-full" disabled={create.isPending || !definition}>
                {create.isPending ? 'Uploading…' : 'Create job'}
              </Button>
            </div>
          )}
        </form>
      )}
    </>
  );
}
