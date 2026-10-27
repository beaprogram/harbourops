import { useInfiniteQuery, useMutation, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { ApiError, apiRequest } from '../../api/client'
import type { CommentItem, CommentPage } from '../../api/types'
import { workOrderKeys } from './workOrders'

/** Must match Comment.BodyMaxLength on the server. */
export const COMMENT_MAX_LENGTH = 2000
const PAGE_SIZE = 20

export const commentKeys = (workOrderId: string) => ['work-orders', 'comments', workOrderId] as const

/**
 * The newest page loads first; "Show earlier comments" fetches the page before the oldest comment
 * shown (keyset cursor), so new comments never shift or duplicate what is already on screen.
 */
export function useComments(workOrderId: string) {
  return useInfiniteQuery({
    queryKey: commentKeys(workOrderId),
    queryFn: ({ pageParam }) =>
      apiRequest<CommentPage>(
        `/api/work-orders/${workOrderId}/comments?limit=${PAGE_SIZE}${pageParam ? `&before=${pageParam}` : ''}`,
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => (lastPage.hasOlder ? (lastPage.items[0]?.id ?? null) : null),
  })
}

/** All loaded comments in chronological order. Pages arrive newest-first, so reverse them. */
export function flattenComments(data: InfiniteData<CommentPage> | undefined): CommentItem[] {
  return data ? [...data.pages].reverse().flatMap((page) => page.items) : []
}

export function useAddComment(workOrderId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: string) =>
      apiRequest<CommentItem>(`/api/work-orders/${workOrderId}/comments`, { method: 'POST', body: { body } }),
    onSuccess: (comment) => {
      // Show the new comment immediately by appending it to the newest page.
      queryClient.setQueryData<InfiniteData<CommentPage>>(commentKeys(workOrderId), (old) =>
        old
          ? {
              ...old,
              pages: old.pages.map((page, index) => ({
                ...page,
                items: index === 0 ? [...page.items, comment] : page.items,
                totalCount: page.totalCount + 1,
              })),
            }
          : old,
      )
    },
    onError: (error) => {
      // Someone closed the work order meanwhile: refresh it so the page shows the closed state.
      if (error instanceof ApiError && error.code === 'work_order_closed') {
        void queryClient.invalidateQueries({ queryKey: workOrderKeys.detail(workOrderId) })
      }
    },
  })
}
