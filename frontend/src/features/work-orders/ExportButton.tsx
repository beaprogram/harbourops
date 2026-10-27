import { useMutation } from '@tanstack/react-query'
import { useId } from 'react'
import { apiDownload } from '../../api/client'
import { Button } from '../../components/Button'
import { Alert } from '../../components/Feedback'
import { toSearchParams, type WorkOrderListFilters } from './listFilters'

interface ExportButtonProps {
  filters: WorkOrderListFilters
  /** How many work orders match the current filters (from the list response). */
  matchingCount: number
  /** The server's safety limit; exports above it are refused, never truncated. */
  rowLimit: number
}

/** Manager-only. Exports every work order matching the current search and filters, not just this page. */
export function ExportButton({ filters, matchingCount, rowLimit }: ExportButtonProps) {
  const download = useMutation({
    mutationFn: async () => {
      const query = toSearchParams({ ...filters, page: 1 }).toString()
      const { blob, fileName } = await apiDownload(`/api/work-orders/export${query ? `?${query}` : ''}`)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = fileName ?? 'harbourops-work-orders.csv'
      document.body.append(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(url)
    },
  })

  const descriptionId = useId()
  const tooMany = matchingCount > rowLimit
  const limitText = rowLimit.toLocaleString('en-CA')

  return (
    <div className="space-y-2">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-end">
        <p id={descriptionId} className="text-sm text-slate-600 sm:text-right">
          {tooMany
            ? `${matchingCount.toLocaleString('en-CA')} work orders match. Narrow the filters to ${limitText} or fewer to export.`
            : matchingCount === 1
              ? `Exports the 1 matching work order (limit ${limitText}).`
              : `Exports all ${matchingCount.toLocaleString('en-CA')} matching work orders (limit ${limitText}).`}
        </p>
        <Button
          variant="secondary"
          busy={download.isPending}
          disabled={tooMany || matchingCount === 0}
          aria-describedby={descriptionId}
          onClick={() => download.mutate()}
        >
          {download.isPending ? 'Preparing CSV…' : 'Export CSV'}
        </Button>
      </div>
      <p role="status" className="sr-only">
        {download.isSuccess ? 'Export downloaded.' : ''}
      </p>
      {download.isError && (
        <Alert tone="error" title="The export failed">
          {download.error.message}
        </Alert>
      )}
    </div>
  )
}
