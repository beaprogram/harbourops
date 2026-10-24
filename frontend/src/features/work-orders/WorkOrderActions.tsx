import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { ApiError } from '../../api/client'
import type { WorkOrderDetail, WorkOrderPriority } from '../../api/types'
import { Button } from '../../components/Button'
import { Alert } from '../../components/Feedback'
import { FormField, inputClass } from '../../components/FormField'
import { useTechnicians, useWorkOrderCommand, workOrderKeys, type WorkOrderCommand } from './workOrders'

/**
 * Shows only the actions the server says this user may take right now (allowedActions).
 * Hiding a button is a usability feature, not security: the API checks every command again.
 */
export function WorkOrderActions({ workOrder }: { workOrder: WorkOrderDetail }) {
  const queryClient = useQueryClient()
  const command = useWorkOrderCommand(workOrder)
  const [lastSuccess, setLastSuccess] = useState<string | null>(null)
  const allowed = new Set(workOrder.allowedActions)

  const run = (input: WorkOrderCommand, successMessage: string, onSuccess?: () => void) => {
    setLastSuccess(null)
    command.mutate(input, {
      onSuccess: () => {
        setLastSuccess(successMessage)
        onSuccess?.()
      },
    })
  }

  const loadLatest = async () => {
    command.reset()
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: workOrderKeys.detail(workOrder.id) }),
      queryClient.invalidateQueries({ queryKey: workOrderKeys.activity(workOrder.id) }),
    ])
  }

  const error = command.error
  const busy = command.isPending

  return (
    <section aria-labelledby="actions-heading" className="space-y-4 rounded-xl border border-slate-200 bg-white p-6 shadow-xs">
      <h2 id="actions-heading" className="text-lg font-semibold text-navy-900">
        Actions
      </h2>

      {error instanceof ApiError && error.isStale ? (
        <Alert
          tone="warning"
          title="This work order changed while you were viewing it"
          action={<Button variant="secondary" onClick={() => void loadLatest()}>Load latest version</Button>}
        >
          Someone else saved a change first, so yours was not applied. Load the latest version, check it, then try
          again. Anything you typed is kept.
        </Alert>
      ) : error ? (
        <Alert tone="error" title="That action could not be completed">
          {error.message}
        </Alert>
      ) : null}

      {lastSuccess && !error && <p role="status" className="text-sm font-medium text-teal-800">{lastSuccess}</p>}

      {allowed.size === 0 && (
        <p className="text-sm text-slate-600">
          {workOrder.status === 'Closed'
            ? 'This work order is closed and can no longer be changed.'
            : 'There are no actions available to you at this stage.'}
        </p>
      )}

      {allowed.has('Start') && (
        <ActionBlock description="Let everyone know you are working on this job.">
          <Button busy={busy} onClick={() => run({ action: 'start' }, 'Work started.')}>
            Start work
          </Button>
        </ActionBlock>
      )}

      {allowed.has('Resolve') && (
        <NoteForm
          label="Resolution note"
          hint="What did you do? The requester and manager will see this."
          submitLabel="Mark resolved"
          busy={busy}
          onSubmit={(note, reset) => run({ action: 'resolve', resolutionNote: note }, 'Marked resolved.', reset)}
        />
      )}

      {allowed.has('SetPriority') && (
        <PriorityForm
          current={workOrder.priority}
          busy={busy}
          onSubmit={(priority) => run({ action: 'priority', priority }, 'Priority updated.')}
        />
      )}

      {allowed.has('Assign') && (
        <AssignForm
          currentTechnicianId={workOrder.assignedTechnician?.id ?? null}
          busy={busy}
          onSubmit={(technicianId) =>
            run({ action: 'assign', technicianId }, workOrder.assignedTechnician ? 'Technician reassigned.' : 'Technician assigned.')
          }
        />
      )}

      {allowed.has('Close') && (
        <ActionBlock description="Confirm the work is complete. Closed work orders become read-only.">
          <Button busy={busy} onClick={() => run({ action: 'close' }, 'Work order closed.')}>
            Close work order
          </Button>
        </ActionBlock>
      )}

      {allowed.has('Reopen') && (
        <NoteForm
          label="Reason for reopening"
          hint="Tell the technician what is still wrong."
          submitLabel="Reopen request"
          busy={busy}
          variant="secondary"
          onSubmit={(reason, reset) => run({ action: 'reopen', reason }, 'Request reopened.', reset)}
        />
      )}
    </section>
  )
}

