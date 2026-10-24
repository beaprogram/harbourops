import { Link, Navigate, type RouteObject } from 'react-router'
import { useCurrentUser } from '../features/auth/auth'
import { LoginPage } from '../features/auth/LoginPage'
import { RequireAuth } from '../features/auth/RequireAuth'
import { AssignedToMePage } from '../features/work-orders/AssignedToMePage'
import { CreateWorkOrderPage } from '../features/work-orders/CreateWorkOrderPage'
import { WorkOrderDetailPage } from '../features/work-orders/WorkOrderDetailPage'
import { WorkOrderListPage } from '../features/work-orders/WorkOrderListPage'
import { AppLayout } from './AppLayout'

/** Technicians land on their queue; everyone else on the work-order list. */
function HomeRedirect() {
  const { data: user } = useCurrentUser()
  return <Navigate to={user?.role === 'Technician' ? '/assigned' : '/work-orders'} replace />
}

function NotFoundPage() {
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
  {
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <HomeRedirect /> },
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
