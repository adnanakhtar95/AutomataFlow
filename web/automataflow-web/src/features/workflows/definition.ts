/**
 * A loose view of a workflow definition (schemaVersion 1) for the dashboard. The Api is the only validator: this module
 * reads and edits the JSON tree, it never decides whether a definition is valid.
 */
export interface DefinitionParameter {
  name: string;
  type: string;
}

export interface DefinitionInput {
  name: string;
  type: string;
  required?: boolean;
  maxLength?: number;
  sensitive?: boolean;
  [key: string]: unknown;
}

export interface DefinitionStep {
  id: string;
  type: string;
  config: Record<string, unknown>;
  timeoutMs?: number;
  sideEffect?: boolean;
  onError?: string;
  idempotency?: { key: string; guarantee: string };
  [key: string]: unknown;
}

export interface WorkflowDefinitionJson {
  schemaVersion: number;
  name: string;
  description?: string;
  timeoutSeconds?: number;
  parameters?: DefinitionParameter[];
  allowedHosts?: string[];
  secrets?: string[];
  inputs?: DefinitionInput[];
  reconciliation?: { evidence: string; matchKey: string; [key: string]: unknown };
  setup?: DefinitionStep[];
  reconcile?: DefinitionStep[];
  steps?: DefinitionStep[];
  [key: string]: unknown;
}

export type Section = 'setup' | 'reconcile' | 'steps';

export const Sections: Section[] = ['setup', 'steps', 'reconcile'];

export function parseDefinition(json: string): WorkflowDefinitionJson | null {
  try {
    const value: unknown = JSON.parse(json);
    return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as WorkflowDefinitionJson) : null;
  } catch {
    return null;
  }
}

/** A minimal starting point the Api accepts: one navigation step on a parameterized host. */
export function starterDefinition(name: string): WorkflowDefinitionJson {
  return {
    schemaVersion: 1,
    name,
    description: '',
    timeoutSeconds: 120,
    parameters: [{ name: 'baseUrl', type: 'url' }],
    allowedHosts: ['{{params.baseUrl}}'],
    secrets: [],
    inputs: [{ name: 'reference', type: 'string', required: true, maxLength: 100 }],
    setup: [],
    steps: [{ id: 'open', type: 'navigate', config: { url: '{{params.baseUrl}}/' } }],
  };
}
