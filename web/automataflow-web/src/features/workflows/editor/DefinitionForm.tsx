import { useState } from 'react';
import type { Schemas } from '../../../api/client';
import { Button, Card, Field, inputClass } from '../../../components/ui';
import type { DefinitionInput, DefinitionParameter, DefinitionStep, Section, WorkflowDefinitionJson } from '../definition';
import { Sections } from '../definition';
import { defaultConfig } from './actionForms';
import { StepEditor } from './StepEditor';

type Catalog = Schemas['WorkflowCatalog'];

const sectionHelp: Record<Section, string> = {
  setup: 'Runs before every record attempt, e.g. signing in. No side effects.',
  steps: 'The work for one record.',
  reconcile: 'Looks up whether an earlier, uncertain attempt already changed the target system.',
};

function ListEditor({ label, values, onChange, hint, placeholder }: { label: string; values: string[]; onChange: (v: string[]) => void; hint?: string; placeholder?: string }) {
  return (
    <Field label={label} hint={hint}>
      <div className="space-y-2">
        {values.map((v, i) => (
          <div key={i} className="flex gap-2">
            <input aria-label={`${label} ${i + 1}`} className={`${inputClass} font-mono`} value={v} placeholder={placeholder} onChange={(e) => onChange(values.map((x, j) => (j === i ? e.target.value : x)))} />
            <Button variant="ghost" onClick={() => onChange(values.filter((_, j) => j !== i))} aria-label={`Remove ${label} ${i + 1}`}>
              ✕
            </Button>
          </div>
        ))}
        <Button variant="secondary" onClick={() => onChange([...values, ''])}>
          Add
        </Button>
      </div>
    </Field>
  );
}

function uniqueStepId(definition: WorkflowDefinitionJson, type: string): string {
  const used = new Set(Sections.flatMap((s) => (definition[s] ?? []).map((step) => step.id)));
  let n = 1;
  while (used.has(`${type}-${n}`)) {
    n++;
  }

  return `${type}-${n}`;
}

function SectionEditor({ section, definition, catalog, onChange }: { section: Section; definition: WorkflowDefinitionJson; catalog: Catalog; onChange: (d: WorkflowDefinitionJson) => void }) {
  const steps = definition[section] ?? [];
  const allowed = catalog.actions.filter((a) => a.sections.includes(section));
  const [adding, setAdding] = useState(allowed[0]?.type ?? '');
  const setSteps = (next: DefinitionStep[]) => onChange({ ...definition, [section]: next });

  return (
    <Card title={`${section.charAt(0).toUpperCase()}${section.slice(1)} (${steps.length})`}>
      <p className="mb-3 text-xs text-slate-500">{sectionHelp[section]}</p>
      <ol className="space-y-3">
        {steps.map((step, i) => (
          <StepEditor
            key={i}
            step={step}
            action={catalog.actions.find((a) => a.type === step.type)}
            first={i === 0}
            last={i === steps.length - 1}
            onChange={(s) => setSteps(steps.map((x, j) => (j === i ? s : x)))}
            onRemove={() => setSteps(steps.filter((_, j) => j !== i))}
            onMove={(delta) => {
              const next = [...steps];
              const [moved] = next.splice(i, 1);
              next.splice(i + delta, 0, moved!);
              setSteps(next);
            }}
          />
        ))}
      </ol>
      <div className="mt-3 flex items-center gap-2">
        <select aria-label={`Action to add to ${section}`} className={`${inputClass} w-48`} value={adding} onChange={(e) => setAdding(e.target.value)}>
          {allowed.map((a) => (
            <option key={a.type} value={a.type}>
              {a.type}
            </option>
          ))}
        </select>
        <Button variant="secondary" disabled={!adding} onClick={() => setSteps([...steps, { id: uniqueStepId(definition, adding), type: adding, config: defaultConfig(adding) }])}>
          Add step
        </Button>
      </div>
    </Card>
  );
}

