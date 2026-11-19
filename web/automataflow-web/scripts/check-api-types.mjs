// Fails when src/api/schema.d.ts is stale against the committed OpenAPI snapshot (ADR-076).
// The snapshot itself is guarded by the Api's OpenApiContractTests; together they keep the dashboard on the served contract.
import { readFile } from 'node:fs/promises';
import openapiTS, { astToString } from 'openapi-typescript';

const snapshot = new URL('../openapi/automataflow-api.json', import.meta.url);
const committed = new URL('../src/api/schema.d.ts', import.meta.url);

const generated = astToString(await openapiTS(JSON.parse(await readFile(snapshot, 'utf8'))));
const normalize = (text) => text.replace(/\r\n/g, '\n').replace(/^\/\*\*[\s\S]*?\*\/\n/, '').trim();
const current = await readFile(committed, 'utf8');

if (normalize(generated) !== normalize(current)) {
  console.error('src/api/schema.d.ts is stale: run "npm run generate:api" (or scripts/update-openapi.ps1) and commit the result.');
  process.exit(1);
}

console.log('API types match the OpenAPI snapshot.');
