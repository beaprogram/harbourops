import { Navigate, Outlet, useLocation } from 'react-router'
import { Alert, LoadingState } from '../../components/Feedback'
import { useDemoSession } from '../demo/demo'
import { useCurrentUser } from './auth'

/**
 * Sends signed-out visitors to the login page. This is a convenience for the interface only;
 * the API enforces authentication and permissions on every request.
 */
export function RequireAuth() {
  const location = useLocation()
  const { data: user, isPending, isError } = useCurrentUser()
  // Only needed once signed out: an ended demo gets its own screen instead of the sign-in page.
  const demo = useDemoSession({ enabled: user === null })

  if (isPending || (user === null && demo.isPending)) return <LoadingState label="Checking your session…" />
  if (isError) {
    return (
      <div className="mx-auto max-w-lg p-6">
        <Alert tone="error" title="HarbourOps is unavailable">
          We could not reach the server. Check your connection and refresh the page.
        </Alert>
      </div>
    )
  }
  if (!user && demo.data?.status === 'ended') return <Navigate to="/demo/ended" replace />
  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  return <Outlet />
}
