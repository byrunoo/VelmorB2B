import type { z } from 'zod';
import { env } from '../config/env';
import { ErrorEnvelopeSchema, type ErrorEnvelope } from './schemas';

/** Thrown for every non-2xx response; carries the standard error envelope when the API returned one. */
export class ApiError extends Error {
  readonly status: number;
  readonly envelope: ErrorEnvelope | null;
  readonly correlationId: string;

  constructor(status: number, envelope: ErrorEnvelope | null, correlationId: string) {
    super(envelope?.detail ?? envelope?.title ?? `HTTP ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.envelope = envelope;
    this.correlationId = envelope?.correlationId ?? correlationId;
  }

  get code(): string {
    return this.envelope?.code ?? `HTTP_${this.status}`;
  }

  get fieldErrors(): Record<string, string[]> {
    return this.envelope?.errors ?? {};
  }
}

export function newCorrelationId(): string {
  return crypto.randomUUID().replaceAll('-', '');
}

type RequestOptions = {
  body?: unknown;
  signal?: AbortSignal;
  /** Accept non-2xx bodies that still match the schema (e.g. /health/ready returns 503 with a report). */
  acceptErrorStatusBody?: boolean;
};

/**
 * The ONLY HTTP entry point for browser code (A017). Talks exclusively to the Commercial API
 * (relative paths under the configured API origin), validates responses with Zod and attaches a
 * correlation ID to every request.
 */
export async function apiRequest<TSchema extends z.ZodType>(
  method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE',
  path: `/${string}`,
  schema: TSchema,
  options: RequestOptions = {},
): Promise<z.infer<TSchema>> {
  const correlationId = newCorrelationId();
  const headers: Record<string, string> = {
    Accept: 'application/json, application/problem+json',
    'X-Correlation-Id': correlationId,
  };
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  const response = await fetch(`${env.apiBaseUrl}${path}`, {
    method,
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
    credentials: 'same-origin',
  });

  const text = await response.text();
  const json: unknown = text.length > 0 ? safeJson(text) : null;

  if (!response.ok && !options.acceptErrorStatusBody) {
    const envelope = ErrorEnvelopeSchema.safeParse(json);
    throw new ApiError(response.status, envelope.success ? envelope.data : null, correlationId);
  }

  return schema.parse(json);
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}
