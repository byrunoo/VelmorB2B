import { z } from 'zod';

/** Mirrors tests/Commercial.ContractTests/Schemas/error-envelope.schema.json. */
export const ErrorEnvelopeSchema = z.object({
  type: z.string(),
  title: z.string(),
  status: z.number().int(),
  code: z.string(),
  detail: z.string().nullish(),
  instance: z.string().nullish(),
  correlationId: z.string(),
  traceId: z.string().nullish(),
  errors: z.record(z.string(), z.array(z.string())).default({}),
});
export type ErrorEnvelope = z.infer<typeof ErrorEnvelopeSchema>;

export const VersionResponseSchema = z.object({
  service: z.string(),
  version: z.string(),
  environment: z.string(),
  runtime: z.string(),
});
export type VersionResponse = z.infer<typeof VersionResponseSchema>;

export const HealthReportSchema = z.object({
  status: z.enum(['Healthy', 'Degraded', 'Unhealthy']),
  totalDurationMs: z.number(),
  correlationId: z.string(),
  checks: z.array(
    z.object({
      name: z.string(),
      status: z.enum(['Healthy', 'Degraded', 'Unhealthy']),
      durationMs: z.number(),
      description: z.string().nullish(),
    }),
  ),
});
export type HealthReport = z.infer<typeof HealthReportSchema>;
export type HealthCheckEntry = HealthReport['checks'][number];

export const OutboxPingAcceptedSchema = z.object({
  messageId: z.string().uuid(),
  correlationId: z.string(),
});
export type OutboxPingAccepted = z.infer<typeof OutboxPingAcceptedSchema>;

export const OutboxPingStatusSchema = z.object({
  messageId: z.string().uuid(),
  outboxStatus: z.string(),
  attempts: z.number().int(),
  processedAtUtc: z.string().nullish(),
  handlerEffectRecorded: z.boolean(),
  correlationId: z.string(),
});
export type OutboxPingStatus = z.infer<typeof OutboxPingStatusSchema>;
