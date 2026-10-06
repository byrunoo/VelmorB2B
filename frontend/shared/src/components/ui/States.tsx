import { AlertTriangle, Inbox, Loader2, RotateCcw } from 'lucide-react';
import type { ReactNode } from 'react';
import { Button } from './Button';

export function Spinner({ label = 'Yükleniyor…' }: { label?: string }) {
  return (
    <span className="inline-flex items-center gap-2 text-sm text-muted" role="status">
      <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
      {label}
    </span>
  );
}

export function EmptyState({
  title,
  description,
  icon,
}: {
  title: string;
  description?: ReactNode;
  icon?: ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 rounded-lg border border-dashed border-border-strong bg-surface px-6 py-12 text-center">
      <div className="flex h-10 w-10 items-center justify-center rounded-full bg-surface-2 text-muted">
        {icon ?? <Inbox className="h-5 w-5" aria-hidden />}
      </div>
      <h3 className="text-[15px] font-semibold text-fg">{title}</h3>
      {description ? <p className="max-w-md text-sm text-muted">{description}</p> : null}
    </div>
  );
}

export function ErrorState({
  title,
  description,
  correlationId,
  onRetry,
}: {
  title: string;
  description?: ReactNode;
  correlationId?: string;
  onRetry?: () => void;
}) {
  return (
    <div
      role="alert"
      className="flex items-start gap-3 rounded-lg border border-danger/30 bg-danger-soft p-4"
    >
      <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-danger" aria-hidden />
      <div className="min-w-0 flex-1 space-y-1">
        <p className="text-sm font-semibold text-fg">{title}</p>
        {description ? <p className="text-sm text-muted">{description}</p> : null}
        {correlationId ? (
          <p className="text-xs text-muted">
            Correlation ID: <span className="tabular select-all">{correlationId}</span>
          </p>
        ) : null}
      </div>
      {onRetry ? (
        <Button variant="outline" size="sm" onClick={onRetry}>
          <RotateCcw className="h-3.5 w-3.5" aria-hidden />
          Tekrar dene
        </Button>
      ) : null}
    </div>
  );
}
