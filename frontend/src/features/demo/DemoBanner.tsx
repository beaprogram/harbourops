import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import type { DemoPersonaKey } from '../../api/types'
import { Button } from '../../components/Button'
import { DISPLAY_TIME_ZONE, formatDuration, roleLabels } from '../../lib/format'
import { currentUserKey } from '../auth/auth'
import { demoErrorMessage, useDemoSession, useEndDemo, useResetDemo, useSwitchPersona } from './demo'

const endTimeFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: DISPLAY_TIME_ZONE,
  hour: 'numeric',
  minute: '2-digit',
  timeZoneName: 'short',
})

function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(timer)
  }, [intervalMs])
  return now
}

/**
 * Shown on every page while a visitor is acting as a demo persona: what this is, when it ends, and the
 * controls to switch person, reset the data, or end the demo. The server enforces all of it; this is only
 * the interface to it.
 */
export function DemoBanner({ expiresAt }: { expiresAt: string }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const session = useDemoSession()
  const switchPersona = useSwitchPersona()
  const reset = useResetDemo()
  const end = useEndDemo()
  // The person picked in the list, remembered only while the current person stays the same (a switch made
  // here or in another tab resets it).
  const [picked, setPicked] = useState<{ key: DemoPersonaKey; whileCurrent: DemoPersonaKey | null } | null>(null)
  const [confirmingReset, setConfirmingReset] = useState(false)
  const [status, setStatus] = useState('')
  const confirmButton = useRef<HTMLButtonElement>(null)
  const resetButton = useRef<HTMLButtonElement>(null)
  const wasConfirming = useRef(false)
  const now = useNow(30_000)
  const selectId = useId()
  const aboutId = useId()
  const personaHintId = useId()

  const details = session.data?.session
  const current = details?.currentPersona ?? null
  const choice: DemoPersonaKey | '' = picked && picked.whileCurrent === current ? picked.key : (current ?? '')
  const selected = details?.personas.find((p) => p.key === choice)

  // Keyboard focus follows the two-step reset: onto "Reset now" when asked, back to "Reset my demo" on cancel.
  useEffect(() => {
    if (confirmingReset) confirmButton.current?.focus()
    else if (wasConfirming.current) resetButton.current?.focus()
    wasConfirming.current = confirmingReset
  }, [confirmingReset])

  const remainingMs = new Date(expiresAt).getTime() - now
  useEffect(() => {
    // Time is up: ask the server again. It answers 401 "demo_expired" and the ended screen appears.
    if (remainingMs <= 0) void queryClient.invalidateQueries({ queryKey: currentUserKey })
  }, [remainingMs, queryClient])

  const error = switchPersona.error ?? reset.error ?? end.error

  const switchTo = () => {
    if (!choice || choice === current) return
    switchPersona.mutate(choice, {
      onSuccess: (result) => {
        setStatus(`You are now ${result.user.displayName}, ${roleLabels[result.user.role]} at ${result.user.organization.name}.`)
        navigate('/')
      },
    })
  }

  const resetNow = () =>
    reset.mutate(undefined, {
      onSuccess: () => {
        setConfirmingReset(false)
        setStatus('Your demo has fresh data.')
        navigate('/')
      },
    })

  const endNow = () => end.mutate(undefined, { onSettled: () => navigate('/demo/ended', { replace: true }) })

  return (
    <section aria-label="Demo session" className="border-b border-amber-300 bg-amber-50 text-amber-950">
      <div className="mx-auto flex max-w-6xl flex-col gap-3 px-4 py-3 sm:px-6 lg:flex-row lg:items-end lg:justify-between">
        <div className="text-sm">
          <p className="font-semibold">Portfolio demo · fictional data · temporary session.</p>
          <p id={aboutId} className="mt-0.5">
            {remainingMs > 0
              ? `Ends at ${endTimeFormatter.format(new Date(expiresAt))} (in ${formatDuration(remainingMs)}).`
              : 'This demo session has ended.'}{' '}
            Switching person applies to every tab in this browser.
          </p>
        </div>

        <div className="flex flex-wrap items-end gap-2">
          {details && (
            <>
              <div className="min-w-0">
                <label htmlFor={selectId} className="block text-xs font-semibold">
                  Act as
                </label>
                <select
                  id={selectId}
                  value={choice}
                  onChange={(event) => setPicked({ key: event.target.value as DemoPersonaKey, whileCurrent: current })}
                  aria-describedby={`${personaHintId} ${aboutId}`}
                  className="mt-1 max-w-full rounded-md border border-amber-400 bg-white px-2 py-2 text-sm text-navy-900"
                >
                  {details.personas.map((persona) => (
                    <option key={persona.key} value={persona.key}>
                      {persona.displayName} ({roleLabels[persona.role]}, {persona.organizationName})
                    </option>
                  ))}
                </select>
              </div>
              <Button variant="secondary" onClick={switchTo} busy={switchPersona.isPending} disabled={!choice || choice === current}>
                Switch
              </Button>
            </>
          )}
          {confirmingReset ? (
            <>
              <Button ref={confirmButton} variant="danger" onClick={resetNow} busy={reset.isPending}>
                Reset now
              </Button>
              <Button variant="ghost" onClick={() => setConfirmingReset(false)} disabled={reset.isPending}>
                Cancel
              </Button>
            </>
          ) : (
            <Button ref={resetButton} variant="secondary" onClick={() => setConfirmingReset(true)}>
              Reset my demo
            </Button>
          )}
          <Button variant="ghost" onClick={endNow} busy={end.isPending}>
            End demo
          </Button>
        </div>
      </div>
      <div className="mx-auto max-w-6xl px-4 pb-3 text-xs sm:px-6">
        <p id={personaHintId}>{selected?.description}</p>
        {confirmingReset && <p className="mt-1 font-semibold">Reset replaces all of your demo data with a fresh copy. Other visitors are not affected.</p>}
        <p role="status" className="mt-1 font-semibold empty:hidden">
          {status}
        </p>
        {error && (
          <p role="alert" className="mt-1 font-semibold text-red-800">
            {demoErrorMessage(error)}
          </p>
        )}
      </div>
    </section>
  )
}
