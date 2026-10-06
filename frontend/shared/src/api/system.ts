import { useMutation, useQuery } from '@tanstack/react-query';
import { apiRequest } from './client';
import {
  HealthReportSchema,
  OutboxPingAcceptedSchema,
  OutboxPingStatusSchema,
  VersionResponseSchema,
} from './schemas';

export const systemKeys = {
  version: ['system', 'version'] as const,
  health: (kind: 'live' | 'ready') => ['system', 'health', kind] as const,
  ping: (id: string) => ['diagnostics', 'outbox-ping', id] as const,
};

export function useApiVersion() {
  return useQuery({
    queryKey: systemKeys.version,
    queryFn: ({ signal }) => apiRequest('GET', '/api/v1/system/version', VersionResponseSchema, { signal }),
  });
}

export function useHealth(kind: 'live' | 'ready') {
  return useQuery({
    queryKey: systemKeys.health(kind),
    queryFn: ({ signal }) =>
      apiRequest('GET', `/health/${kind}`, HealthReportSchema, { signal, acceptErrorStatusBody: true }),
    refetchInterval: 15_000,
  });
}

export function useCreateOutboxPing() {
  return useMutation({
    mutationFn: (input: { note?: string }) =>
      apiRequest('POST', '/api/v1/diagnostics/outbox-pings', OutboxPingAcceptedSchema, { body: input }),
  });
}

export function useOutboxPingStatus(messageId: string | null) {
  return useQuery({
    queryKey: systemKeys.ping(messageId ?? 'none'),
    enabled: messageId !== null,
    queryFn: ({ signal }) =>
      apiRequest('GET', `/api/v1/diagnostics/outbox-pings/${messageId}`, OutboxPingStatusSchema, { signal }),
    refetchInterval: (query) => (query.state.data?.outboxStatus === 'Processed' ? false : 1_000),
  });
}
