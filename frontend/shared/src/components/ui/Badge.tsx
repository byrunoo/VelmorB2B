import type { ReactNode } from 'react';
import { cn } from '../../lib/cn';

export type Tone = 'neutral' | 'brand' | 'success' | 'warning' | 'danger' | 'info';

const tones: Record<Tone, string> = {
  neutral: 'bg-surface-2 text-muted',
  brand: 'bg-brand-soft text-brand',
  success: 'bg-success-soft text-success',
  warning: 'bg-warning-soft text-warning',
  danger: 'bg-danger-soft text-danger',
  info: 'bg-info-soft text-info',
};

export function Badge({
  tone = 'neutral',
  children,
  className,
}: {
  tone?: Tone;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium whitespace-nowrap',
        tones[tone],
        className,
      )}
    >
      {children}
    </span>
  );
}

/** Status pill with a leading dot — shape + text, never colour alone. */
export function StatusPill({ tone, label }: { tone: Tone; label: string }) {
  return (
    <Badge tone={tone}>
      <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-current" />
      {label}
    </Badge>
  );
}
