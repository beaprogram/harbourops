import type { ComponentProps } from 'react'

type Variant = 'primary' | 'secondary' | 'danger' | 'ghost'

const variants: Record<Variant, string> = {
  primary: 'bg-teal-700 text-white hover:bg-teal-800 disabled:bg-teal-700/60',
  secondary: 'border border-slate-300 bg-white text-navy-800 hover:bg-slate-50 disabled:text-slate-400',
  danger: 'bg-red-700 text-white hover:bg-red-800 disabled:bg-red-700/60',
  ghost: 'text-navy-700 hover:bg-navy-50 disabled:text-slate-400',
}

/** Includes `ref` (React 19 passes it as an ordinary prop), so callers can move focus to a button. */
interface ButtonProps extends ComponentProps<'button'> {
  variant?: Variant
  busy?: boolean
}

export function Button({ variant = 'primary', busy = false, disabled, className = '', children, ...props }: ButtonProps) {
  return (
    <button
      type="button"
      {...props}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      className={`inline-flex min-h-10 items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-semibold transition-colors disabled:cursor-not-allowed ${variants[variant]} ${className}`}
    >
      {busy && <span aria-hidden="true" className="size-4 animate-spin rounded-full border-2 border-current border-t-transparent" />}
      {children}
    </button>
  )
}
