import { zodResolver } from '@hookform/resolvers/zod'
import { useForm, useWatch } from 'react-hook-form'
import { Link, useNavigate } from 'react-router'
import { z } from 'zod'
import { ApiError } from '../../api/client'
import type { WorkOrderCategory } from '../../api/types'
import { Button } from '../../components/Button'
import { Alert, LoadingState } from '../../components/Feedback'
import { FormField, inputClass } from '../../components/FormField'
import { categoryLabels } from '../../lib/format'
import { useCreateWorkOrder, useProperties } from './workOrders'

const CATEGORIES = Object.keys(categoryLabels) as [WorkOrderCategory, ...WorkOrderCategory[]]

// Mirrors the server rules for fast feedback. The server validates again; it is the authority.
const createSchema = z.object({
  propertyId: z.string().min(1, 'Choose a property.'),
  category: z.enum(CATEGORIES, { error: 'Choose a category.' }),
  title: z.string().trim().min(1, 'Title is required.').max(120, 'Title must be 120 characters or fewer.'),
  description: z
    .string()
    .trim()
    .min(1, 'Description is required.')
    .max(4000, 'Description must be 4000 characters or fewer.'),
})

type CreateValues = z.input<typeof createSchema>
type CreateOutput = z.output<typeof createSchema>

const fieldNames = ['propertyId', 'category', 'title', 'description'] as const

export function CreateWorkOrderPage() {
  const navigate = useNavigate()
  const properties = useProperties()
  const createWorkOrder = useCreateWorkOrder()
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors },
  } = useForm<CreateValues, unknown, CreateOutput>({
    resolver: zodResolver(createSchema),
    defaultValues: { propertyId: '', title: '', description: '' },
  })

  const descriptionLength = useWatch({ control, name: 'description' })?.length ?? 0

  const onSubmit = handleSubmit((values) =>
    createWorkOrder.mutate(values, {
      onSuccess: (created) =>
        navigate(`/work-orders/${created.id}`, { state: { flash: 'Your request was submitted.' } }),
      onError: (error) => {
        // Show server-side validation messages next to the matching fields.
        if (error instanceof ApiError) {
          for (const field of fieldNames) {
            const message = error.fieldErrors[field]?.[0]
            if (message) setError(field, { type: 'server', message })
          }
        }
      },
    }),
  )

  const serverError = createWorkOrder.error
  const hasFieldErrors = serverError instanceof ApiError && Object.keys(serverError.fieldErrors).length > 0

  return (
    <div className="mx-auto max-w-2xl space-y-6">
      <div>
        <Link to="/work-orders" className="text-sm font-medium text-teal-800 underline">
          Back to work orders
        </Link>
        <h1 className="mt-2 text-2xl font-bold text-navy-900">New maintenance request</h1>
        <p className="mt-1 text-slate-600">Describe the problem so the maintenance team can plan the visit.</p>
      </div>

      {properties.isPending ? (
        <LoadingState label="Loading properties…" />
      ) : properties.isError ? (
        <Alert tone="error" title="Properties could not be loaded">
          {properties.error.message}
        </Alert>
      ) : (
        <form onSubmit={onSubmit} noValidate className="space-y-5 rounded-xl border border-slate-200 bg-white p-6 shadow-xs">
          {serverError && !hasFieldErrors && (
            <Alert tone="error" title="The request was not submitted">
              {serverError.message}
            </Alert>
          )}

          <div className="grid gap-5 sm:grid-cols-2">
            <FormField label="Property" error={errors.propertyId?.message}>
              <select className={inputClass} {...register('propertyId')}>
                <option value="">Choose a property</option>
                {properties.data.map((property) => (
                  <option key={property.id} value={property.id}>
                    {property.name} ({property.community})
                  </option>
                ))}
              </select>
            </FormField>
            <FormField label="Category" error={errors.category?.message}>
              <select className={inputClass} defaultValue="" {...register('category')}>
                <option value="" disabled>
                  Choose a category
                </option>
                {CATEGORIES.map((category) => (
                  <option key={category} value={category}>
                    {categoryLabels[category]}
                  </option>
                ))}
              </select>
            </FormField>
          </div>

          <FormField label="Title" hint="A short summary, for example “Kitchen sink draining slowly”." error={errors.title?.message}>
            <input type="text" maxLength={120} className={inputClass} {...register('title')} />
          </FormField>

          <FormField
            label="Description"
            hint={`Where is the problem, when did it start, and is anything at risk? (${descriptionLength}/4000)`}
            error={errors.description?.message}
          >
            <textarea rows={6} maxLength={4000} className={inputClass} {...register('description')} />
          </FormField>

          <div className="flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
            <Link
              to="/work-orders"
              className="inline-flex min-h-10 items-center justify-center rounded-md px-4 py-2 text-sm font-semibold text-navy-700 hover:bg-navy-50"
            >
              Cancel
            </Link>
            <Button type="submit" busy={createWorkOrder.isPending}>
              Submit request
            </Button>
          </div>
        </form>
      )}
    </div>
  )
}
