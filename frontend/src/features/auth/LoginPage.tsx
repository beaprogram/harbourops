import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import { Navigate, useLocation, useNavigate } from 'react-router'
import { z } from 'zod'
import { ApiError } from '../../api/client'
import { Button } from '../../components/Button'
import { Alert } from '../../components/Feedback'
import { FormField, inputClass } from '../../components/FormField'
import { roleLabels } from '../../lib/format'
import { useCurrentUser, useDemoAccounts, useLogin } from './auth'
import { usePageTitle } from '../../lib/usePageTitle'
import { demoErrorMessage, useDemoSession, useStartDemo } from '../demo/demo'

const loginSchema = z.object({
  email: z.email('Enter a valid email address.'),
  password: z.string().min(1, 'Enter your password.'),
})

type LoginValues = z.infer<typeof loginSchema>

export function LoginPage() {
  const navigate = useNavigate()
  const location = useLocation()
  const { data: currentUser } = useCurrentUser()
  const login = useLogin()
  const demo = useDemoAccounts()
  const sandbox = useDemoSession()
  usePageTitle('Sign in')
  const {
    register,
    handleSubmit,
    setValue,
    formState: { errors },
  } = useForm<LoginValues>({ resolver: zodResolver(loginSchema), defaultValues: { email: '', password: '' } })

  const redirectTo = (location.state as { from?: string } | null)?.from ?? '/'
  if (currentUser) return <Navigate to={redirectTo} replace />

  const onSubmit = handleSubmit((values) =>
    login.mutate(values, { onSuccess: () => navigate(redirectTo, { replace: true }) }),
  )

  const fillDemoAccount = (email: string) => {
    setValue('email', email, { shouldValidate: true })
    setValue('password', demo.data?.password ?? '', { shouldValidate: true })
  }

  return (
    <main id="main" className="flex min-h-screen flex-col bg-navy-900 lg:flex-row">
      <section className="px-6 py-10 text-white lg:flex lg:w-5/12 lg:flex-col lg:justify-center lg:px-14">
        <p className="text-sm font-semibold tracking-widest text-teal-300 uppercase">HarbourOps</p>
        <h1 className="mt-3 text-3xl font-bold lg:text-4xl">Maintenance requests, dispatched clearly.</h1>
        <p className="mt-4 max-w-md text-navy-100">
          Residents report problems, managers prioritize and assign, technicians resolve, and every step is recorded.
        </p>
      </section>

      <section className="flex flex-1 items-start justify-center bg-slate-50 px-4 py-10 lg:items-center lg:rounded-l-3xl">
        <div className="w-full max-w-md space-y-6">
          {sandbox.data && (
            <TryDemoCard
              active={sandbox.data.status === 'active'}
              lifetimeMinutes={sandbox.data.lifetimeMinutes}
              onStarted={() => navigate('/', { replace: true })}
            />
          )}

          <div className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm sm:p-8">
            <h2 className="text-xl font-bold text-navy-900">Sign in</h2>
            {sandbox.data && !demo.data && (
              <p className="mt-2 text-sm text-slate-600">This public demo has no sign-in accounts. Use “Try the demo” instead.</p>
            )}
            <form className="mt-6 space-y-4" onSubmit={onSubmit} noValidate>
              {login.isError && (
                <Alert tone="error" title="Sign-in failed">
                  {login.error instanceof ApiError ? login.error.message : 'Something went wrong. Please try again.'}
                </Alert>
              )}
              <FormField required label="Email" error={errors.email?.message}>
                <input type="email" autoComplete="username" className={inputClass} {...register('email')} />
              </FormField>
              <FormField required label="Password" error={errors.password?.message}>
                <input type="password" autoComplete="current-password" className={inputClass} {...register('password')} />
              </FormField>
              <Button type="submit" busy={login.isPending} className="w-full">
                Sign in
              </Button>
            </form>
          </div>

          {demo.data && (
            <section aria-labelledby="demo-heading" className="rounded-xl border border-amber-300 bg-amber-50 p-5">
              <h2 id="demo-heading" className="font-semibold text-amber-950">
                Demo accounts (fictional data)
              </h2>
              <p className="mt-1 text-sm text-amber-900">
                Every demo account uses the demo-only password{' '}
                <code className="rounded bg-white px-1.5 py-0.5 font-mono text-xs">{demo.data.password}</code>. Choose
                one to fill in the form.
              </p>
              <ul className="mt-3 grid gap-2">
                {demo.data.accounts.map((account) => (
                  <li key={account.email}>
                    <button
                      type="button"
                      onClick={() => fillDemoAccount(account.email)}
                      className="w-full rounded-md border border-amber-200 bg-white px-3 py-2 text-left text-sm hover:border-teal-600"
                    >
                      <span className="font-medium text-navy-900">{account.displayName}</span>
                      <span className="text-slate-600">
                        {' '}
                        · {roleLabels[account.role]} · {account.organizationName}
                      </span>
                      <span className="block font-mono text-xs text-slate-600">{account.email}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </div>
      </section>
    </main>
  )
}

/** The entry point to a private, temporary demo sandbox (shown only when the server has demo mode on). */
function TryDemoCard({ active, lifetimeMinutes, onStarted }: { active: boolean; lifetimeMinutes: number; onStarted: () => void }) {
  const start = useStartDemo()
  return (
    <section aria-labelledby="try-demo-heading" className="rounded-xl border border-teal-300 bg-white p-6 shadow-sm sm:p-8">
      <h2 id="try-demo-heading" className="text-xl font-bold text-navy-900">
        {active ? 'Continue your demo' : 'Try the demo'}
      </h2>
      <p className="mt-2 text-sm text-slate-700">
        Get your own private copy of two fictional property teams. Switch between a manager, a technician, and a
        resident to follow a request from report to repair. Other visitors never see your changes. The session lasts{' '}
        {lifetimeMinutes} minutes, then it and its data are deleted.
      </p>
      {start.isError && (
        <div className="mt-4">
          <Alert tone="error" title="The demo couldn't start">
            {demoErrorMessage(start.error)}
          </Alert>
        </div>
      )}
      <Button className="mt-4 w-full" busy={start.isPending} onClick={() => start.mutate(undefined, { onSuccess: onStarted })}>
        {active ? 'Continue the demo' : 'Try the demo'}
      </Button>
    </section>
  )
}