function ActionBlock({ description, children }: { description: string; children: ReactNode }) {
  return (
    <div className="space-y-2 border-t border-slate-100 pt-4">
      <p className="text-sm text-slate-600">{description}</p>
      {children}
    </div>
  )
}

const noteSchema = z.object({
  note: z.string().trim().min(1, 'This field is required.').max(2000, 'Keep it to 2000 characters or fewer.'),
})

function NoteForm(props: {
  label: string
  hint: string
  submitLabel: string
  busy: boolean
  variant?: 'primary' | 'secondary'
  onSubmit: (note: string, reset: () => void) => void
}) {
  const { register, handleSubmit, reset, formState: { errors } } = useForm<z.infer<typeof noteSchema>>({
    resolver: zodResolver(noteSchema),
    defaultValues: { note: '' },
  })
  return (
    <form noValidate className="space-y-3 border-t border-slate-100 pt-4" onSubmit={handleSubmit(({ note }) => props.onSubmit(note, () => reset()))}>
      <FormField label={props.label} hint={props.hint} error={errors.note?.message}>
        <textarea rows={4} maxLength={2000} className={inputClass} {...register('note')} />
      </FormField>
      <Button type="submit" variant={props.variant ?? 'primary'} busy={props.busy}>
        {props.submitLabel}
      </Button>
    </form>
  )
}

function PriorityForm({ current, busy, onSubmit }: { current: WorkOrderPriority; busy: boolean; onSubmit: (priority: WorkOrderPriority) => void }) {
  const [priority, setPriority] = useState<WorkOrderPriority>(current)
  return (
    <form
      className="space-y-3 border-t border-slate-100 pt-4"
      onSubmit={(event) => {
        event.preventDefault()
        onSubmit(priority)
      }}
    >
      <FormField label="Priority" hint="High: due 24 hours after submission. Normal: 72 hours.">
        <select className={inputClass} value={priority} onChange={(e) => setPriority(e.target.value as WorkOrderPriority)}>
          <option value="Normal">Normal</option>
          <option value="High">High</option>
        </select>
      </FormField>
      <Button type="submit" variant="secondary" busy={busy} disabled={priority === current}>
        Update priority
      </Button>
    </form>
  )
}

function AssignForm({ currentTechnicianId, busy, onSubmit }: { currentTechnicianId: string | null; busy: boolean; onSubmit: (technicianId: string) => void }) {
  const technicians = useTechnicians(true)
  const [technicianId, setTechnicianId] = useState('')
  const [error, setError] = useState<string>()

  return (
    <form
      noValidate
      className="space-y-3 border-t border-slate-100 pt-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (!technicianId) {
          setError('Choose a technician.')
          return
        }
        setError(undefined)
        onSubmit(technicianId)
      }}
    >
      <FormField label={currentTechnicianId ? 'Reassign to' : 'Assign to'} error={error ?? (technicians.isError ? 'Technicians could not be loaded.' : undefined)}>
        <select className={inputClass} value={technicianId} onChange={(e) => setTechnicianId(e.target.value)} disabled={technicians.isPending}>
          <option value="">{technicians.isPending ? 'Loading technicians…' : 'Choose a technician'}</option>
          {technicians.data
            ?.filter((t) => t.id !== currentTechnicianId)
            .map((t) => (
              <option key={t.id} value={t.id}>
                {t.displayName} ({t.openAssignments} open)
              </option>
            ))}
        </select>
      </FormField>
      <Button type="submit" busy={busy}>
        {currentTechnicianId ? 'Reassign technician' : 'Assign technician'}
      </Button>
    </form>
  )
}
