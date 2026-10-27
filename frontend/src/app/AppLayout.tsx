import { useEffect, useRef } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router'
import { Button } from '../components/Button'
import { roleLabels } from '../lib/format'
import { useCurrentUser, useLogout } from '../features/auth/auth'
import { DemoBanner } from '../features/demo/DemoBanner'

const navLinkClass = ({ isActive }: { isActive: boolean }) =>
  `rounded-md px-3 py-2 text-sm font-medium ${isActive ? 'bg-navy-700 text-white' : 'text-navy-100 hover:bg-navy-800 hover:text-white'}`

export function AppLayout() {
  const { data: user } = useCurrentUser()
  const logout = useLogout()
  const navigate = useNavigate()
  const location = useLocation()
  const isFirstPage = useRef(true)

  // After moving to another page, screen-reader and keyboard users would otherwise stay on the link they
  // used (or on <body>). Move focus to the new page's heading, or to <main> while the page is loading.
  // Only the path counts: changing filters (?status=…) keeps focus where it is.
  useEffect(() => {
    if (isFirstPage.current) {
      isFirstPage.current = false
      return
    }
    const main = document.getElementById('main')
    const heading = main?.querySelector<HTMLElement>('h1')
    if (heading) heading.tabIndex = -1
    ;(heading ?? main)?.focus()
  }, [location.pathname])

  if (!user) return null

  const signOut = () => logout.mutate(undefined, { onSettled: () => navigate('/login', { replace: true }) })

  return (
    <div className="min-h-screen">
      <a href="#main" className="sr-only rounded-md bg-white px-4 py-2 font-semibold text-navy-900 focus:not-sr-only focus:absolute focus:top-3 focus:left-3 focus:z-50">
        Skip to main content
      </a>
      {user.demo && <DemoBanner expiresAt={user.demo.expiresAt} />}
      <header className="bg-navy-900 text-white">
        <div className="mx-auto flex max-w-6xl flex-col gap-3 px-4 py-3 sm:px-6 md:flex-row md:items-center md:justify-between">
          <div className="flex items-center justify-between gap-6">
            <NavLink to="/" className="text-lg font-bold tracking-tight">
              Harbour<span className="text-teal-300">Ops</span>
            </NavLink>
            <p className="text-right text-xs text-navy-200 md:hidden">{user.organization.name}</p>
          </div>
          <nav aria-label="Main" className="flex flex-wrap gap-1">
            {user.role === 'Manager' && (
              <NavLink to="/dashboard" className={navLinkClass}>
                Dashboard
              </NavLink>
            )}
            {user.role === 'Technician' && (
              <NavLink to="/assigned" className={navLinkClass}>
                Assigned to me
              </NavLink>
            )}
            <NavLink to="/work-orders" end className={navLinkClass}>
              {user.role === 'Manager' ? 'Work orders' : user.role === 'Technician' ? 'My jobs' : 'My requests'}
            </NavLink>
            {user.role !== 'Technician' && (
              <NavLink to="/work-orders/new" className={navLinkClass}>
                New request
              </NavLink>
            )}
          </nav>
          <div className="flex items-center justify-between gap-4 md:justify-end">
            <div className="text-sm leading-tight">
              <p className="font-semibold">{user.displayName}</p>
              <p className="text-navy-200">
                {roleLabels[user.role]}
                <span className="hidden md:inline"> · {user.organization.name}</span>
              </p>
            </div>
            <Button variant="secondary" onClick={signOut} busy={logout.isPending} className="min-h-9! px-3!">
              Sign out
            </Button>
          </div>
        </div>
      </header>
      <main id="main" tabIndex={-1} className="mx-auto max-w-6xl px-4 py-8 focus:outline-none sm:px-6">
        <Outlet />
      </main>
    </div>
  )
}
