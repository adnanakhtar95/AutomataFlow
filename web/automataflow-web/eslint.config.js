import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'node_modules', 'src/api/schema.d.ts', 'playwright-report', 'test-results'] },
  {
    files: ['**/*.{ts,tsx,js,mjs}'],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: { ecmaVersion: 2023, globals: { ...globals.browser, ...globals.node } },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // API text is rendered as text only (never as HTML).
      'no-restricted-syntax': ['error', { selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']", message: 'Never render API data as HTML.' }],
      // Tokens live in memory only (ADR-076): no web storage anywhere in the dashboard.
      'no-restricted-globals': ['error', { name: 'localStorage', message: 'Tokens and API data never go to web storage (ADR-076).' }, { name: 'sessionStorage', message: 'Tokens and API data never go to web storage (ADR-076).' }],
    },
  },
);
