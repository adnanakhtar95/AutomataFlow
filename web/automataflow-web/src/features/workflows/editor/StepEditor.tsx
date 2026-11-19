import { useState } from 'react';
import type { Schemas } from '../../../api/client';
import { Button, Field, inputClass } from '../../../components/ui';
import type { DefinitionStep } from '../definition';
import { ActionForms, JsonConfigActions, readSelector, SelectorKinds, writeSelector, type FieldSpec } from './actionForms';

type ActionInfo = Schemas['ActionInfo'];

function SelectorField({ value, onChange, label }: { value: unknown; onChange: (v: unknown) => void; label: string }) {
  const s = readSelector(value);
  return (
    <fieldset className="text-sm">
      <legend className="font-medium text-slate-700">{label}</legend>
      <div className="mt-1 flex gap-2">
        <select aria-label={`${label} selector kind`} className={`${inputClass} w-32`} value={s.kind} onChange={(e) => onChange(writeSelector(e.target.value, s.value, s.name))}>
          {SelectorKinds.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </select>
        <input aria-label={`${label} selector`} className={inputClass} value={s.value} onChange={(e) => onChange(writeSelector(s.kind, e.target.value, s.name))} />
        {s.kind === 'role' && (
          <input aria-label={`${label} accessible name`} placeholder="name" className={inputClass} value={s.name} onChange={(e) => onChange(writeSelector(s.kind, s.value, e.target.value))} />
        )}
      </div>
    </fieldset>
  );
}

function ConfigField({ spec, value, onChange }: { spec: FieldSpec; value: unknown; onChange: (v: unknown) => void }) {
  const label = spec.label + (spec.required ? ' *' : '');
  switch (spec.kind) {
    case 'selector':
      return value === undefined && !spec.required ? (
        <Button variant="ghost" className="text-xs" onClick={() => onChange({ testId: '' })}>
          + {spec.label}
        </Button>
      ) : (
        <SelectorField label={label} value={value} onChange={onChange} />
      );
    case 'boolean':
      return (
        <label className="flex items-center gap-2 text-sm text-slate-700">
          <input type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked ? true : undefined)} />
          {spec.label}
        </label>
      );
    case 'number':
      return (
        <Field label={label} hint={spec.hint}>
          <input type="number" className={inputClass} value={typeof value === 'number' ? value : ''} onChange={(e) => onChange(e.target.value === '' ? undefined : Number(e.target.value))} />
        </Field>
      );
    case 'enum':
      return (
        <Field label={label} hint={spec.hint}>
          <select className={inputClass} value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value || undefined)}>
            <option value="">—</option>
            {spec.options!.map((o) => (
              <option key={o} value={o}>
                {o}
              </option>
            ))}
          </select>
        </Field>
      );
    case 'list':
    case 'numberList':
      return (
        <Field label={label} hint={spec.hint}>
          <input
            className={inputClass}
            value={Array.isArray(value) ? value.join(', ') : ''}
            onChange={(e) => {
              const items = e.target.value.split(',').map((x) => x.trim()).filter(Boolean);
              onChange(items.length === 0 ? undefined : spec.kind === 'numberList' ? items.map(Number) : items);
            }}
          />
        </Field>
      );
    default:
      return (
        <Field label={label} hint={spec.hint}>
          <input className={`${inputClass} font-mono`} value={typeof value === 'string' ? value : ''} onChange={(e) => onChange(e.target.value === '' && !spec.required ? undefined : e.target.value)} />
        </Field>
      );
  }
}

/** A JSON object field that keeps the user's text while it does not parse. */
function JsonConfig({ value, onChange }: { value: Record<string, unknown>; onChange: (v: Record<string, unknown>) => void }) {
  const [text, setText] = useState(() => JSON.stringify(value, null, 2));
  const [error, setError] = useState<string | null>(null);
  return (
    <Field label="Configuration (JSON)" error={error} hint="Nested steps and conditions are edited as JSON; validate to see the Api's diagnostics.">
      <textarea
        className={`${inputClass} h-48 font-mono text-xs`}
        value={text}
        spellCheck={false}
        onChange={(e) => {
          setText(e.target.value);
          try {
            const parsed: unknown = JSON.parse(e.target.value);
            if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
              throw new Error('not an object');
            }

            setError(null);
            onChange(parsed as Record<string, unknown>);
          } catch {
            setError('Not a JSON object yet; the last valid configuration is kept.');
          }
        }}
      />
    </Field>
  );
}

