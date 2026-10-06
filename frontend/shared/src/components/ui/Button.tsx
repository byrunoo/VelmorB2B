import { forwardRef, type ButtonHTMLAttributes } from 'react';
import { cn } from '../../lib/cn';

type Variant = 'primary' | 'secondary' | 'outline' | 'ghost' | 'danger';
type Size = 'sm' | 'md' | 'lg' | 'icon';

const variants: Record<Variant, string> = {
  primary: 'bg-brand text-brand-fg hover:brightness-110 shadow-xs',
  secondary: 'bg-surface-2 text-fg hover:bg-border',
  outline: 'border border-border bg-surface text-fg hover:bg-surface-2',
  ghost: 'text-muted hover:bg-surface-2 hover:text-fg',
  danger: 'bg-danger text-white hover:brightness-110',
};

const sizes: Record<Size, string> = {
  sm: 'h-8 px-3 text-[13px]',
  md: 'h-9 px-3.5 text-sm',
  lg: 'h-11 px-5 text-sm',
  icon: 'h-9 w-9',
};

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; size?: Size };

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { className, variant = 'primary', size = 'md', type = 'button', ...props },
  ref,
) {
  return (
    <button
      ref={ref}
      type={type}
      className={cn(
        'inline-flex shrink-0 items-center justify-center gap-2 rounded-md font-medium transition-colors',
        'disabled:pointer-events-none disabled:opacity-50',
        variants[variant],
        sizes[size],
        className,
      )}
      {...props}
    />
  );
});
