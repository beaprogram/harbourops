import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'
import type { WorkOrderDetail } from '../../api/types'
import { Button } from '../../components/Button'
import { Alert, LoadingState } from '../../components/Feedback'
import { FormField, inputClass } from '../../components/FormField'
import { formatDateTime, roleLabels } from '../../lib/format'
import { COMMENT_MAX_LENGTH, flattenComments, useAddComment, useComments } from './comments'

// Mirrors the server: trimmed text must be non-empty and at most 2000 characters.
const commentSchema = z.object({
  body: z
    .string()
    .trim()
    .min(1, 'Write a comment before posting.')
    .max(COMMENT_MAX_LENGTH, `Comments must be ${COMMENT_MAX_LENGTH} characters or fewer.`),
})

/**
 * Discussion about a repair. Separate from the activity timeline: comments are conversation, the
 * timeline is the official record of workflow changes. Posting a comment never changes the work order.
 */
export function CommentsSection({ workOrder }: { workOrder: WorkOrderDetail }) {
  const comments = useComments(workOrder.id)
  const addComment = useAddComment(workOrder.id)
  const [announcement, setAnnouncement] = useState('')
  const { register, handleSubmit, reset, setFocus, control, formState: { errors } } = useForm<z.infer<typeof commentSchema>>({
    resolver: zodResolver(commentSchema),
    defaultValues: { body: '' },
  })
  const length = useWatch({ control, name: 'body' })?.length ?? 0

  const onSubmit = handleSubmit(({ body }) => {
    if (addComment.isPending) return // never send the same comment twice
    setAnnouncement('')
    addComment.mutate(body, {
      onSuccess: () => {
        // Focus first: reset() unregisters fields until the next render, so setFocus after it would do nothing.
        setFocus('body')
        reset({ body: '' })
        setAnnouncement('Comment posted.')
      },
      // On failure the text stays in the box so nothing typed is lost.
    })
  })

  const items = flattenComments(comments.data)
  const total = comments.data?.pages[0]?.totalCount ?? 0
  const closed = workOrder.status === 'Closed'

  return (
    <section aria-labelledby="comments-heading" className="rounded-xl border border-slate-200 bg-white p-6 shadow-xs">
      <h2 id="comments-heading" className="text-lg font-semibold text-navy-900">
        Comments {comments.isSuccess && <span className="text-slate-500">({total})</span>}
      </h2>
      <p className="mt-1 text-sm text-slate-600">Questions and updates, visible to everyone who can see this work order.</p>

      {comments.isPending ? (
        <LoadingState label="Loading comments…" />
      ) : comments.isError ? (
        <div className="mt-4">
          <Alert
            tone="error"
            title="Comments could not be loaded"
            action={<Button variant="secondary" onClick={() => void comments.refetch()}>Try again</Button>}
          >
            {comments.error.message}
          </Alert>
        </div>
      ) : (
        <>
          {comments.hasNextPage && (
            <Button
              variant="ghost"
              className="mt-4"
              busy={comments.isFetchingNextPage}
              onClick={() => void comments.fetchNextPage()}
            >
              Show earlier comments
            </Button>
          )}
          {items.length === 0 ? (
            <p className="mt-4 text-sm text-slate-600">No comments yet.</p>
          ) : (
            <ol className="mt-4 space-y-4">
              {items.map((comment) => (
                <li key={comment.id} className="rounded-lg bg-slate-50 p-4">
                  <p className="flex flex-wrap items-baseline gap-x-2 text-sm">
                    <span className="font-semibold text-navy-900">{comment.author.displayName}</span>
                    <span className="text-slate-600">{roleLabels[comment.authorRole]}</span>
                    <time dateTime={comment.createdAt} className="text-slate-600">
                      {formatDateTime(comment.createdAt)}
                    </time>
                  </p>
                  {/* Rendered as text (React escapes it); pre-wrap keeps the author's line breaks. */}
                  <p className="mt-2 text-sm break-words whitespace-pre-wrap text-slate-800">{comment.body}</p>
                </li>
              ))}
            </ol>
          )}
        </>
      )}

      {closed ? (
        <p className="mt-6 rounded-md bg-slate-100 p-3 text-sm text-slate-700">
          This work order is closed, so new comments can't be added.
        </p>
      ) : (
        <form noValidate onSubmit={onSubmit} className="mt-6 space-y-3 border-t border-slate-100 pt-4">
          {addComment.isError && (
            <Alert tone="error" title="Your comment was not posted">
              {addComment.error.message} Your text is still in the box below.
            </Alert>
          )}
          <FormField
            label="Add a comment"
            hint={`Plain text, up to ${COMMENT_MAX_LENGTH.toLocaleString('en-CA')} characters (${length.toLocaleString('en-CA')} used).`}
            error={errors.body?.message}
          >
            <textarea rows={3} maxLength={COMMENT_MAX_LENGTH} className={inputClass} {...register('body')} />
          </FormField>
          <div className="flex items-center gap-3">
            <Button type="submit" busy={addComment.isPending}>
              {addComment.isPending ? 'Posting…' : 'Post comment'}
            </Button>
            <p role="status" className="text-sm font-medium text-teal-800">
              {announcement}
            </p>
          </div>
        </form>
      )}
    </section>
  )
}