export function StepEditor({
  step,
  action,
  onChange,
  onRemove,
  onMove,
  first,
  last,
}: {
  step: DefinitionStep;
  action: ActionInfo | undefined;
  onChange: (step: DefinitionStep) => void;
  onRemove: () => void;
  onMove: (delta: number) => void;
  first: boolean;
  last: boolean;
}) {
  const set = (key: string, value: unknown) => {
    const next: DefinitionStep = { ...step };
    if (value === undefined) {
      delete next[key];
    } else {
      next[key] = value;
    }

    onChange(next);
  };
  const setConfig = (key: string, value: unknown) => {
    const config = { ...step.config };
    if (value === undefined) {
      delete config[key];
    } else {
      config[key] = value;
    }

    onChange({ ...step, config });
  };

  return (
    <li className="rounded-md bg-white p-3 ring-1 ring-slate-200" data-testid="step-editor">
      <div className="flex flex-wrap items-end gap-3">
        <Field label="Step id">
          <input className={`${inputClass} w-48 font-mono`} value={step.id} onChange={(e) => set('id', e.target.value)} />
        </Field>
        <span className="mb-1.5 rounded bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-700">{step.type}</span>
        {!action && <span className="mb-1.5 text-xs text-rose-700">Not an action the Api offers.</span>}
        <div className="ml-auto flex gap-1">
          <Button variant="ghost" disabled={first} onClick={() => onMove(-1)} aria-label="Move step up">
            ↑
          </Button>
          <Button variant="ghost" disabled={last} onClick={() => onMove(1)} aria-label="Move step down">
            ↓
          </Button>
          <Button variant="ghost" onClick={onRemove} aria-label={`Remove step ${step.id}`}>
            Remove
          </Button>
        </div>
      </div>

      <div className="mt-3 grid gap-3 sm:grid-cols-2">
        {JsonConfigActions.has(step.type) || !ActionForms[step.type] ? (
          <div className="sm:col-span-2">
            <JsonConfig value={step.config} onChange={(config) => onChange({ ...step, config })} />
          </div>
        ) : (
          ActionForms[step.type]!.map((spec) => (
            <div key={spec.key} className={spec.kind === 'selector' ? 'sm:col-span-2' : ''}>
              <ConfigField spec={spec} value={step.config[spec.key]} onChange={(v) => setConfig(spec.key, v)} />
            </div>
          ))
        )}
      </div>

      <details className="mt-3 text-sm">
        <summary className="cursor-pointer text-slate-600">Timeout, errors and side effects</summary>
        <div className="mt-2 grid gap-3 sm:grid-cols-3">
          <Field label="Timeout (ms)" hint={action ? `Default ${action.defaultTimeoutMs} ms` : undefined}>
            <input type="number" className={inputClass} value={step.timeoutMs ?? ''} onChange={(e) => set('timeoutMs', e.target.value === '' ? undefined : Number(e.target.value))} />
          </Field>
          {action?.continueAllowed && (
            <Field label="On error">
              <select className={inputClass} value={step.onError ?? ''} onChange={(e) => set('onError', e.target.value || undefined)}>
                <option value="">fail (default)</option>
                <option value="continue">continue</option>
              </select>
            </Field>
          )}
          {action?.sideEffectAllowed && (
            <label className="flex items-center gap-2 self-end pb-1.5 text-slate-700">
              <input type="checkbox" checked={step.sideEffect === true} onChange={(e) => set('sideEffect', e.target.checked ? true : step.sideEffect !== undefined ? false : undefined)} />
              Changes the target system (side effect)
            </label>
          )}
        </div>
        {step.sideEffect === true && (
          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            <Field label="Idempotency key" hint="Usually {{operation.id}}, sent to the target so it can refuse duplicates.">
              <input
                className={`${inputClass} font-mono`}
                value={step.idempotency?.key ?? ''}
                onChange={(e) => set('idempotency', e.target.value ? { key: e.target.value, guarantee: step.idempotency?.guarantee ?? 'targetEnforced' } : undefined)}
              />
            </Field>
            {step.idempotency && <p className="self-end pb-1.5 text-xs text-amber-800">Guarantee: targetEnforced — an attestation that must be acknowledged when publishing.</p>}
          </div>
        )}
      </details>
    </li>
  );
}
