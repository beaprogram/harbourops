import type { ReactNode } from 'react'

type Tone = 'info' | 'success' | 'warning' | 'error'

const tones: Record<Tone, string> = {
  info: 'border-navy-200 bg-navy-50 text-navy-900',
  success: 'border-teal-200 bg-teal-50 text-teal-900',
  warning: 'border-amber-300 bg-amber-50 text-amber-900',
  error: 'border-red-200 bg-red-50 text-red-900',
}

interface AlertProps {
  tone: Tone
  title: string
  children?: ReactNode
  action?: ReactNode
}

/** Errors and warnings are announced to screen readers immediately (role="alert"). */
export function Alert({ tone, title, children, action }: AlertProps) {
  const urgent = tone === 'error' || tone === 'warning'
  return (
    <div role={urgent ? 'alert' : 'status'} className={`rounded-md border p-4 ${tones[tone]}`}>
      <p className="font-semibold">{title}</p>
      {children && <div className="mt-1 text-sm">{children}</div>}
      {action && <div className="mt-3">{action}</div>}
    </div>
  )
}

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  return (
    <div role="status" className="flex items-center gap-3 py-10 text-slate-600">
      <span aria-hidden="true" className="size-5 animate-spin rounded-full border-2 border-teal-600 border-t-transparent" />
      <span>{label}</span>
    </div>
  )
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed border-slate-300 bg-white px-6 py-12 text-center">
      <p className="font-semibold text-navy-900">{title}</p>
      {children && <div className="mt-2 text-sm text-slate-600">{children}</div>}
    </div>
  )
}
