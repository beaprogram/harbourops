import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import type { AttachmentItem, WorkOrderDetail } from '../../api/types'
import { Button } from '../../components/Button'
import { Alert, LoadingState } from '../../components/Feedback'
import { FormField } from '../../components/FormField'
import { formatDateTime, roleLabels } from '../../lib/format'
import { formatBytes, useAttachments, useUploadAttachment } from './attachments'

const ACCEPT = 'image/jpeg,image/png'

/**
 * Photos of the problem or the finished repair. The gallery loads only small previews; the full-size
 * image is fetched when a photo is opened. Every image request is authorised by the API, so these
 * URLs are useless to anyone who can't see the work order.
 */
export function AttachmentsSection({ workOrder }: { workOrder: WorkOrderDetail }) {
  const attachments = useAttachments(workOrder.id)
  const [openIndex, setOpenIndex] = useState<number | null>(null)
  const thumbnailRefs = useRef<Array<HTMLButtonElement | null>>([])

  const items = attachments.data?.items ?? []
  const limits = attachments.data?.limits

  return (
    <section aria-labelledby="photos-heading" className="rounded-xl border border-slate-200 bg-white p-6 shadow-xs">
      <h2 id="photos-heading" className="text-lg font-semibold text-navy-900">
        Photos{' '}
        {limits && (
          <span className="text-slate-600">
            ({items.length} of {limits.maxPerWorkOrder})
          </span>
        )}
      </h2>
      <p className="mt-1 text-sm text-slate-600">
        Photos of the problem or the finished repair, visible to everyone who can see this work order.
      </p>

      {attachments.isPending ? (
        <LoadingState label="Loading photos…" />
      ) : attachments.isError ? (
        <div className="mt-4">
          <Alert
            tone="error"
            title="Photos could not be loaded"
            action={<Button variant="secondary" onClick={() => void attachments.refetch()}>Try again</Button>}
          >
            {attachments.error.message}
          </Alert>
        </div>
      ) : (
        <>
          {items.length === 0 ? (
            <p className="mt-4 text-sm text-slate-600">No photos yet.</p>
          ) : (
            <ul className="mt-4 grid grid-cols-2 gap-3 sm:grid-cols-3">
              {items.map((item, index) => (
                <li key={item.id}>
                  <button
                    type="button"
                    ref={(element) => {
                      thumbnailRefs.current[index] = element
                    }}
                    onClick={() => setOpenIndex(index)}
                    className="group block w-full overflow-hidden rounded-lg border border-slate-200 bg-slate-50 text-left hover:border-teal-600"
                  >
                    <img
                      src={item.previewUrl}
                      alt={photoAlt(item, index, items.length)}
                      width={item.previewWidth}
                      height={item.previewHeight}
                      loading="lazy"
                      className="aspect-[4/3] w-full object-cover"
                    />
                    <span className="block px-2 py-1.5 text-xs text-slate-700">
                      {item.uploadedBy.displayName} · {formatDateTime(item.uploadedAt)}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}

          {attachments.data.canUpload ? (
            <UploadForm
              workOrderId={workOrder.id}
              maxFileSizeBytes={attachments.data.limits.maxFileSizeBytes}
              remaining={attachments.data.limits.maxPerWorkOrder - items.length}
            />
          ) : (
            attachments.data.cannotUploadReason && (
              <p className="mt-6 rounded-md bg-slate-100 p-3 text-sm text-slate-700">{attachments.data.cannotUploadReason}</p>
            )
          )}
        </>
      )}

      {openIndex !== null && items[openIndex] && (
        <PhotoDialog
          items={items}
          index={openIndex}
          onNavigate={setOpenIndex}
          onClose={() => {
            const trigger = thumbnailRefs.current[openIndex]
            setOpenIndex(null)
            trigger?.focus() // return focus to the photo that was opened
          }}
        />
      )}
    </section>
  )
}

function photoAlt(item: AttachmentItem, index: number, total: number) {
  return `Photo ${index + 1} of ${total}, ${item.fileName}, added by ${item.uploadedBy.displayName}`
}

function UploadForm({ workOrderId, maxFileSizeBytes, remaining }: { workOrderId: string; maxFileSizeBytes: number; remaining: number }) {
  const upload = useUploadAttachment(workOrderId)
  const inputRef = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [clientError, setClientError] = useState<string>()
  const [announcement, setAnnouncement] = useState('')

  const choose = (chosen: File | null) => {
    setAnnouncement('')
    upload.reset()
    if (chosen && !ACCEPT.split(',').includes(chosen.type)) {
      // A quick hint only; the server checks the actual file content.
      setClientError('Choose a JPEG or PNG photo.')
      setFile(null)
      return
    }

    if (chosen && chosen.size > maxFileSizeBytes) {
      setClientError(`This photo is ${formatBytes(chosen.size)}. Photos must be ${formatBytes(maxFileSizeBytes)} or smaller.`)
      setFile(null)
      return
    }

    setClientError(undefined)
    setFile(chosen)
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!file) {
      setClientError('Choose a photo to upload.')
      inputRef.current?.focus()
      return
    }

    if (upload.isPending) return // never send the same photo twice
    upload.mutate(file, {
      onSuccess: () => {
        setFile(null)
        if (inputRef.current) inputRef.current.value = ''
        setAnnouncement('Photo uploaded.')
        inputRef.current?.focus()
      },
      // On failure the chosen file and its preview stay, so the user can retry or pick another.
    })
  }

  return (
    <form noValidate onSubmit={submit} className="mt-6 space-y-3 border-t border-slate-100 pt-4">
      {upload.isError && (
        <Alert tone="error" title="The photo was not uploaded">
          {upload.error.message}
        </Alert>
      )}
      <FormField
        label="Add a photo"
        hint={`JPEG or PNG, up to ${formatBytes(maxFileSizeBytes)}. ${remaining} ${remaining === 1 ? 'photo' : 'photos'} remaining. Location and camera details are removed when the photo is saved.`}
        error={clientError}
      >
        <input
          ref={inputRef}
          type="file"
          accept={ACCEPT}
          disabled={upload.isPending}
          onChange={(event) => choose(event.target.files?.[0] ?? null)}
          className="block w-full text-sm text-slate-700 file:mr-3 file:rounded-md file:border-0 file:bg-navy-50 file:px-3 file:py-2 file:font-semibold file:text-navy-800 hover:file:bg-navy-100"
        />
      </FormField>

      {file && (
        <div className="flex items-start gap-3 rounded-md bg-slate-50 p-3">
          <FilePreview file={file} />
          <div className="min-w-0 text-sm">
            <p className="font-medium break-words text-navy-900">{file.name}</p>
            <p className="text-slate-600">{formatBytes(file.size)}</p>
            <Button
              variant="ghost"
              className="mt-1 min-h-8! px-2!"
              disabled={upload.isPending}
              onClick={() => {
                choose(null)
                if (inputRef.current) inputRef.current.value = ''
                inputRef.current?.focus()
              }}
            >
              Remove<span className="sr-only"> {file.name}</span>
            </Button>
          </div>
        </div>
      )}

      <div className="flex items-center gap-3">
        <Button type="submit" busy={upload.isPending} disabled={!file}>
          {upload.isPending ? 'Uploading…' : 'Upload photo'}
        </Button>
        <p role="status" className="text-sm font-medium text-teal-800">
          {announcement}
        </p>
      </div>
    </form>
  )
}

/**
 * Shows the chosen file before upload. The temporary object URL is created and revoked inside an effect,
 * so it is released when the selection changes or the form unmounts (and works under StrictMode re-runs).
 */
function FilePreview({ file }: { file: File }) {
  const imageRef = useRef<HTMLImageElement>(null)
  useEffect(() => {
    const url = URL.createObjectURL(file)
    if (imageRef.current) imageRef.current.src = url
    return () => URL.revokeObjectURL(url)
  }, [file])

  return <img ref={imageRef} alt={`Preview of ${file.name}`} className="size-20 shrink-0 rounded object-cover" />
}

/**
 * Full-size view in a native modal dialog: the browser traps focus inside it and Escape closes it.
 * The full image is only requested here, never for the whole gallery.
 */
function PhotoDialog({
  items,
  index,
  onNavigate,
  onClose,
}: {
  items: AttachmentItem[]
  index: number
  onNavigate: (index: number) => void
  onClose: () => void
}) {
  const dialogRef = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const item = items[index]!

  useEffect(() => {
    const dialog = dialogRef.current
    if (dialog && !dialog.open) {
      dialog.showModal()
      // Browsers differ on what showModal() focuses; put focus on Close explicitly.
      dialog.querySelector<HTMLButtonElement>('[data-dialog-close]')?.focus()
    }
  }, [])

  return (
    <dialog
      ref={dialogRef}
      aria-labelledby={titleId}
      onClose={onClose}
      className="m-auto w-[min(100vw-2rem,64rem)] rounded-xl bg-white p-0 shadow-xl backdrop:bg-navy-950/70"
    >
      <div className="flex items-center justify-between gap-3 border-b border-slate-200 px-4 py-3">
        <h3 id={titleId} className="font-semibold text-navy-900">
          Photo {index + 1} of {items.length}
        </h3>
        <Button variant="secondary" data-dialog-close onClick={() => dialogRef.current?.close()}>
          Close
        </Button>
      </div>
      <div className="bg-slate-900 p-2">
        <img
          src={item.contentUrl}
          alt={photoAlt(item, index, items.length)}
          width={item.width}
          height={item.height}
          className="mx-auto h-auto max-h-[70vh] max-w-full object-contain"
        />
      </div>
      <div className="flex flex-col gap-3 px-4 py-3 text-sm sm:flex-row sm:items-center sm:justify-between">
        <p className="text-slate-700">
          {item.fileName} · Added by {item.uploadedBy.displayName} ({roleLabels[item.uploaderRole]}) on{' '}
          {formatDateTime(item.uploadedAt)}
        </p>
        <div className="flex flex-wrap gap-2">
          {items.length > 1 && (
            <>
              <Button variant="ghost" disabled={index === 0} onClick={() => onNavigate(index - 1)}>
                Previous photo
              </Button>
              <Button variant="ghost" disabled={index === items.length - 1} onClick={() => onNavigate(index + 1)}>
                Next photo
              </Button>
            </>
          )}
          <a
            href={`${item.contentUrl}?download=true`}
            className="inline-flex min-h-10 items-center rounded-md px-3 font-semibold text-teal-800 underline"
          >
            Download
          </a>
        </div>
      </div>
    </dialog>
  )
}
