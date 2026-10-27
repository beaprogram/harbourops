import type { ReactNode } from 'react'
import { Link } from 'react-router'
import type { UserRole } from '../../api/types'
import { usePageTitle } from '../../lib/usePageTitle'
import { useCurrentUser } from './auth'

/**
 * Shows a page only to one role. This is for clarity in the interface; the API enforces the same
 * rule independently (the dashboard and export endpoints return 403 to other roles).
 */
export function RequireRole({ role, children }: { role: UserRole; children: ReactNode }) {
  const { data: user } = useCurrentUser()
  const allowed = user?.role === role
  return allowed ? children : <NotAvailable role={role} />
}

function NotAvailable({ role }: { role: UserRole }) {
  usePageTitle('Not available')

  return (
    <div className="mx-auto max-w-lg py-10 text-center">
      <h1 className="text-2xl font-bold text-navy-900">Not available</h1>
      <p className="mt-2 text-slate-600">This page is only available to {role.toLowerCase()}s.</p>
      <Link to="/" className="mt-4 inline-block font-medium text-teal-800 underline">
        Go to your work orders
      </Link>
    </div>
  )
}
