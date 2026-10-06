import { z } from 'zod';

/**
 * Browser-visible configuration. Only the Commercial API origin may be configured here — the
 * browser never talks to an ERP or other external provider directly (A017). Never put secrets in
 * VITE_* variables: they are embedded in the bundle.
 */
const EnvSchema = z.object({
  VITE_API_BASE_URL: z
    .string()
    .optional()
    .transform((v) => (v ?? '').replace(/\/+$/, '')),
  VITE_APP_ENV: z.enum(['development', 'test', 'staging', 'production']).default('development'),
});

export type AppEnv = {
  apiBaseUrl: string;
  appEnv: z.infer<typeof EnvSchema>['VITE_APP_ENV'];
  isDevelopment: boolean;
};

export function readEnv(source: Record<string, unknown> = import.meta.env): AppEnv {
  const parsed = EnvSchema.parse(source);
  return {
    apiBaseUrl: parsed.VITE_API_BASE_URL,
    appEnv: parsed.VITE_APP_ENV,
    isDevelopment: parsed.VITE_APP_ENV === 'development',
  };
}

export const env: AppEnv = readEnv();
