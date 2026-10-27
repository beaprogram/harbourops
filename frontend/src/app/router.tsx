import { Link, Navigate, type RouteObject } from 'react-router'
import { useCurrentUser } from '../features/auth/auth'
import { DashboardPage } from '../features/dashboard/DashboardPage'
import { DemoEndedPage } from '../features/demo/DemoEndedPage'
import { LoginPage } from '../features/auth/LoginPage'
import { RequireAuth } from '../features/auth/RequireAuth'
import { RequireRole } from '../features/auth/RequireRole'
import { AssignedToMePage } from '../features/work-orders/AssignedToMePage'
import { CreateWorkOrderPage } from '../features/work-orders/CreateWorkOrderPage'
import { WorkOrderDetailPage } from '../features/work-orders/WorkOrderDetailPage'
import { WorkOrderListPage } from '../features/work-orders/WorkOrderListPage'
import { AppLayout } from './AppLayout'
import { usePageTitle } from '../lib/usePageTitle'

/** Managers land on the dashboard, technicians on their queue, requesters on their requests. */
function HomeRedirect() {
  const { data: user } = useCurrentUser()
  const home = user?.role === 'Manager' ? '/dashboard' : user?.role === 'Technician' ? '/assigned' : '/work-orders'
  return <Navigate to={home} replace />
}

function NotFoundPage() {
  usePageTitle('Page not found')
  return (
    <div className="mx-auto max-w-lg py-10 text-center">
      <h1 className="text-2xl font-bold text-navy-900">Page not found</h1>
      <p className="mt-2 text-slate-600">The page you are looking for does not exist.</p>
      <Link to="/" className="mt-4 inline-block font-medium text-teal-800 underline">
        Go to your work orders
      </Link>
    </div>
  )
}

export const routes: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  { path: '/demo/ended', element: <DemoEndedPage /> },
  {
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <HomeRedirect /> },
          {
            path: 'dashboard',
            element: (
              <RequireRole role="Manager">
                <DashboardPage />
              </RequireRole>
            ),
          },
          { path: 'work-orders', element: <WorkOrderListPage /> },
          { path: 'work-orders/new', element: <CreateWorkOrderPage /> },
          { path: 'work-orders/:id', element: <WorkOrderDetailPage /> },
          { path: 'assigned', element: <AssignedToMePage /> },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
]
