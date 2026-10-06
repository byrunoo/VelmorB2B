import { QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/client';

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        refetchOnWindowFocus: false,
        // Client errors (4xx) are deterministic — retrying them only adds noise.
        retry: (failureCount, error) =>
          !(error instanceof ApiError && error.status >= 400 && error.status < 500) && failureCount < 1,
      },
      mutations: { retry: false },
    },
  });
}
