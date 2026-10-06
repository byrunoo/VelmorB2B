import { ShieldAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { env } from '../config/env';
import { Badge } from '../components/ui/Badge';

/**
 * Authentication shell PLACEHOLDER. Real identity (WORKFORCE / CUSTOMER principals, tenant/company/
 * customer-account scope) is the Phase 1 deliverable; until then every surface renders as an
 * unauthenticated development session. Nothing here grants access server-side.
 */
export function AuthShell({ children }: { principalType: 'WORKFORCE' | 'CUSTOMER'; children: ReactNode }) {
  // Phase 1: resolve the session, redirect to sign-in, expose the principal via context.
  return <>{children}</>;
}

export function DevSessionBadge({ principalType }: { principalType: 'WORKFORCE' | 'CUSTOMER' }) {
  if (env.appEnv === 'production') return null;
  return (
    <Badge tone="warning" className="hidden sm:inline-flex">
      <ShieldAlert className="h-3.5 w-3.5" aria-hidden />
      Dev oturumu · {principalType} · kimlik Phase 1
    </Badge>
  );
}
