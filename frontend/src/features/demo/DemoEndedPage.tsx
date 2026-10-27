import { useEffect, useRef } from 'react'
import { Link, Navigate, useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { Alert } from '../../components/Feedback'
import { usePageTitle } from '../../lib/usePageTitle'
import { useCurrentUser } from '../auth/auth'
import { demoErrorMessage, useStartDemo } from './demo'

/** Where a visitor lands when their demo has expired, was reset in another tab, or was ended. */
export function DemoEndedPage() {
  usePageTitle('Demo ended')
  const navigate = useNavigate()
  const { data: user } = useCurrentUser()
  const start = useStartDemo()
  const heading = useRef<HTMLHeadingElement>(null)

  // Arriving here is a change of context (often unexpected), so announce it by moving focus to the heading.
  useEffect(() => heading.current?.focus(), [])

  if (user) return <Navigate to="/" replace />

  return (
    <main id="main" className="flex min-h-screen items-start justify-center bg-slate-50 px-4 py-10 sm:items-center">
      <div className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-sm sm:p-8">
        <p className="text-sm font-semibold tracking-widest text-teal-800 uppercase">HarbourOps demo</p>
        <h1 ref={heading} tabIndex={-1} className="mt-2 text-2xl font-bold text-navy-900">
          Your demo session has ended
        </h1>
        <p className="mt-3 text-slate-700">
          A demo session lasts one hour, or until it is reset or ended. Its fictional records and photos are then deleted
          automatically, and nobody else ever saw them.
        </p>
        {start.isError && (
          <div className="mt-4">
            <Alert tone="error" title="The demo couldn't start">
              {demoErrorMessage(start.error)}
            </Alert>
          </div>
        )}
        <div className="mt-6 flex flex-wrap items-center gap-4">
          <Button busy={start.isPending} onClick={() => start.mutate(undefined, { onSuccess: () => navigate('/', { replace: true }) })}>
            Start a new demo
          </Button>
          <Link to="/login" className="font-medium text-teal-800 underline">
            Go to the sign-in page
          </Link>
        </div>
      </div>
    </main>
  )
}
