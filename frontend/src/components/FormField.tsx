import { cloneElement, isValidElement, useId, type ReactElement } from 'react'

interface FormFieldProps {
  label: string
  error?: string | undefined
  hint?: string
  /** Announces the field as required (aria-required) without triggering browser validation popups. */
  required?: boolean
  /** A single input, select, or textarea. It receives id and ARIA attributes automatically. */
  children: ReactElement<Record<string, unknown>>
}

export const inputClass =
  'block w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 shadow-xs ' +
  'placeholder:text-slate-400 aria-invalid:border-red-600 disabled:bg-slate-100'

/** Label, hint, and error message wired to the control so screen readers announce them together. */
export function FormField({ label, error, hint, required, children }: FormFieldProps) {
  const id = useId()
  const hintId = hint ? `${id}-hint` : undefined
  const errorId = error ? `${id}-error` : undefined
  const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined

  const control = isValidElement(children)
    ? cloneElement(children, {
        id,
        'aria-invalid': error ? true : undefined,
        'aria-describedby': describedBy,
        'aria-required': required ? true : undefined,
      })
    : children

  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="block text-sm font-medium text-navy-900">
        {label}
      </label>
      {hint && (
        <p id={hintId} className="text-xs text-slate-600">
          {hint}
        </p>
      )}
      {control}
      {error && (
        <p id={errorId} className="text-sm font-medium text-red-700">
          {error}
        </p>
      )}
    </div>
  )
}
