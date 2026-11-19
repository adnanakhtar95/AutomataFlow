/**
 * Form descriptions of the action configurations of workflow schemaVersion 1 (docs/workflow-schema-v1.json). The editor
 * offers only actions the Api lists in its catalog; actions with nested steps or expressions (condition, captureResult,
 * reconcileMatch) are edited as JSON. The Api validates every definition; these forms only shape the JSON.
 */
export type FieldKind = 'selector' | 'template' | 'text' | 'boolean' | 'number' | 'enum' | 'list' | 'numberList';

export interface FieldSpec {
  key: string;
  label: string;
  kind: FieldKind;
  required?: boolean;
  options?: string[];
  hint?: string;
}

export const SelectorKinds = ['testId', 'css', 'role', 'text', 'label', 'placeholder'] as const;

export const ActionForms: Record<string, FieldSpec[]> = {
  navigate: [
    { key: 'url', label: 'URL', kind: 'template', required: true, hint: 'e.g. {{params.baseUrl}}/orders. The host must be an allowed host.' },
    { key: 'acceptStatus', label: 'Accepted error statuses', kind: 'numberList', hint: '4xx/5xx statuses that do not fail the step, comma separated.' },
  ],
  click: [
    { key: 'selector', label: 'Element', kind: 'selector', required: true },
    { key: 'waitForNavigation', label: 'Wait for the page to navigate', kind: 'boolean' },
  ],
  fill: [
    { key: 'selector', label: 'Field', kind: 'selector', required: true },
    { key: 'value', label: 'Value', kind: 'template', required: true, hint: 'e.g. {{record.email}} or {{secret.password}}' },
  ],
  select: [
    { key: 'selector', label: 'Drop-down', kind: 'selector', required: true },
    { key: 'value', label: 'Option value', kind: 'template', hint: 'Set exactly one of value, label or index.' },
    { key: 'label', label: 'Option label', kind: 'template' },
    { key: 'index', label: 'Option index', kind: 'number' },
  ],
  wait: [
    { key: 'for', label: 'Wait for', kind: 'enum', options: ['visible', 'hidden', 'attached', 'detached', 'load', 'url'] },
    { key: 'selector', label: 'Element', kind: 'selector', hint: 'For visible, hidden, attached and detached.' },
    { key: 'url', label: 'URL pattern', kind: 'text', hint: 'For "url", e.g. **/orders/**' },
  ],
  extract: [
    { key: 'selector', label: 'Element', kind: 'selector', required: true },
    { key: 'source', label: 'Read', kind: 'enum', options: ['text', 'attribute'], required: true },
    { key: 'attribute', label: 'Attribute', kind: 'text', hint: 'When reading an attribute.' },
    { key: 'maxLength', label: 'Maximum length', kind: 'number' },
    { key: 'sensitive', label: 'Sensitive (masked in logs)', kind: 'boolean' },
  ],
  screenshot: [
    { key: 'label', label: 'Label', kind: 'template', required: true },
    { key: 'fullPage', label: 'Full page', kind: 'boolean' },
  ],
  upload: [
    { key: 'selector', label: 'File input', kind: 'selector', required: true },
    { key: 'file', label: 'File', kind: 'template', required: true, hint: 'A file input of the record, e.g. {{record.attachment}}' },
    { key: 'allowedExtensions', label: 'Allowed extensions', kind: 'list', required: true, hint: 'e.g. .pdf, .png' },
    { key: 'maxBytes', label: 'Maximum bytes', kind: 'number' },
  ],
  download: [{ key: 'trigger', label: 'Download link or button', kind: 'selector', required: true }],
  delay: [{ key: 'milliseconds', label: 'Milliseconds', kind: 'number', required: true }],
};

/** Actions whose configuration is edited as JSON (nested steps, expressions or result maps). */
export const JsonConfigActions = new Set(['condition', 'captureResult', 'reconcileMatch']);

/** A configuration skeleton for a newly added step (the Api reports anything still missing). */
export function defaultConfig(type: string): Record<string, unknown> {
  switch (type) {
    case 'navigate':
      return { url: '' };
    case 'click':
    case 'fill':
    case 'select':
    case 'extract':
    case 'upload':
      return { selector: { testId: '' }, ...(type === 'fill' ? { value: '' } : {}), ...(type === 'extract' ? { source: 'text' } : {}) };
    case 'wait':
      return { for: 'visible', selector: { testId: '' } };
    case 'screenshot':
      return { label: '' };
    case 'download':
      return { trigger: { testId: '' } };
    case 'delay':
      return { milliseconds: 1000 };
    case 'condition':
      return { if: { elementExists: { testId: '' } }, then: [], else: [] };
    case 'captureResult':
      return { fields: {} };
    case 'reconcileMatch':
      return { rows: { testId: '' }, key: { attribute: '' }, equals: '{{operation.id}}', emptyIndicator: { testId: '' } };
    default:
      return {};
  }
}

export type SelectorValue = Record<string, string>;

/** The selector's kind and value(s); a role selector carries an accessible name too. */
export function readSelector(value: unknown): { kind: string; value: string; name: string } {
  const selector = (typeof value === 'object' && value !== null ? value : {}) as SelectorValue;
  const kind = SelectorKinds.find((k) => k in selector) ?? 'testId';
  return { kind, value: selector[kind] ?? '', name: selector.name ?? '' };
}

export function writeSelector(kind: string, value: string, name: string): SelectorValue {
  return kind === 'role' && name ? { role: value, name } : { [kind]: value };
}
