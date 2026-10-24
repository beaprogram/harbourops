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
          <div className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm sm:p-8">
            <h2 className="text-xl font-bold text-navy-900">Sign in</h2>
            <form className="mt-6 space-y-4" onSubmit={onSubmit} noValidate>
              {login.isError && (
                <Alert tone="error" title="Sign-in failed">
                  {login.error instanceof ApiError ? login.error.message : 'Something went wrong. Please try again.'}
                </Alert>
              )}
              <FormField label="Email" error={errors.email?.message}>
                <input type="email" autoComplete="username" className={inputClass} {...register('email')} />
              </FormField>
              <FormField label="Password" error={errors.password?.message}>
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