/** The guided editor over the definition's JSON tree. Every change produces a new definition object. */
export function DefinitionForm({ definition, catalog, onChange }: { definition: WorkflowDefinitionJson; catalog: Catalog; onChange: (d: WorkflowDefinitionJson) => void }) {
  const set = <K extends keyof WorkflowDefinitionJson>(key: K, value: WorkflowDefinitionJson[K] | undefined) => {
    const next = { ...definition };
    if (value === undefined) {
      delete next[key];
    } else {
      next[key] = value;
    }

    onChange(next);
  };
  const parameters = definition.parameters ?? [];
  const inputs = definition.inputs ?? [];
  const setParameters = (p: DefinitionParameter[]) => set('parameters', p);
  const setInputs = (i: DefinitionInput[]) => set('inputs', i);

  return (
    <div className="space-y-6" data-testid="definition-form">
      <Card title="General">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Definition name">
            <input className={inputClass} value={definition.name ?? ''} onChange={(e) => set('name', e.target.value)} />
          </Field>
          <Field label="Timeout per record (seconds)">
            <input type="number" className={inputClass} value={definition.timeoutSeconds ?? ''} onChange={(e) => set('timeoutSeconds', e.target.value === '' ? undefined : Number(e.target.value))} />
          </Field>
          <div className="sm:col-span-2">
            <Field label="Description">
              <input className={inputClass} value={definition.description ?? ''} onChange={(e) => set('description', e.target.value || undefined)} />
            </Field>
          </div>
        </div>
      </Card>

      <Card title="Connection">
        <div className="grid gap-6 lg:grid-cols-3">
          <Field label="Parameters" hint="Set when a job is created, e.g. the target system's address.">
            <div className="space-y-2">
              {parameters.map((p, i) => (
                <div key={i} className="flex gap-2">
                  <input aria-label={`Parameter ${i + 1} name`} className={`${inputClass} font-mono`} value={p.name} onChange={(e) => setParameters(parameters.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
                  <select aria-label={`Parameter ${i + 1} type`} className={`${inputClass} w-24`} value={p.type} onChange={(e) => setParameters(parameters.map((x, j) => (j === i ? { ...x, type: e.target.value } : x)))}>
                    {catalog.parameterTypes.map((t) => (
                      <option key={t}>{t}</option>
                    ))}
                  </select>
                  <Button variant="ghost" onClick={() => setParameters(parameters.filter((_, j) => j !== i))} aria-label={`Remove parameter ${i + 1}`}>
                    ✕
                  </Button>
                </div>
              ))}
              <Button variant="secondary" onClick={() => setParameters([...parameters, { name: '', type: catalog.parameterTypes[0] ?? 'string' }])}>
                Add
              </Button>
            </div>
          </Field>
          <ListEditor label="Allowed hosts" values={definition.allowedHosts ?? []} onChange={(v) => set('allowedHosts', v)} placeholder="{{params.baseUrl}}" hint="The browser may only reach these origins." />
          <ListEditor label="Secrets" values={definition.secrets ?? []} onChange={(v) => set('secrets', v)} placeholder="crmPassword" hint="Names of workspace secrets; values are set under Secrets." />
        </div>
      </Card>

      <Card title="Record inputs" actions={<Button variant="secondary" onClick={() => setInputs([...inputs, { name: '', type: 'string', required: true }])}>Add input</Button>}>
        <p className="mb-3 text-xs text-slate-500">The columns of the record file. Sensitive inputs are encrypted at rest and masked in logs.</p>
        <div className="space-y-2">
          {inputs.map((input, i) => {
            const update = (patch: Partial<DefinitionInput>) => setInputs(inputs.map((x, j) => (j === i ? { ...x, ...patch } : x)));
            return (
              <div key={i} className="flex flex-wrap items-center gap-2">
                <input aria-label={`Input ${i + 1} name`} className={`${inputClass} w-44 font-mono`} value={input.name} onChange={(e) => update({ name: e.target.value })} />
                <select aria-label={`Input ${i + 1} type`} className={`${inputClass} w-28`} value={input.type} onChange={(e) => update({ type: e.target.value })}>
                  {catalog.inputTypes.map((t) => (
                    <option key={t}>{t}</option>
                  ))}
                </select>
                <input aria-label={`Input ${i + 1} maximum length`} type="number" placeholder="max length" className={`${inputClass} w-28`} value={input.maxLength ?? ''} onChange={(e) => update({ maxLength: e.target.value === '' ? undefined : Number(e.target.value) })} />
                <label className="flex items-center gap-1 text-sm">
                  <input type="checkbox" checked={input.required === true} onChange={(e) => update({ required: e.target.checked })} /> required
                </label>
                <label className="flex items-center gap-1 text-sm">
                  <input type="checkbox" checked={input.sensitive === true} onChange={(e) => update({ sensitive: e.target.checked || undefined })} /> sensitive
                </label>
                <Button variant="ghost" onClick={() => setInputs(inputs.filter((_, j) => j !== i))} aria-label={`Remove input ${i + 1}`}>
                  ✕
                </Button>
              </div>
            );
          })}
        </div>
      </Card>

      <Card title="Reconciliation">
        <p className="mb-3 text-xs text-slate-500">Required when a step changes the target system: how the platform finds out whether an uncertain attempt took effect.</p>
        {definition.reconciliation ? (
          <div className="flex flex-wrap items-end gap-4">
            <Field label="Evidence">
              <select className={`${inputClass} w-40`} value={definition.reconciliation.evidence} onChange={(e) => set('reconciliation', { ...definition.reconciliation!, evidence: e.target.value })}>
                {catalog.evidenceLevels.map((t) => (
                  <option key={t}>{t}</option>
                ))}
              </select>
            </Field>
            <Field label="Match key">
              <select className={`${inputClass} w-40`} value={definition.reconciliation.matchKey} onChange={(e) => set('reconciliation', { ...definition.reconciliation!, matchKey: e.target.value })}>
                {catalog.matchKeys.map((t) => (
                  <option key={t}>{t}</option>
                ))}
              </select>
            </Field>
            <Button variant="ghost" onClick={() => set('reconciliation', undefined)}>
              Remove reconciliation
            </Button>
          </div>
        ) : (
          <Button variant="secondary" onClick={() => set('reconciliation', { evidence: catalog.evidenceLevels[0] ?? 'advisory', matchKey: catalog.matchKeys[0] ?? 'operationId' })}>
            Add reconciliation
          </Button>
        )}
      </Card>

      {Sections.map((section) => (
        <SectionEditor key={section} section={section} definition={definition} catalog={catalog} onChange={onChange} />
      ))}
    </div>
  );
}
