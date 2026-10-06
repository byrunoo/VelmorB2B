import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// Architecture boundaries (docs/architecture/dependency-rules.md):
//   A016 FRONTEND_CROSS_APP_IMPORT          admin <-> b2b never import each other; shared imports neither.
//   A017 FRONTEND_EXTERNAL_PROVIDER_BYPASS  browser code calls the Commercial API only, via the shared client.
const PROVIDER_PATTERN =
  '/(kumpara|businessos|logotiger|logogo|logoisbasi|logoerp|mikroerp|netsis|parasut|nebim|winnererp|sapbusinessone)/i';

const crossAppImports = (other) => ({
  patterns: [
    {
      group: [
        `@commercial/${other}`,
        `@commercial/${other}/**`,
        `**/${other}/src/**`,
        `../${other}/**`,
        `../../${other}/**`,
      ],
      message: `FRONTEND_CROSS_APP_IMPORT: move shared code to @commercial/shared instead of importing ${other}.`,
    },
  ],
});

const providerBypassSyntax = [
  {
    selector: 'Literal[value=/^https?:\\/\\//]',
    message:
      'FRONTEND_EXTERNAL_PROVIDER_BYPASS: no absolute URLs in browser code; use the shared Commercial API client.',
  },
  {
    selector: 'TemplateElement[value.raw=/https?:\\/\\//]',
    message:
      'FRONTEND_EXTERNAL_PROVIDER_BYPASS: no absolute URLs in browser code; use the shared Commercial API client.',
  },
  {
    selector: `Literal[value=${PROVIDER_PATTERN}]`,
    message:
      'FRONTEND_EXTERNAL_PROVIDER_BYPASS: external providers are reached only through the Commercial API.',
  },
];

const noDirectFetch = {
  'no-restricted-globals': [
    'error',
    { name: 'fetch', message: 'FRONTEND_EXTERNAL_PROVIDER_BYPASS: use apiRequest from @commercial/shared.' },
    {
      name: 'XMLHttpRequest',
      message: 'FRONTEND_EXTERNAL_PROVIDER_BYPASS: use apiRequest from @commercial/shared.',
    },
  ],
};

export default tseslint.config(
  { ignores: ['**/dist/**', '**/node_modules/**'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, ...tseslint.configs.recommended, reactHooks.configs.flat.recommended],
    languageOptions: { ecmaVersion: 2023, globals: globals.browser },
    plugins: { 'react-refresh': reactRefresh },
    rules: {
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      'no-restricted-syntax': ['error', ...providerBypassSyntax],
    },
  },
  {
    files: ['admin/src/**/*.{ts,tsx}'],
    rules: { 'no-restricted-imports': ['error', crossAppImports('b2b')], ...noDirectFetch },
  },
  {
    files: ['b2b/src/**/*.{ts,tsx}'],
    rules: { 'no-restricted-imports': ['error', crossAppImports('admin')], ...noDirectFetch },
  },
  {
    files: ['shared/src/**/*.{ts,tsx}'],
    ignores: ['shared/src/api/client.ts'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: [
                '@commercial/admin',
                '@commercial/admin/**',
                '@commercial/b2b',
                '@commercial/b2b/**',
                '**/admin/src/**',
                '**/b2b/src/**',
              ],
              message: 'FRONTEND_CROSS_APP_IMPORT: shared must not depend on an app.',
            },
          ],
        },
      ],
      ...noDirectFetch,
    },
  },
  {
    // Node-side build configuration (not shipped to browsers): absolute dev-proxy targets are allowed.
    files: ['*/vite.config.ts'],
    languageOptions: { globals: globals.node },
    rules: { 'no-restricted-syntax': 'off' },
  },
);
