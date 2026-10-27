import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiRequest } from '../../api/client'
import type { AttachmentItem, AttachmentList } from '../../api/types'

export const attachmentKeys = (workOrderId: string) => ['work-orders', 'attachments', workOrderId] as const

export function useAttachments(workOrderId: string) {
  return useQuery({
    queryKey: attachmentKeys(workOrderId),
    queryFn: () => apiRequest<AttachmentList>(`/api/work-orders/${workOrderId}/attachments`),
  })
}

export function useUploadAttachment(workOrderId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (file: File) => {
      const form = new FormData()
      form.append('file', file)
      return apiRequest<AttachmentItem>(`/api/work-orders/${workOrderId}/attachments`, { method: 'POST', body: form })
    },
    // Refetch rather than patch the cache: the server also recalculates whether more photos are allowed.
    onSettled: () => queryClient.invalidateQueries({ queryKey: attachmentKeys(workOrderId) }),
  })
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} bytes`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1).replace(/\.0$/, '')} MiB`
}
